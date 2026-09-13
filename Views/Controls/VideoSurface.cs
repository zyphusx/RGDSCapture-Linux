using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using RGDSCapture.ViewModels;

namespace RGDSCapture.Views.Controls
{
    /// <summary>
    /// Draws a screen's decoded frames.
    ///
    /// The Windows build binds an Image straight to the WriteableBitmap and
    /// lets WPF notice the writes. That does not work here: the bitmap is
    /// mutated in place, its reference never changes, and an Image has no
    /// reason to repaint. So the surface listens to
    /// <see cref="ScreenViewModel.FrameRendered"/> and invalidates itself,
    /// which also keeps repaints to exactly one per decoded frame.
    ///
    /// Rotation is handled here rather than with a layout transform, because
    /// what should rotate is the picture inside a fixed panel — not the
    /// panel, which would otherwise resize the surrounding layout every time
    /// the user turned a sideways-held game upright.
    /// </summary>
    public sealed class VideoSurface : Control
    {
        public static readonly StyledProperty<WriteableBitmap?> SourceProperty =
            AvaloniaProperty.Register<VideoSurface, WriteableBitmap?>(nameof(Source));

        public static readonly StyledProperty<BitmapInterpolationMode> InterpolationProperty =
            AvaloniaProperty.Register<VideoSurface, BitmapInterpolationMode>(
                nameof(Interpolation), BitmapInterpolationMode.None);

        public static readonly StyledProperty<double> AngleProperty =
            AvaloniaProperty.Register<VideoSurface, double>(nameof(Angle));

        public WriteableBitmap? Source
        {
            get => GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        /// <summary>Nearest-neighbour (None) keeps handheld pixel art sharp.</summary>
        public BitmapInterpolationMode Interpolation
        {
            get => GetValue(InterpolationProperty);
            set => SetValue(InterpolationProperty, value);
        }

        /// <summary>Display rotation in degrees, for sideways-held games.</summary>
        public double Angle
        {
            get => GetValue(AngleProperty);
            set => SetValue(AngleProperty, value);
        }

        static VideoSurface()
        {
            AffectsRender<VideoSurface>(SourceProperty, InterpolationProperty, AngleProperty);
        }

        private ScreenViewModel? _subscribed;

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_subscribed is not null)
                _subscribed.FrameRendered -= OnFrameRendered;

            _subscribed = DataContext as ScreenViewModel;

            if (_subscribed is not null)
                _subscribed.FrameRendered += OnFrameRendered;
        }

        protected override void OnDetachedFromVisualTree(
            VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            // The view-model outlives the window in a fullscreen hand-off, so
            // an un-removed handler would keep invalidating a dead control.
            if (_subscribed is not null)
            {
                _subscribed.FrameRendered -= OnFrameRendered;
                _subscribed = null;
            }
        }

        private void OnFrameRendered() => InvalidateVisual();

        public override void Render(DrawingContext context)
        {
            WriteableBitmap? bitmap = Source;
            if (bitmap is null) return;

            Size panel = Bounds.Size;
            var pixels = bitmap.PixelSize;
            if (panel.Width <= 0 || panel.Height <= 0 ||
                pixels.Width <= 0 || pixels.Height <= 0) return;

            double angle = ((Angle % 360) + 360) % 360;

            // At a quarter turn the picture's footprint is transposed, so the
            // fit has to be computed against the rotated extent or the image
            // overflows the panel on its long side.
            bool quarterTurn = angle is > 89 and < 91 or > 269 and < 271;
            double fitWidth = quarterTurn ? pixels.Height : pixels.Width;
            double fitHeight = quarterTurn ? pixels.Width : pixels.Height;

            double scale = Math.Min(panel.Width / fitWidth, panel.Height / fitHeight);

            var drawn = new Size(pixels.Width * scale, pixels.Height * scale);
            var centre = new Point(panel.Width / 2, panel.Height / 2);
            var destination = new Rect(
                centre.X - drawn.Width / 2,
                centre.Y - drawn.Height / 2,
                drawn.Width,
                drawn.Height);

            Matrix rotation =
                Matrix.CreateTranslation(-centre.X, -centre.Y)
                * Matrix.CreateRotation(angle * Math.PI / 180.0)
                * Matrix.CreateTranslation(centre.X, centre.Y);

            using (context.PushRenderOptions(
                       new RenderOptions { BitmapInterpolationMode = Interpolation }))
            using (context.PushTransform(rotation))
            {
                context.DrawImage(
                    bitmap,
                    new Rect(0, 0, pixels.Width, pixels.Height),
                    destination);
            }
        }
    }
}
