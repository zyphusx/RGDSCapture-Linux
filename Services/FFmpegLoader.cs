using System.IO;
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

        private static string Architecture => System.Runtime.InteropServices
            .RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "aarch64-linux-gnu",
            _ => "x86_64-linux-gnu"
        };

        /// <summary>
        /// avcodec's soname major for the FFmpeg generation these bindings
        /// were generated against. Used only to probe for a usable directory;
        /// FFmpeg.AutoGen applies the real per-library versions itself.
        /// </summary>
        private const int AvcodecMajor = 63;

        public static void EnsureRegistered()
        {
            lock (Gate)
            {
                if (_registered) return;

                string? root = SearchPaths.FirstOrDefault(HasAvcodec);

                if (root is null)
                    throw new FileNotFoundException(
                        $"FFmpeg libavcodec.so.{AvcodecMajor} was not found. " +
                        "Install FFmpeg " + AvcodecMajor switch
                        {
                            63 => "9.x",
                            _ => "matching these bindings"
                        } +
                        ", or run the Flatpak build, which bundles it.");

                ffmpeg.RootPath = root;
                _registered = true;
            }
        }

        /// <summary>
        /// True if <paramref name="dir"/> holds a libavcodec of the expected
        /// generation. The empty path is accepted unconditionally: the loader
        /// resolves it against ld.so's own search path, which is not
        /// enumerable from here, so it stands as the last-chance fallback.
        /// </summary>
        private static bool HasAvcodec(string dir)
        {
            if (dir.Length == 0) return true;

            try
            {
                return File.Exists(Path.Combine(dir, $"libavcodec.so.{AvcodecMajor}"));
            }
            catch
            {
                return false;
            }
        }
    }
}
