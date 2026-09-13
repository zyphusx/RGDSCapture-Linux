using System.IO;

namespace RGDSCapture.Core
{
    /// <summary>
    /// Well-known folders and file locations, following the XDG Base Directory
    /// spec rather than the Windows build's %APPDATA% layout.
    ///
    /// Config and state are deliberately split: settings.json is user
    /// configuration the user might reasonably back up or edit, while the
    /// crash log is disposable state. Recordings and screenshots go to the
    /// user's real media folders, resolved through xdg-user-dirs when it has
    /// been configured (which is the norm on a desktop install, and what the
    /// Flatpak's --filesystem grants are written against).
    /// </summary>
    public static class AppPaths
    {
        private const string AppFolder = "RGDSCapture";

        /// <summary>$XDG_CONFIG_HOME, or ~/.config when unset.</summary>
        private static string ConfigHome { get; } = ResolveXdgBase(
            "XDG_CONFIG_HOME", ".config");

        /// <summary>$XDG_STATE_HOME, or ~/.local/state when unset.</summary>
        private static string StateHome { get; } = ResolveXdgBase(
            "XDG_STATE_HOME", Path.Combine(".local", "state"));

        public static string SettingsDir { get; } = Path.Combine(ConfigHome, AppFolder);

        public static string SettingsFile { get; } = Path.Combine(SettingsDir, "settings.json");

        public static string StateDir { get; } = Path.Combine(StateHome, AppFolder);

        public static string CrashLogFile { get; } = Path.Combine(StateDir, "crash.log");

        public static string RecordingsDir { get; } = Path.Combine(
            UserDir("VIDEOS", "Videos"), AppFolder);

        public static string ScreenshotsDir { get; } = Path.Combine(
            UserDir("PICTURES", "Pictures"), AppFolder);

        /// <summary>
        /// Absolute path to the ffmpeg binary used for recording and remuxing.
        ///
        /// Unlike the Windows build there is no ffmpeg.exe shipped beside the
        /// app: on Linux it comes from the host, or from the LGPL FFmpeg the
        /// Flatpak manifest builds into /app/bin. Resolved once by searching
        /// PATH so callers can keep doing a plain File.Exists check.
        /// </summary>
        public static string FfmpegExe { get; } = ResolveOnPath("ffmpeg");

        public static void EnsureCreated()
        {
            Directory.CreateDirectory(SettingsDir);
            Directory.CreateDirectory(StateDir);
            Directory.CreateDirectory(RecordingsDir);
            Directory.CreateDirectory(ScreenshotsDir);
        }

        // ─────────────────────────────────────────────────────────────
        private static string Home =>
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } h
                ? h
                : "/tmp";

        private static string ResolveXdgBase(string variable, string fallbackUnderHome)
        {
            string? value = Environment.GetEnvironmentVariable(variable);

            // The spec says a relative value is invalid and must be ignored.
            return Path.IsPathRooted(value)
                ? value!
                : Path.Combine(Home, fallbackUnderHome);
        }

        /// <summary>
        /// A configured xdg-user-dirs folder (XDG_VIDEOS_DIR and friends),
        /// falling back to the conventional English folder name. .NET maps
        /// SpecialFolder.MyVideos through the same user-dirs config, so it is
        /// tried first and only the fallback is hard-coded.
        /// </summary>
        private static string UserDir(string kind, string fallbackName)
        {
            string? env = Environment.GetEnvironmentVariable($"XDG_{kind}_DIR");
            if (Path.IsPathRooted(env)) return env!;

            var special = kind == "VIDEOS"
                ? Environment.SpecialFolder.MyVideos
                : Environment.SpecialFolder.MyPictures;

            string mapped = Environment.GetFolderPath(special);

            // GetFolderPath returns "" rather than throwing when the folder is
            // not configured, and returns $HOME itself on some minimal setups
            // — neither is somewhere we should scatter recordings.
            return mapped.Length > 0 && mapped != Home
                ? mapped
                : Path.Combine(Home, fallbackName);
        }

        /// <summary>
        /// First executable match for <paramref name="name"/> on PATH, or the
        /// bare name if it is not found — callers report a missing ffmpeg far
        /// more usefully than a path-resolution failure here would.
        /// </summary>
        private static string ResolveOnPath(string name)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            foreach (string dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(dir, name);
                try
                {
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // Unreadable PATH entry — skip it.
                }
            }
            return name;
        }
    }
}
