using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RGDSCapture.Views.Controls
{
    /// <summary>
    /// Right sidebar: recording, instant replay, audio monitoring and the run
    /// timer. Purely declarative — every control binds to the MainViewModel.
    /// </summary>
    public partial class CapturePanel : UserControl
    {
        public CapturePanel() => AvaloniaXamlLoader.Load(this);
    }
}
