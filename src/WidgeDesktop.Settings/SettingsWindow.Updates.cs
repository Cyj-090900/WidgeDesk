using System.Windows;

namespace WidgeDesktop.Settings;

public partial class SettingsWindow
{
    public event EventHandler? CheckForUpdatesRequested;

    public void ShowUpdateStatus(string version, string status, bool checking = false)
    {
        ProductVersionText.Text = "WidgeDesktop " + version;
        UpdateStatusText.Text = status;
        CheckUpdateButton.IsEnabled = !checking && !_userDataDeletionPending;
    }

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e) =>
        CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);
}
