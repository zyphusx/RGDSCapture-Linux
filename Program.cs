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
                // Inter is bundled rather than resolved from the system: the
                // Flatpak runtime carries a minimal font set, and falling back
                // to whatever it has would reflow every panel in the UI.
                .WithInterFont()
                .LogToTrace();
    }
}
