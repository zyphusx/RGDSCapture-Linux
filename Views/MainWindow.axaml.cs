using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using RGDSCapture.Core;
using RGDSCapture.Services;
using RGDSCapture.ViewModels;
using RGDSCapture.Views.Controls;

namespace RGDSCapture.Views
{
    /// <summary>
    /// Thin shell: wires the MainViewModel to view-only concerns (window
    /// chrome, dialogs, layout grid spans, fullscreen window, Space shortcut,
    /// shutdown sequencing). All behavior lives in the view-models.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel? _vm;
        private FullScreenWindow? _fullscreen;
        private ThemeDialog? _themeDialog;
        private bool _shutdownStarted;
        private bool _shutdownComplete;

        // Caption glyphs: a single square when restorable, two offset squares
        // when already maximized.
        private static readonly Geometry MaximizeGeometry =
            Geometry.Parse("M 0.5 0.5 H 9.5 V 9.5 H 0.5 Z");
        private static readonly Geometry RestoreGeometry =
            Geometry.Parse("M 2.5 0.5 H 9.5 V 7.5 M 0.5 2.5 H 7.5 V 9.5 H 0.5 Z");

        // The UI-scale transform stops at RootBorder, so the window's own
        // minimum size — which is in unscaled window coordinates — is
        // recomputed from these baselines whenever the factor changes.
        private readonly double _baseMinWidth;
        private readonly double _baseMinHeight;

        public MainWindow() { InitializeComponent(); }

        public MainWindow(MainViewModel vm) : this()
        {
            _vm = vm;
            DataContext = vm;

            _baseMinWidth = MinWidth;
            _baseMinHeight = MinHeight;

            // Raising the minimum also grows a too-small window, so switching
            // to a larger factor makes room for itself.
            OnUiScaleChanged(UiScaleService.Current);
            UiScaleService.Changed += OnUiScaleChanged;
            Closed += (_, _) => UiScaleService.Changed -= OnUiScaleChanged;

            vm.PromptCredentials = ShowCredentialDialog;
            vm.Confirm = Confirm;
            vm.FullscreenRequested += OpenFullscreen;
            vm.ThemePickerRequested += OpenThemePicker;
            vm.PropertyChanged += OnVmPropertyChanged;

            Opened += (_, _) =>
            {
                ApplyLayout();
                ApplyPanelState();
                UpdateMaximizeGlyph();
            };
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);

            BtnMinimize.Click += (_, _) => WindowState = WindowState.Minimized;
            BtnMaximize.Click += (_, _) => ToggleMaximize();
            BtnClose.Click += (_, _) => Close();
            MnuExit.Click += (_, _) => Close();

            // Extending the client area leaves the window manager owning
            // resize and snapping, but not the drag: the title bar is our
            // content now, so moving the window is explicit.
            TitleBar.PointerPressed += OnTitleBarPressed;
            TitleBar.DoubleTapped += (_, _) => ToggleMaximize();

            PropertyChanged += (_, e) =>
            {
                if (e.Property == WindowStateProperty) UpdateMaximizeGlyph();
            };

            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
            Closing += OnClosingAsync;
        }

        // ── Window chrome ─────────────────────────────────────────
        private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
        {
            // Only a press on the bar itself, not on a menu or caption button
            // sitting in it.
            if (e.Source is not Border) return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void ToggleMaximize() =>
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void UpdateMaximizeGlyph()
        {
            bool maximized = WindowState == WindowState.Maximized;
            MaximizeGlyph.Data = maximized ? RestoreGeometry : MaximizeGeometry;
            ToolTip.SetTip(BtnMaximize, maximized ? "Restore Down" : "Maximize");
        }

        // ── UI scale ──────────────────────────────────────────────
        private void OnUiScaleChanged(double scale)
        {
            var workArea = ScreenMetrics.WorkArea(this);
            MinWidth = FitOnScreen(_baseMinWidth * scale, workArea.Width);
            MinHeight = FitOnScreen(_baseMinHeight * scale, workArea.Height);
        }

        /// <summary>
        /// Caps a scaled minimum to the monitor: 175% of the 620px floor is
        /// taller than a 1080p work area, and a window whose minimum exceeds
        /// the screen cannot be positioned sensibly at all. Content gets
        /// tighter past that point rather than the window becoming unusable.
        /// </summary>
        private static double FitOnScreen(double desired, double available)
            => Math.Min(desired, available * 0.95);

        // ── Sidebar columns ───────────────────────────────────────
        private void ApplyPanelState()
        {
            if (_vm is null) return;
            LeftPanelHost.Classes.Set("collapsed", !_vm.IsLeftPanelOpen);
            RightPanelHost.Classes.Set("collapsed", !_vm.IsRightPanelOpen);
        }

        // ── Credential dialog ─────────────────────────────────────
        private (string User, string Pass, bool Remember)? ShowCredentialDialog(string defaultUsername)
        {
            if (_vm is null) return null;

            // The view-model calls this synchronously, but every Avalonia
            // dialog is awaitable-only. Pumping a nested dispatcher frame is
            // what ShowDialog did on WPF anyway, so the shape is unchanged;
            // here it just has to be spelled out.
            var dialog = new ConnectDialog(defaultUsername, _vm.Settings.RememberCredentials);

            var frame = new Avalonia.Threading.DispatcherFrame();
            bool accepted = false;

            dialog.ShowDialog<bool>(this).ContinueWith(
                t =>
                {
                    accepted = t.Result;
                    frame.Continue = false;
                },
                TaskScheduler.FromCurrentSynchronizationContext());

            Avalonia.Threading.Dispatcher.UIThread.PushFrame(frame);

            return accepted
                ? (dialog.Username, dialog.Password, dialog.Remember)
                : null;
        }

        private bool Confirm(string message, string title)
        {
            var frame = new Avalonia.Threading.DispatcherFrame();
            bool answer = false;

            MessageDialog.ConfirmAsync(this, message, title).ContinueWith(
                t =>
                {
                    answer = t.Result;
                    frame.Continue = false;
                },
                TaskScheduler.FromCurrentSynchronizationContext());

            Avalonia.Threading.Dispatcher.UIThread.PushFrame(frame);
            return answer;
        }

        // ── Layout switching ──────────────────────────────────────
        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.Layout):
                case nameof(MainViewModel.IsSwapped):
                case nameof(MainViewModel.ScreenGap):
                    ApplyLayout();
                    break;
                case nameof(MainViewModel.IsLeftPanelOpen):
                case nameof(MainViewModel.IsRightPanelOpen):
                    ApplyPanelState();
                    break;
            }
        }

        private void ApplyLayout()
        {
            if (_vm is null) return;

            static void Place(Control el, int row, int col, int rowSpan, int colSpan, bool visible)
            {
                Grid.SetRow(el, row);
                Grid.SetColumn(el, col);
                Grid.SetRowSpan(el, rowSpan);
                Grid.SetColumnSpan(el, colSpan);
                el.IsVisible = visible;
            }

            // Swap exchanges the screens' positions in the two-screen
            // layouts; Top Only / Bottom Only stay literal.
            ScreenView first = _vm.IsSwapped ? BottomView : TopView;
            ScreenView second = _vm.IsSwapped ? TopView : BottomView;

            var gap = new Thickness(_vm.ScreenGap);
            TopView.Margin = gap;
            BottomView.Margin = gap;

            VideoGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            VideoGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);

            switch (_vm.Layout)
            {
                case LayoutMode.VerticalStack:
                    Place(first, 0, 0, 1, 2, true);
                    Place(second, 1, 0, 1, 2, true);
                    break;
                case LayoutMode.SideBySide:
                    Place(first, 0, 0, 2, 1, true);
                    Place(second, 0, 1, 2, 1, true);
                    break;
                case LayoutMode.TopOnly:
                    Place(TopView, 0, 0, 2, 2, true);
                    Place(BottomView, 0, 0, 1, 1, false);
                    break;
                case LayoutMode.BottomOnly:
                    Place(BottomView, 0, 0, 2, 2, true);
                    Place(TopView, 0, 0, 1, 1, false);
                    break;
                case LayoutMode.Hybrid:
                    // Primary screen large on the left (2/3 width), the
                    // other small in the bottom-right corner.
                    VideoGrid.ColumnDefinitions[0].Width = new GridLength(2, GridUnitType.Star);
                    Place(first, 0, 0, 2, 1, true);
                    Place(second, 1, 1, 1, 1, true);
                    break;
            }
        }

        // ── Fullscreen ────────────────────────────────────────────
        private void OpenFullscreen(ScreenViewModel screen)
        {
            if (_fullscreen != null || _vm is null) return;

            _fullscreen = new FullScreenWindow(_vm, screen);
            _fullscreen.Closed += (_, _) => _fullscreen = null;
            _fullscreen.Show(this);
        }

        // ── Theme picker ──────────────────────────────────────────
        private void OpenThemePicker()
        {
            if (_vm is null) return;

            // Modeless: presets apply live, so the user wants to see the app
            // behind the picker change as they click through them.
            if (_themeDialog != null)
            {
                _themeDialog.Activate();
                return;
            }

            _themeDialog = new ThemeDialog(_vm);
            _themeDialog.Closed += (_, _) => _themeDialog = null;
            _themeDialog.Show(this);
        }

        // ── Keyboard: Space toggles the speedrun timer ────────────
        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || _vm is null) return;

            // Not while the user is typing into a field — Space is a
            // character there, not a shortcut.
            if (FocusManager?.GetFocusedElement() is TextBox) return;

            _vm.Timer.Toggle();
            e.Handled = true;
        }

        // ── Shutdown sequencing ───────────────────────────────────
        // Closing is synchronous, but our teardown (stop recordings, SSH
        // cleanup) is async. So: cancel the first close, run teardown, then
        // close for real.
        private async void OnClosingAsync(object? sender, WindowClosingEventArgs e)
        {
            if (_shutdownComplete || _vm is null) return;

            e.Cancel = true;

            if (_vm.IsConnected && !_shutdownStarted)
            {
                bool proceed = await MessageDialog.ConfirmAsync(this,
                    "Streams are currently running on the DS.\n\n" +
                    "Closing will stop all GStreamer pipelines and disconnect SSH.\n\nExit anyway?",
                    "Confirm Exit", "Exit", "Cancel");

                if (!proceed) return;
            }

            if (_shutdownStarted) return;
            _shutdownStarted = true;

            _fullscreen?.Close();
            _themeDialog?.Close();
            await _vm.ShutdownAsync();

            _shutdownComplete = true;

            // Shut the lifetime down rather than calling Close() again: the
            // continuation can run while the original close is still in
            // flight, and closing twice from inside the handshake is exactly
            // what the Windows build had to avoid too.
            if (Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
    }
}
