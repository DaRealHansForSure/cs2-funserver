using System.Windows;

namespace Cs2ServerControl.Shell;

/// <summary>
/// A small dialog in the app's own look; the system MessageBox would be the
/// only light window in a dark app.
/// </summary>
public partial class ConfirmDialog : Window {
    private ConfirmDialog(string title, string message, string okText, bool showCancel) {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        if (!showCancel) {
            CancelButton.Visibility = Visibility.Collapsed;
        }
        Loaded += (_, _) => OkButton.Focus();
    }

    public static bool Ask(string title, string message, string okText = "OK") {
        var dialog = new ConfirmDialog(title, message, okText, showCancel: true) {
            Owner = Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true;
    }

    public static void ShowMessage(Window? owner, string title, string message) {
        var dialog = new ConfirmDialog(title, message, "OK", showCancel: false);
        if (owner != null && owner.IsVisible) {
            dialog.Owner = owner;
        } else {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dialog.ShowDialog();
    }

    private void OnOk(object sender, RoutedEventArgs e) {
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) {
        DialogResult = false;
    }
}
