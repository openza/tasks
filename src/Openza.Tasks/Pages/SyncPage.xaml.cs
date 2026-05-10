using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Openza.Tasks.Pages;

public sealed partial class SyncPage : UserControl
{
    public event RoutedEventHandler? RunSyncClicked;
    public event RoutedEventHandler? OpenSettingsClicked;

    public SyncPage()
    {
        InitializeComponent();
        SetProviderStatus(false, false);
        SetLastSyncMessage("Sync has not run in this session.");
    }

    public void SetProviderStatus(bool todoistConnected, bool microsoftConnected)
    {
        ProviderStatusText.Text =
            $"Todoist: {(todoistConnected ? "connected" : "not connected")}  |  " +
            $"Microsoft To Do: {(microsoftConnected ? "connected" : "not connected")}";
    }

    public void SetLastSyncMessage(string message)
    {
        LastSyncText.Text = message;
    }

    public void SetSyncRunning(bool isRunning)
    {
        SyncNowButton.IsEnabled = !isRunning;
        SyncProgress.IsActive = isRunning;
        SyncProgress.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRunSyncClicked(object sender, RoutedEventArgs e) => RunSyncClicked?.Invoke(sender, e);

    private void OnOpenSettingsClicked(object sender, RoutedEventArgs e) => OpenSettingsClicked?.Invoke(sender, e);
}
