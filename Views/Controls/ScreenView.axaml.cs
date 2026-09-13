using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using RGDSCapture.Core;
using RGDSCapture.ViewModels;

namespace RGDSCapture.Views.Controls
{
    /// <summary>
    /// One screen panel: the video surface plus its health badge and stats
    /// overlay. Almost entirely declarative — everything it shows is bound to
    /// a <see cref="ScreenViewModel"/>.
    ///
    /// What is not declarative is the handful of things that reach past this
    /// control: the display options live on the MainViewModel rather than on
    /// the screen, and Avalonia has no equivalent of WPF's
    /// RelativeSource AncestorType for finding it. The live-border tint is
    /// likewise a value comparison, which Avalonia styles cannot express
    /// without a converter, so it is a property observer here instead.
    /// </summary>
    public partial class ScreenView : UserControl
    {
        private MainViewModel? _main;
        private ScreenViewModel? _screen;

        public ScreenView()
        {
            InitializeComponent();
            FullscreenButton.Click += OnFullscreenClick;
            Video.PointerPressed += OnVideoPressed;
        }

        protected override void OnAttachedToVisualTree(
            VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            _screen = DataContext as ScreenViewModel;
            if (_screen is not null)
            {
                _screen.PropertyChanged += OnScreenPropertyChanged;
                ApplyHealthBorder();
            }

            // The window's DataContext is the MainViewModel that owns this
            // screen; it is the only place the display options live.
            _main = (this.GetVisualRoot() as Window)?.DataContext as MainViewModel;
            if (_main is not null)
            {
                _main.PropertyChanged += OnMainPropertyChanged;
                ApplyDisplayOptions();
            }
        }

        protected override void OnDetachedFromVisualTree(
            VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            if (_screen is not null) _screen.PropertyChanged -= OnScreenPropertyChanged;
            if (_main is not null) _main.PropertyChanged -= OnMainPropertyChanged;
            _screen = null;
            _main = null;
        }

        private void OnScreenPropertyChanged(
            object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ScreenViewModel.Health)) ApplyHealthBorder();
        }

        private void OnMainPropertyChanged(
            object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainViewModel.ScalingMode)
                or nameof(MainViewModel.RotationAngle))
                ApplyDisplayOptions();
        }

        /// <summary>A live screen gets a subtly warmer edge.</summary>
        private void ApplyHealthBorder()
        {
            if (_screen is null) return;

            Root.BorderBrush = Services.ThemeService.Brush(
                _screen.Health == StreamHealth.Live ? "ScreenBorderLive" : "ScreenBorder");
        }

        private void ApplyDisplayOptions()
        {
            if (_main is null) return;

            Video.Interpolation = _main.ScalingMode;
            Video.Angle = _main.RotationAngle;
        }

        private void OnFullscreenClick(object? sender, RoutedEventArgs e) => RequestFullscreen();

        private void OnVideoPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(Video).Properties.IsLeftButtonPressed)
                RequestFullscreen();
        }

        private void RequestFullscreen()
        {
            if (_main is null || _screen is null) return;
            if (_main.FullscreenCommand.CanExecute(_screen))
                _main.FullscreenCommand.Execute(_screen);
        }
    }
}
