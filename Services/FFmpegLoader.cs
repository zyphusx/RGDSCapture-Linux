using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace RGDSCapture.Services
{
    /// <summary>
    /// Points FFmpeg.AutoGen at the native FFmpeg shared libraries.
    /// Thread-safe and idempotent.
    ///
    /// The Windows build ships its own DLLs next to the executable and simply
    /// sets RootPath to the app directory. Nothing is bundled here: the
    /// libraries come either from the Flatpak (which builds an LGPL FFmpeg
    /// into /app/lib so the ABI is pinned to what the bindings expect) or from
    /// the host distribution. FFmpeg.AutoGen builds its filename as
    /// "lib{name}.so.{major}", and the majors it asks for are fixed by the
    /// binding version — so a host FFmpeg of the wrong generation fails to
    /// load rather than mis-binding, and gets a legible error instead of a
    /// DllNotFoundException from somewhere deep in the decoder.
    /// </summary>
    public static class FFmpegLoader
    {
        private static readonly object Gate = new();
        private static bool _registered;

        /// <summary>
        /// Search order for the FFmpeg shared libraries. The empty entry is
        /// last and means "let the dynamic loader use its own search path",
        /// which is what picks up a distro FFmpeg in a normal /usr/lib.
        /// </summary>
        private static readonly string[] SearchPaths =
        {
            // Bundled by the Flatpak manifest.
            "/app/lib",
            // The freedesktop ffmpeg-full extension, if the manifest is
            // changed to use it instead of building FFmpeg.
            $"/usr/lib/{Architecture}/ffmpeg-full",
            // Common host layouts, for a plain `dotnet run` outside Flatpak.
            $"/usr/lib/{Architecture}",
            "/usr/lib64",
            "/usr/lib",
            "/usr/local/lib",
            string.Empty
        };

        private static string Architecture =>
            RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? "aarch64-linux-gnu"
                : "x86_64-linux-gnu";

        /// <summary>
        /// avcodec's soname major for the FFmpeg generation these bindings
        /// were generated against — 62 is FFmpeg 8.x. Used only to probe for
        /// a usable directory; FFmpeg.AutoGen applies the real per-library
        /// versions itself.
        ///
        /// This tracks the FFmpeg.AutoGen package version and must move with
        /// it: the two are generated as a pair, and a mismatch fails at
        /// runtime rather than at compile time.
        /// </summary>
        private const int AvcodecMajor = 62;

        /// <summary>Human-readable FFmpeg major matching <see cref="AvcodecMajor"/>.</summary>
        private static string FFmpegGeneration => AvcodecMajor switch
        {
            61 => "7.x",
            62 => "8.x",
            63 => "9.x",
            _ => "matching these bindings"
        };

        public static void EnsureRegistered()
        {
            lock (Gate)
            {
                if (_registered) return;

                string? root = SearchPaths.FirstOrDefault(HasAvcodec);

                if (root is null)
                    throw new FileNotFoundException(
                        $"FFmpeg libavcodec.so.{AvcodecMajor} was not found. " +
                        $"Install FFmpeg {FFmpegGeneration}, or run the Flatpak " +
                        "build, which bundles a matching one.");

                ffmpeg.RootPath = root;
                _registered = true;
            }
        }

        /// <summary>
        /// True if <paramref name="dir"/> yields a libavcodec of the expected
        /// generation.
        ///
        /// The empty path means "let the dynamic loader use its own search
        /// path", which is not enumerable from here — so rather than assume
        /// it works, ask ld.so to actually resolve the soname. Accepting it
        /// unconditionally would make this method always succeed, and the
        /// error below could never fire: a missing FFmpeg would resurface as
        /// a bare DllNotFoundException from inside the decoder, which is the
        /// thing this check exists to prevent.
        /// </summary>
        private static bool HasAvcodec(string dir)
        {
            string soname = $"libavcodec.so.{AvcodecMajor}";

            try
            {
                return dir.Length == 0
                    ? NativeLibrary.TryLoad(soname, out _)
                    : File.Exists(Path.Combine(dir, soname));
            }
            catch
            {
                return false;
            }
        }
    }
}
