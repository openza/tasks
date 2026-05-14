using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Pages;

public sealed partial class SyncPage : UserControl
{
    public event RoutedEventHandler? RunSyncClicked;
    public event RoutedEventHandler? OpenSettingsClicked;
    public event RoutedEventHandler? OpenInboxClicked;

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

    public void SetSourceItems(IReadOnlyList<ProviderSourceItem> items)
    {
        SourceItemsEmptyText.Text = items.Count == 0
            ? "No connected-app tasks waiting."
            : $"{items.Count} connected-app task{(items.Count == 1 ? string.Empty : "s")} waiting in Inbox intake.";
    }

    private void OnRunSyncClicked(object sender, RoutedEventArgs e) => RunSyncClicked?.Invoke(sender, e);

    private void OnOpenSettingsClicked(object sender, RoutedEventArgs e) => OpenSettingsClicked?.Invoke(sender, e);

    private void OnOpenInboxClicked(object sender, RoutedEventArgs e) => OpenInboxClicked?.Invoke(sender, e);
}
