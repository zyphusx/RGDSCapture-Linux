using System.Runtime.InteropServices;

namespace RGDSCapture.Core
{
    /// <summary>
    /// Stores the saved SSH password in the desktop keyring over the
    /// freedesktop Secret Service, via libsecret.
    ///
    /// The Windows build encrypts the password with DPAPI and keeps the
    /// ciphertext inside settings.json. That has no real equivalent here — a
    /// key kept beside its own ciphertext is obfuscation, not encryption — so
    /// the secret is handed to the keyring instead and settings.json only
    /// holds the marker token returned by <see cref="Protect"/>. The threat
    /// model is the stronger one for it: the password is encrypted at rest
    /// under the login keyring and is not readable at all while it is locked.
    ///
    /// Every failure path is non-fatal and returns null, which the callers
    /// already treat as "no saved password" — so a machine with no keyring
    /// daemon simply never remembers credentials rather than breaking.
    /// </summary>
    public static class CredentialStore
    {
        /// <summary>
        /// Written to settings.json in place of the password. Versioned so a
        /// later change of backend can recognise, and migrate, old entries.
        /// </summary>
        private const string Token = "secret-service:v1";

        private const string SchemaName = "io.github.zyphusx.RGDSCapture.Password";
        private const string AttributeKey = "credential";
        private const string AttributeValue = "ssh";
        private const string Label = "RGDSCapture SSH password";

        /// <summary>Stores <paramref name="secret"/> in the keyring. Returns the marker token, or null on failure.</summary>
        public static string? Protect(string secret)
        {
            try
            {
                return Native.Store(secret) ? Token : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Reads the password back. Returns null if absent, locked or unreadable.</summary>
        public static string? Unprotect(string token)
        {
            // Anything that is not our marker is a settings.json written by an
            // older or foreign build (e.g. a DPAPI blob copied over from the
            // Windows install); there is nothing here that can decrypt it.
            if (token != Token) return null;

            try
            {
                return Native.Lookup();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Removes the stored password. Called when the user turns off
        /// "remember credentials" — clearing only the settings field would
        /// leave the secret orphaned in the keyring.
        /// </summary>
        public static void Clear()
        {
            try
            {
                Native.Clear();
            }
            catch
            {
                // Best effort — a missing keyring has nothing to clear anyway.
            }
        }

        // ── libsecret interop ─────────────────────────────────────────
        private static class Native
        {
            private const string Secret = "libsecret-1.so.0";
            private const string Glib = "libglib-2.0.so.0";

            /// <summary>SECRET_COLLECTION_DEFAULT — the user's login keyring.</summary>
            private const string DefaultCollection = "default";

            [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr secret_password_lookupv_sync(
                IntPtr schema, IntPtr attributes, IntPtr cancellable, IntPtr error);

            [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
            private static extern bool secret_password_storev_sync(
                IntPtr schema, IntPtr attributes, string? collection, string label,
                string password, IntPtr cancellable, IntPtr error);

            [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
            private static extern bool secret_password_clearv_sync(
                IntPtr schema, IntPtr attributes, IntPtr cancellable, IntPtr error);

            [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
            private static extern void secret_password_free(IntPtr password);

            [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr equalFunc);

            [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
            private static extern void g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

            [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
            private static extern void g_hash_table_destroy(IntPtr table);

            // ── Schema ────────────────────────────────────────────────
            // SecretSchema is a public, caller-allocated struct, so it can be
            // built field by field here. That matters: every convenience
            // wrapper libsecret offers for making one (secret_schema_new) is
            // variadic, and variadic P/Invoke is not something to rely on.
            //
            //   struct _SecretSchema {
            //       const gchar *name;                      // offset  0
            //       SecretSchemaFlags flags;                // offset  8
            //       SecretSchemaAttribute attributes[32];   // offset 16
            //       ... private, zero ...
            //   };
            //   struct SecretSchemaAttribute { const gchar *name; int type; }
            //
            // The buffer is over-allocated and zeroed, which both terminates
            // the attribute array and covers libsecret's private tail.
            private const int SchemaBytes = 1024;
            private const int AttributesOffset = 16;
            private const int AttributeStride = 16;

            private static readonly Lazy<IntPtr> SchemaPtr = new(BuildSchema);

            private static IntPtr BuildSchema()
            {
                IntPtr schema = Marshal.AllocHGlobal(SchemaBytes);
                for (int i = 0; i < SchemaBytes; i += IntPtr.Size)
                    Marshal.WriteIntPtr(schema, i, IntPtr.Zero);

                Marshal.WriteIntPtr(schema, 0, Utf8(SchemaName));
                Marshal.WriteInt32(schema, 8, 0);                        // SECRET_SCHEMA_NONE
                Marshal.WriteIntPtr(schema, AttributesOffset, Utf8(AttributeKey));
                Marshal.WriteInt32(schema, AttributesOffset + 8, 0);     // ATTRIBUTE_STRING

                // attributes[1].name stays NULL, terminating the array.
                _ = AttributeStride;
                return schema;
            }

            // ── Attribute table ───────────────────────────────────────
            // g_hash_table_new with NULL destroy functions: the table never
            // owns the keys or values, so the two allocations below are freed
            // here rather than by GLib.
            private static IntPtr NewAttributes(out IntPtr key, out IntPtr value)
            {
                IntPtr table = g_hash_table_new(StrHash.Value, StrEqual.Value);
                key = Utf8(AttributeKey);
                value = Utf8(AttributeValue);
                g_hash_table_insert(table, key, value);
                return table;
            }

            private static void FreeAttributes(IntPtr table, IntPtr key, IntPtr value)
            {
                g_hash_table_destroy(table);
                Marshal.FreeHGlobal(key);
                Marshal.FreeHGlobal(value);
            }

            // g_str_hash / g_str_equal are passed to GLib as function
            // pointers, so they are resolved as data exports rather than
            // called from here.
            private static readonly Lazy<IntPtr> StrHash = new(() => Export("g_str_hash"));
            private static readonly Lazy<IntPtr> StrEqual = new(() => Export("g_str_equal"));

            private static IntPtr Export(string symbol) =>
                NativeLibrary.GetExport(NativeLibrary.Load(Glib), symbol);

            private static IntPtr Utf8(string value)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
                IntPtr p = Marshal.AllocHGlobal(bytes.Length + 1);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                Marshal.WriteByte(p, bytes.Length, 0);
                return p;
            }

            // ── Operations ────────────────────────────────────────────
            internal static bool Store(string password)
            {
                IntPtr table = NewAttributes(out IntPtr key, out IntPtr value);
                try
                {
                    return secret_password_storev_sync(
                        SchemaPtr.Value, table, DefaultCollection, Label,
                        password, IntPtr.Zero, IntPtr.Zero);
                }
                finally
                {
                    FreeAttributes(table, key, value);
                }
            }

            internal static string? Lookup()
            {
                IntPtr table = NewAttributes(out IntPtr key, out IntPtr value);
                IntPtr result = IntPtr.Zero;
                try
                {
                    result = secret_password_lookupv_sync(
                        SchemaPtr.Value, table, IntPtr.Zero, IntPtr.Zero);

                    return result == IntPtr.Zero
                        ? null
                        : Marshal.PtrToStringUTF8(result);
                }
                finally
                {
                    // Wipes the copy libsecret allocated, not just frees it.
                    if (result != IntPtr.Zero) secret_password_free(result);
                    FreeAttributes(table, key, value);
                }
            }

            internal static void Clear()
            {
                IntPtr table = NewAttributes(out IntPtr key, out IntPtr value);
                try
                {
                    secret_password_clearv_sync(
                        SchemaPtr.Value, table, IntPtr.Zero, IntPtr.Zero);
                }
                finally
                {
                    FreeAttributes(table, key, value);
                }
            }
        }
    }
}
