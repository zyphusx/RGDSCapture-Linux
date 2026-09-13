using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RGDSCapture.ViewModels;

namespace RGDSCapture.Views.Controls
{
    /// <summary>
    /// The event log panel. Its jobs beyond the XAML are to keep the view
    /// pinned to the newest entry — data binding alone cannot do that, since
    /// a ScrollViewer has no notion of "follow the tail" — and to drive the
    /// open/closed slide, which is a style class here rather than the
    /// DataTrigger storyboards the Windows build uses.
    /// </summary>
    public partial class LogDrawer : UserControl
    {
        // Tracked so the previous log can be unsubscribed. The DataContext is
        // set after construction and can be replaced later, so subscribing
        // once in the constructor would attach to nothing, and subscribing on
        // every change without detaching would leak handlers onto a log that
        // is no longer displayed.
        private LogViewModel? _attachedLog;

        public LogDrawer()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_attachedLog != null)
            {
                ((INotifyCollectionChanged)_attachedLog.Entries).CollectionChanged -= OnEntriesChanged;
                _attachedLog.PropertyChanged -= OnLogPropertyChanged;
            }

            _attachedLog = (DataContext as MainViewModel)?.Log;

            if (_attachedLog != null)
            {
                ((INotifyCollectionChanged)_attachedLog.Entries).CollectionChanged += OnEntriesChanged;
                _attachedLog.PropertyChanged += OnLogPropertyChanged;
                ApplyOpenState();
            }
        }

        private void OnLogPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LogViewModel.IsOpen)) ApplyOpenState();
        }

        private void ApplyOpenState() =>
            Drawer.Classes.Set("open", _attachedLog?.IsOpen == true);

        private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Only follow appends. The log trims its own backlog, and
            // scrolling to the end on a Remove would fight the user every
            // time they scrolled back to read something.
            if (e.Action == NotifyCollectionChangedAction.Add)
                LogScroll.ScrollToEnd();
        }
    }
}
