using Avalonia.Controls;
using Avalonia.Input;
using RGDSCapture.Services;

namespace RGDSCapture.Views
{
    /// <summary>
    /// A themed confirmation dialog, standing in for the MessageBox the
    /// Windows build uses — Avalonia has no such thing, and a system message
    /// box would not have matched the app's chrome anyway.
    /// </summary>
    public partial class MessageDialog : Window
    {
        public MessageDialog()
        {
            InitializeComponent();
            UiScaleService.TrackDialogSize(this);

            BtnYes.Click += (_, _) => Close(true);
            BtnNo.Click += (_, _) => Close(false);

            // The window is borderless, so the header stands in for a title bar.
            DragHandle.PointerPressed += OnDragHandlePressed;
        }

        private void OnDragHandlePressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        /// <summary>
        /// Asks a yes/no question. Returns false if the user cancels, closes
        /// the window, or dismisses it with Escape.
        /// </summary>
        public static async Task<bool> ConfirmAsync(
            Window owner, string message, string title,
            string confirmText = "Yes", string cancelText = "No")
        {
            var dialog = new MessageDialog();
            dialog.TitleText.Text = title;
            dialog.MessageText.Text = message;
            dialog.BtnYes.Content = confirmText;
            dialog.BtnNo.Content = cancelText;
            dialog.Title = title;

            return await dialog.ShowDialog<bool>(owner);
        }

        /// <summary>A one-button notice; the cancel button is hidden.</summary>
        public static async Task NoticeAsync(Window owner, string message, string title)
        {
            var dialog = new MessageDialog();
            dialog.TitleText.Text = title;
            dialog.MessageText.Text = message;
            dialog.BtnYes.Content = "OK";
            dialog.BtnNo.IsVisible = false;
            dialog.Title = title;

            await dialog.ShowDialog<bool>(owner);
        }
    }
}
