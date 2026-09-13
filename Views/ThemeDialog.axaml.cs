using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using RGDSCapture.Services;
using RGDSCapture.ViewModels;

namespace RGDSCapture.Views
{
    /// <summary>
    /// Theme picker: a grid of presets plus an RGB accent picker.
    ///
    /// Preset tiles apply immediately through their own command (the whole UI
    /// is DynamicResource-bound, so it re-skins live). The accent sliders only
    /// preview into the swatch until Apply is pressed — dragging a slider
    /// would otherwise rebuild the entire palette on every tick.
    /// </summary>
    public partial class ThemeDialog : Window
    {
        private readonly MainViewModel? _vm;
        private bool _syncing;

        public ThemeDialog() { InitializeComponent(); WireUp(); }

        public ThemeDialog(MainViewModel vm) : this()
        {
            _vm = vm;
            DataContext = vm;

            // Fixed width, so it has to be widened by hand for the scaled
            // content, and capped in height so a large factor cannot push
            // the buttons off-screen; modeless, so it follows later changes.
            UiScaleService.TrackDialogSize(this);

            LoadAccent(ThemeService.EffectiveAccent);

            // Picking a preset changes the accent out from under the sliders,
            // so follow the view-model rather than only reading it once.
            vm.PropertyChanged += OnVmPropertyChanged;
            Closed += (_, _) => vm.PropertyChanged -= OnVmPropertyChanged;
        }

        /// <summary>
        /// Event wiring, kept separate from the generated
        /// InitializeComponent — that is what assigns the x:Name fields, so
        /// it has to run first and must not be hidden by a hand-written one.
        /// </summary>
        private void WireUp()
        {

            BtnCloseHeader.Click += (_, _) => Close();
            BtnDone.Click += (_, _) => Close();
            BtnApplyAccent.Click += (_, _) =>
                _vm?.ApplyTheme(ThemeService.Current, CurrentColor());
            BtnClearAccent.Click += (_, _) =>
            {
                _vm?.ApplyTheme(ThemeService.Current, null);
                LoadAccent(ThemeService.EffectiveAccent);
            };

            foreach (var slider in new[] { SldR, SldG, SldB })
                slider.PropertyChanged += OnSliderPropertyChanged;

            TxtHex.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                CommitHex();
                e.Handled = true;
            };
            TxtHex.LostFocus += (_, _) => CommitHex();

            foreach (var shelf in new[] { ShelfDark, ShelfLight, ShelfPride })
                shelf.AddHandler(PointerWheelChangedEvent, OnShelfWheel,
                    Avalonia.Interactivity.RoutingStrategies.Tunnel);

            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Close();
            };

            DragHandle.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginMoveDrag(e);
            };
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.AccentHex)) return;

            // Don't yank the sliders while the user is mid-edit in the hex box.
            if (TxtHex.IsFocused) return;
            LoadAccent(ThemeService.EffectiveAccent);
        }

        // Avalonia has no ValueChanged event on Slider; Value is an ordinary
        // styled property, so the change comes through the property system.
        private void OnSliderPropertyChanged(
            object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (_syncing || e.Property != RangeBase.ValueProperty) return;
            UpdatePreview();
        }

        private void LoadAccent(Color c)
        {
            _syncing = true;
            SldR.Value = c.R;
            SldG.Value = c.G;
            SldB.Value = c.B;
            _syncing = false;
            UpdatePreview();
        }

        private Color CurrentColor() => Color.FromRgb(
            (byte)SldR.Value, (byte)SldG.Value, (byte)SldB.Value);

        private void UpdatePreview()
        {
            var c = CurrentColor();
            AccentPreview.Background = new ImmutableSolidColorBrush(c);
            if (!TxtHex.IsFocused)
                TxtHex.Text = PaletteBuilder.ToHex(c);
        }

        private void CommitHex()
        {
            string text = (TxtHex.Text ?? string.Empty).Trim();
            if (text.Length == 0) return;
            if (!text.StartsWith('#')) text = "#" + text;

            // ParseHex falls back to grey on nonsense; treat that as "leave
            // the sliders alone" rather than silently jumping to grey.
            if (text.Length is not (7 or 4)) { UpdatePreview(); return; }

            LoadAccent(PaletteBuilder.ParseHex(text));
        }

        /// <summary>
        /// The theme shelves scroll sideways, but the wheel drives vertical
        /// scrolling by default — which does nothing here — so remap it.
        /// </summary>
        private void OnShelfWheel(object? sender, PointerWheelEventArgs e)
        {
            if (sender is not ScrollViewer shelf) return;

            // One notch is 1.0 here rather than WPF's 120, so it needs a
            // step size of its own to move a sensible distance.
            const double NotchPixels = 60;

            shelf.Offset = shelf.Offset.WithX(Math.Clamp(
                shelf.Offset.X - e.Delta.Y * NotchPixels,
                0,
                Math.Max(0, shelf.Extent.Width - shelf.Viewport.Width)));

            e.Handled = true;
        }
    }
}
