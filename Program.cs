using Avalonia;

namespace RGDSCapture
{
    internal static class Program
    {
        /// <summary>
        /// Entry point. Kept free of any application logic: Avalonia's
        /// designer and its own tooling call <see cref="BuildAvaloniaApp"/>
        /// directly, so anything done here would not run for them.
        /// </summary>
        [STAThread]
        public static void Main(string[] args) =>
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(new X11PlatformOptions
                {
                    // Avalonia opens D-Bus connections at platform init for
                    // input methods, the global menu and the portal file
                    // picker. This app uses none of them: the menu bar is
                    // drawn in-window, nothing opens a file dialog, and the
                    // only text entry is an address, a port, a username, a
                    // password and a hex colour — all ASCII.
                    //
                    // They are switched off because a failure on one of those
                    // connections is not survivable. Tmds.DBus reports a
                    // disconnect by marshalling the error onto the dispatcher
                    // with a blocking Send; during startup the dispatcher
                    // loop is not pumping yet, so the Send is cancelled and
                    // the exception surfaces on a thread-pool thread, where
                    // nothing can catch it and the process dies. Inside a
                    // Flatpak, whose session bus is a filtered proxy, that is
                    // a real possibility rather than a theoretical one.
                    EnableIme = false,
                    UseDBusMenu = false,
                    UseDBusFilePicker = false,

                    // Matches StartupWMClass in the .desktop file, so the
                    // window associates with its launcher entry.
                    WmClass = "rgdscapture",
                })
                // Inter is bundled rather than resolved from the system: the
                // Flatpak runtime carries a minimal font set, and falling back
                // to whatever it has would reflow every panel in the UI.
                .WithInterFont()
                .LogToTrace();
    }
}
