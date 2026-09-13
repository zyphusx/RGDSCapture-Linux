using System.Runtime.InteropServices;

namespace RGDSCapture.Services
{
    /// <summary>An input or output device as PulseAudio reports it.</summary>
    /// <param name="Id">
    /// The PulseAudio source/sink name — the stable identifier used when
    /// opening a stream. Null means "whatever the server considers default",
    /// which is also what the user's own sound settings control.
    /// </param>
    /// <param name="Name">Human-readable description, shown in the device pickers.</param>
    public sealed record AudioDeviceInfo(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>
    /// Minimal PulseAudio binding covering what the passthrough needs:
    /// enumerate sources and sinks, and open blocking capture/playback
    /// streams.
    ///
    /// PipeWire implements the PulseAudio protocol and ships libpulse
    /// compatibility, so this is the one backend that works unmodified across
    /// PipeWire (Nobara, Fedora), plain PulseAudio, and inside a Flatpak
    /// holding only --socket=pulseaudio. Going to ALSA directly would mean
    /// grabbing the hardware device exclusively, which is wrong for an app
    /// that monitors a line input alongside everything else the desktop is
    /// playing.
    ///
    /// The simple API is a good fit here: capture and playback each run on
    /// their own thread doing blocking reads and writes, which is a closer
    /// match to the ring-buffer design than a callback API would be.
    /// </summary>
    internal static class PulseAudio
    {
        private const string Lib = "libpulse.so.0";
        private const string SimpleLib = "libpulse-simple.so.0";

        internal const int SampleS16Le = 3;      // PA_SAMPLE_S16LE
        private const int DirectionPlayback = 1; // PA_STREAM_PLAYBACK
        private const int DirectionRecord = 2;   // PA_STREAM_RECORD

        private const int ContextReady = 4;
        private const int ContextFailed = 5;
        private const int ContextTerminated = 6;
        private const int OperationRunning = 0;

        /// <summary>
        /// Ceiling on mainloop turns while enumerating, so a wedged or absent
        /// sound server degrades to an empty device list instead of hanging
        /// the caller. Each turn blocks only until the next event.
        /// </summary>
        private const int MaxIterations = 2000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SampleSpec
        {
            public int Format;
            public uint Rate;
            public byte Channels;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BufferAttr
        {
            public uint MaxLength;
            public uint TargetLength;   // playback
            public uint PreBuffer;      // playback
            public uint MinRequest;     // playback
            public uint FragmentSize;   // record

            /// <summary>(uint32_t) -1 — "let the server decide".</summary>
            internal const uint Unset = 0xFFFFFFFFu;
        }

        // ── Simple API ────────────────────────────────────────────────
        [DllImport(SimpleLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr pa_simple_new(
            string? server, string name, int dir, string? dev, string streamName,
            ref SampleSpec ss, IntPtr map, ref BufferAttr attr, out int error);

        [DllImport(SimpleLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int pa_simple_read(IntPtr s, byte[] data, nuint bytes, out int error);

        [DllImport(SimpleLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int pa_simple_write(IntPtr s, byte[] data, nuint bytes, out int error);

        [DllImport(SimpleLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int pa_simple_flush(IntPtr s, out int error);

        [DllImport(SimpleLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void pa_simple_free(IntPtr s);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr pa_strerror(int error);

        internal static string ErrorText(int code) =>
            Marshal.PtrToStringUTF8(pa_strerror(code)) ?? $"PulseAudio error {code}";

        // ── Mainloop / context, for enumeration only ──────────────────
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr pa_mainloop_new();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr pa_mainloop_get_api(IntPtr m);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int pa_mainloop_iterate(IntPtr m, int block, IntPtr retval);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void pa_mainloop_free(IntPtr m);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr pa_context_new(IntPtr api, string name);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int pa_context_connect(IntPtr c, string? server, int flags, IntPtr spawnApi);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int pa_context_get_state(IntPtr c);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void pa_context_disconnect(IntPtr c);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void pa_context_unref(IntPtr c);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr pa_context_get_source_info_list(IntPtr c, InfoCallback cb, IntPtr userdata);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr pa_context_get_sink_info_list(IntPtr c, InfoCallback cb, IntPtr userdata);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int pa_operation_get_state(IntPtr o);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void pa_operation_unref(IntPtr o);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void InfoCallback(IntPtr context, IntPtr info, int eol, IntPtr userdata);

        // pa_source_info and pa_sink_info both open with the same three
        // fields, and have for the whole life of the 1.x ABI:
        //     const char *name;        // 0
        //     uint32_t index;          // 8, then 4 bytes of padding
        //     const char *description; // 16
        // Only those two strings are needed, so the rest of either struct is
        // deliberately not described here.
        private const int InfoNameOffset = 0;
        private const int InfoDescriptionOffset = 16;

        internal static List<AudioDeviceInfo> Sources() => Enumerate(record: true);

        internal static List<AudioDeviceInfo> Sinks() => Enumerate(record: false);

        private static List<AudioDeviceInfo> Enumerate(bool record)
        {
            var found = new List<AudioDeviceInfo>();

            IntPtr mainloop = IntPtr.Zero, context = IntPtr.Zero, op = IntPtr.Zero;
            try
            {
                mainloop = pa_mainloop_new();
                if (mainloop == IntPtr.Zero) return found;

                context = pa_context_new(pa_mainloop_get_api(mainloop), "RGDSCapture");
                if (context == IntPtr.Zero) return found;

                if (pa_context_connect(context, null, 0, IntPtr.Zero) < 0) return found;

                if (!WaitForContext(mainloop, context)) return found;

                // Held in a local so the GC cannot collect the thunk while
                // PulseAudio still holds a pointer to it.
                InfoCallback callback = (_, info, eol, _) =>
                {
                    if (eol != 0 || info == IntPtr.Zero) return;

                    string? id = Marshal.PtrToStringUTF8(
                        Marshal.ReadIntPtr(info, InfoNameOffset));
                    string? description = Marshal.PtrToStringUTF8(
                        Marshal.ReadIntPtr(info, InfoDescriptionOffset));

                    if (id is null) return;
                    found.Add(new AudioDeviceInfo(id, description is { Length: > 0 } d ? d : id));
                };

                op = record
                    ? pa_context_get_source_info_list(context, callback, IntPtr.Zero)
                    : pa_context_get_sink_info_list(context, callback, IntPtr.Zero);

                if (op == IntPtr.Zero) return found;

                for (int i = 0; i < MaxIterations; i++)
                {
                    if (pa_operation_get_state(op) != OperationRunning) break;
                    if (pa_mainloop_iterate(mainloop, 1, IntPtr.Zero) < 0) break;
                }

                GC.KeepAlive(callback);
                return found;
            }
            catch (DllNotFoundException)
            {
                // No libpulse at all — the caller reports an empty list.
                return found;
            }
            catch (EntryPointNotFoundException)
            {
                return found;
            }
            finally
            {
                if (op != IntPtr.Zero) pa_operation_unref(op);
                if (context != IntPtr.Zero)
                {
                    pa_context_disconnect(context);
                    pa_context_unref(context);
                }
                if (mainloop != IntPtr.Zero) pa_mainloop_free(mainloop);
            }
        }

        private static bool WaitForContext(IntPtr mainloop, IntPtr context)
        {
            for (int i = 0; i < MaxIterations; i++)
            {
                int state = pa_context_get_state(context);
                if (state == ContextReady) return true;
                if (state is ContextFailed or ContextTerminated) return false;
                if (pa_mainloop_iterate(mainloop, 1, IntPtr.Zero) < 0) return false;
            }
            return false;
        }

        // ── Streams ───────────────────────────────────────────────────
        /// <summary>
        /// A blocking capture or playback stream. Read and Write block until
        /// the full buffer has moved, which is exactly what the dedicated
        /// audio threads want.
        /// </summary>
        internal sealed class Stream : IDisposable
        {
            private IntPtr _handle;

            private Stream(IntPtr handle) => _handle = handle;

            internal static Stream Open(
                bool record, string? device, SampleSpec spec, BufferAttr attr, string streamName)
            {
                IntPtr handle = pa_simple_new(
                    server: null,
                    name: "RGDSCapture",
                    dir: record ? DirectionRecord : DirectionPlayback,
                    dev: device,
                    streamName: streamName,
                    ss: ref spec,
                    map: IntPtr.Zero,
                    attr: ref attr,
                    error: out int error);

                if (handle == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"Could not open PulseAudio {(record ? "capture" : "playback")} " +
                        $"stream: {ErrorText(error)}");

                return new Stream(handle);
            }

            /// <summary>Fills <paramref name="buffer"/> completely. False once the stream is broken.</summary>
            internal bool Read(byte[] buffer, int count)
            {
                if (_handle == IntPtr.Zero) return false;
                return pa_simple_read(_handle, buffer, (nuint)count, out _) >= 0;
            }

            /// <summary>Writes <paramref name="count"/> bytes. False once the stream is broken.</summary>
            internal bool Write(byte[] buffer, int count)
            {
                if (_handle == IntPtr.Zero) return false;
                return pa_simple_write(_handle, buffer, (nuint)count, out _) >= 0;
            }

            /// <summary>Discards anything already buffered, so a restart does not replay stale audio.</summary>
            internal void Flush()
            {
                if (_handle != IntPtr.Zero) pa_simple_flush(_handle, out _);
            }

            public void Dispose()
            {
                IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
                if (handle != IntPtr.Zero) pa_simple_free(handle);
            }
        }
    }
}
