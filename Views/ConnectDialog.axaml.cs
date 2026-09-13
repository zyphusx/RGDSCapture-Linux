using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using RGDSCapture.Services;

namespace RGDSCapture.Views
{
    /// <summary>
    /// SSH credential prompt. Returns what the user typed via
    /// <see cref="Username"/> / <see cref="Password"/> / <see cref="Remember"/>
    /// once the dialog closes with true; the password is never stored here,
    /// only handed straight back to the caller.
    /// </summary>
    public partial class ConnectDialog : Window
    {
        public string Username { get; private set; } = string.Empty;
        public string Password { get; private set; } = string.Empty;
        public bool Remember { get; private set; }

        // Avalonia constructs windows from XAML with a parameterless
        // constructor; the real one is below.
        public ConnectDialog() : this(string.Empty) { }

        public ConnectDialog(string defaultUsername, bool defaultRemember = false)
        {
            InitializeComponent();
            UiScaleService.TrackDialogSize(this);

            TxtUsername.Text = defaultUsername;
            ChkRemember.IsChecked = defaultRemember;

            Opened += (_, _) => TxtPassword.Focus();

            BtnConnect.Click += (_, _) => TryAccept();
            BtnCancel.Click += (_, _) => Close(false);

            // The window is borderless, so the header stands in for a title bar.
            DragHandle.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginMoveDrag(e);
            };
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void TryAccept()
        {
            if (string.IsNullOrWhiteSpace(TxtUsername.Text))
            {
                ValidationText.Text = "Enter a username.";
                ValidationText.IsVisible = true;
                TxtUsername.Focus();
                return;
            }

            Username = TxtUsername.Text!.Trim();
            Password = TxtPassword.Text ?? string.Empty;
            Remember = ChkRemember.IsChecked == true;
            Close(true);
        }
    }
}
