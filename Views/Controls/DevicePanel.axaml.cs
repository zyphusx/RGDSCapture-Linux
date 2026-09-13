using Avalonia.Controls;

namespace RGDSCapture.Views.Controls
{
    /// <summary>
    /// Left sidebar: connection, stream quality and how the picture is
    /// arranged. Purely declarative — every control binds to the
    /// MainViewModel.
    /// </summary>
    public partial class DevicePanel : UserControl
    {
        public DevicePanel() => InitializeComponent();
    }
}
