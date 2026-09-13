using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RGDSCapture.Core;
using RGDSCapture.Services;
using RGDSCapture.ViewModels;
using RGDSCapture.Views;

namespace RGDSCapture
{
    /// <summary>
    /// Composition root: loads settings, applies the saved theme, builds the
    /// view-model graph and shows the main window. Also installs last-resort
    /// exception logging so unexpected errors land in a crash file instead of
    /// silently killing the app.
    /// </summary>
    public partial class App : Application
    {
        private MainViewModel? _vm;

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            AppPaths.EnsureCreated();

            var settingsService = new SettingsService();
            var settings = settingsService.Load();
            ThemeService.ApplyFrom(settings);

            // Publish the shared scale transform before any window resolves
            // its LayoutTransform, then set the saved factor.
            UiScaleService.Install();
            UiScaleService.ApplyFrom(settings);

            _vm = new MainViewModel(settingsService);

            InstallCrashHandlers();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow(_vm);
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void InstallCrashHandlers()
        {
            // An exception escaping a UI callback would otherwise tear the
            // process down; the Windows build marks these handled the same
            // way, on the grounds that a failed button click should not lose
            // an in-progress recording.
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                ReportCrash(args.Exception);
                args.Handled = true;
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                ReportCrash(args.Exception.InnerException ?? args.Exception);
                args.SetObserved();
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex) WriteCrashFile(ex);
            };
        }

        private void ReportCrash(Exception ex)
        {
            WriteCrashFile(ex);
            _vm?.Log.Append($"[ERROR] {ex.Message}", isError: true);
        }

        private static void WriteCrashFile(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.StateDir);
                File.AppendAllText(AppPaths.CrashLogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
            catch
            {
                // Nothing sane left to do if even crash logging fails.
            }
        }
    }
}
