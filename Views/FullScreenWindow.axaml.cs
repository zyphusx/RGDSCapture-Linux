using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RGDSCapture.ViewModels;

namespace RGDSCapture.Views
{
    /// <summary>
    /// Binding context for the fullscreen window: the one screen being shown,
    /// plus the main view-model the overlay's restart shortcuts act on.
    /// </summary>
    public sealed record FullscreenContext(MainViewModel Main, ScreenViewModel Screen);

    /// <summary>
    /// Borderless fullscreen view of one screen. Shares the same
    /// WriteableBitmap as the main window, so frames appear in both with no
    /// extra copies. Esc or double-click closes.
    /// </summary>
    public partial class FullScreenWindow : Window
    {
        /// <summary>How long the chrome stays up before its first fade.</summary>
        private static readonly TimeSpan InitialChromeDelay = TimeSpan.FromSeconds(3);

        /// <summary>Grace period after the pointer leaves, so a brief exit does not flicker.</summary>
        private static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(1.2);

        private readonly DispatcherTimer _hideTimer = new();
        private MainViewModel? _main;

        public FullScreenWindow() { InitializeComponent(); }

        public FullScreenWindow(MainViewModel main, ScreenViewModel screen) : this()
        {
            _main = main;
            DataContext = new FullscreenContext(main, screen);

            Video.Interpolation = main.ScalingMode;
            Video.Angle = main.RotationAngle;
            main.PropertyChanged += OnMainPropertyChanged;
            Closed += (_, _) => main.PropertyChanged -= OnMainPropertyChanged;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);

            BtnClose.Click += (_, _) => Close();

            _hideTimer.Tick += (_, _) =>
            {
                _hideTimer.Stop();
                SetChromeVisible(false);
            };

            // The chrome starts visible and fades once, which no selector can
            // express — "three seconds after opening" is not a state.
            Opened += (_, _) => RestartHideTimer(InitialChromeDelay);

            Root.PointerMoved += (_, _) =>
            {
                SetChromeVisible(true);
                RestartHideTimer(HideDelay);
            };
            Root.PointerExited += (_, _) => RestartHideTimer(HideDelay);

            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Close();
                    e.Handled = true;
                }
            };

            Root.DoubleTapped += (_, _) => Close();
        }

        private void OnMainPropertyChanged(
            object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_main is null) return;

            if (e.PropertyName == nameof(MainViewModel.ScalingMode))
                Video.Interpolation = _main.ScalingMode;
            else if (e.PropertyName == nameof(MainViewModel.RotationAngle))
                Video.Angle = _main.RotationAngle;
        }

        private void RestartHideTimer(TimeSpan delay)
        {
            _hideTimer.Stop();
            _hideTimer.Interval = delay;
            _hideTimer.Start();
        }

        private void SetChromeVisible(bool visible)
        {
            Chrome.Classes.Set("shown", visible);
            Chrome.Classes.Set("hidden", !visible);
        }
    }
}
