using Avalonia.Media;
using Avalonia.Media.Immutable;
using RGDSCapture.Core;
using RGDSCapture.Services;

namespace RGDSCapture.ViewModels
{
    /// <summary>
    /// One entry in the theme picker: the preset plus the few swatch brushes
    /// the tile needs to preview it without applying it.
    /// </summary>
    public sealed class ThemeChoice : ObservableObject, IMenuChoice
    {
        private readonly Func<ThemePreset, bool> _isActive;

        public ThemeChoice(ThemePreset preset, Func<ThemePreset, bool> isActive, RelayCommand apply)
        {
            Preset = preset;
            _isActive = isActive;
            ApplyCommand = apply;

            StripeSwatch = PaletteBuilder.StripeBrush(preset.Stripes);
            HasStripes = StripeSwatch != null;

            var accent = PaletteBuilder.ParseHex(preset.Accent);
            var (h, s, _) = PaletteBuilder.ToHsl(accent);
            double tintHue = preset.TintHue ?? h;
            double tint = preset.TintStrength;

            // Mirror the ramp PaletteBuilder uses, so a tile actually looks
            // like the theme it applies.
            AccentSwatch = Swatch(accent);
            if (preset.IsDark)
            {
                SurfaceSwatch = Swatch(PaletteBuilder.FromHsl(tintHue, tint * 0.28, 0.082));
                RaisedSwatch = Swatch(PaletteBuilder.FromHsl(tintHue, tint * 0.28, 0.150));
                TextSwatch = Swatch(PaletteBuilder.FromHsl(tintHue, tint * 0.14, 0.905));
            }
            else
            {
                SurfaceSwatch = Swatch(PaletteBuilder.FromHsl(tintHue, tint * 0.24, 0.995));
                RaisedSwatch = Swatch(PaletteBuilder.FromHsl(tintHue, tint * 0.24, 0.900));
                TextSwatch = Swatch(PaletteBuilder.FromHsl(tintHue, tint * 0.14, 0.090));
            }
        }

        public ThemePreset Preset { get; }
        public string Name => Preset.Name;
        public string Id => Preset.Id;
        public bool IsDark => Preset.IsDark;

        public IBrush AccentSwatch { get; }
        public IBrush SurfaceSwatch { get; }
        public IBrush RaisedSwatch { get; }
        public IBrush TextSwatch { get; }

        /// <summary>The flag's stripes, or null for a plain theme.</summary>
        public IBrush? StripeSwatch { get; }
        public bool HasStripes { get; }

        public RelayCommand ApplyCommand { get; }

        public bool IsActive => _isActive(Preset);
        public void RaiseIsActive() => OnPropertyChanged(nameof(IsActive));

        // Named for what it is rather than for WPF's Freeze(): a swatch is
        // built once and shared by the tile that shows it.
        private static IBrush Swatch(Color c) => new ImmutableSolidColorBrush(c);
    }
}
