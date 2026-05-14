using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Openza.Tasks.Core.Services;
using Openza.Tasks.ViewModels;
using System.Collections.ObjectModel;
using Windows.Foundation;

namespace Openza.Tasks.Pages;

public sealed partial class SettingsPage : UserControl
{
    public event SelectionChangedEventHandler? ThemeChanged;
    public event RoutedEventHandler? ConnectTodoistClicked;
    public event RoutedEventHandler? ConnectMicrosoftClicked;
    public event RoutedEventHandler? DisconnectTodoistClicked;
    public event RoutedEventHandler? DisconnectMicrosoftClicked;
    public event RoutedEventHandler? CreateBackupClicked;
    public event RoutedEventHandler? RestoreBackupClicked;
    public event RoutedEventHandler? RefreshBackupsClicked;
    public event RoutedEventHandler? ExportBackupClicked;
    public event RoutedEventHandler? RestoreSelectedBackupClicked;
    public event RoutedEventHandler? DeleteBackupClicked;
    public event RoutedEventHandler? AutoBackupToggled;
    public event RoutedEventHandler? AutoSyncToggled;
    public event RoutedEventHandler? AddSpaceClicked;
    public event TypedEventHandler<SettingsPage, string>? RenameSpaceClicked;
    public event TypedEventHandler<SettingsPage, string>? ArchiveSpaceClicked;

    public ObservableCollection<BackupInfo> Backups { get; } = [];

    public ObservableCollection<SpaceSettingsItemViewModel> Spaces { get; } = [];

    public SettingsPage()
    {
        InitializeComponent();
    }

    public string SelectedTheme => (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";

    public string TodoistToken => TodoistTokenBox.Password;

    public string MicrosoftClientId => MicrosoftClientIdBox.Text.Trim();

    public string MicrosoftTenantId => string.IsNullOrWhiteSpace(MicrosoftTenantIdBox.Text) ? "common" : MicrosoftTenantIdBox.Text.Trim();

    public BackupInfo? SelectedBackup => BackupsList.SelectedItem as BackupInfo;

    public bool AutoBackupEnabled
    {
        get => AutoBackupSwitch.IsOn;
        set => AutoBackupSwitch.IsOn = value;
    }

    public bool AutoSyncEnabled
    {
        get => AutoSyncSwitch.IsOn;
        set => AutoSyncSwitch.IsOn = value;
    }

    public string NewSpaceName => NewSpaceNameBox.Text.Trim();

    public void SetProviderStatus(bool todoistConnected, bool microsoftConnected)
    {
            SetIntegrationState(
                TodoistStatusText,
                TodoistSummaryText,
            TodoistConnectButton,
            TodoistDisconnectButton,
            todoistConnected,
            "Todoist is connected. You can update the token if it changes.",
            "Paste a Todoist API token to connect your account.",
            "Update token");

            SetIntegrationState(
                MicrosoftStatusText,
                MicrosoftSummaryText,
            MicrosoftConnectButton,
            MicrosoftDisconnectButton,
            microsoftConnected,
            "Microsoft To Do is connected. You can update the app registration settings if needed.",
            "Use a public Azure app registration client ID. Tokens stay in Windows credential storage.",
            "Update");
    }

    private static void SetIntegrationState(
        TextBlock statusText,
        TextBlock summaryText,
        Button primaryButton,
        Button disconnectButton,
        bool connected,
        string connectedSummary,
        string disconnectedSummary,
        string connectedAction)
    {
        statusText.Text = connected ? "Connected" : "Not connected";
        summaryText.Text = connected ? connectedSummary : disconnectedSummary;
        primaryButton.Content = connected ? connectedAction : "Connect";
        disconnectButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetBackups(IEnumerable<BackupInfo> backups)
    {
        Backups.Clear();
        foreach (var backup in backups)
        {
            Backups.Add(backup);
        }
    }

    public void SetSpaces(IEnumerable<SpaceSettingsItemViewModel> spaces)
    {
        Spaces.Clear();
        foreach (var space in spaces)
        {
            Spaces.Add(space);
        }
    }

    public void SelectTheme(string theme)
    {
        foreach (var item in ThemeCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), theme, StringComparison.Ordinal))
            {
                ThemeCombo.SelectedItem = item;
                return;
            }
        }
    }

    public void ClearTodoistToken() => TodoistTokenBox.Password = string.Empty;

    public void ClearNewSpaceName() => NewSpaceNameBox.Text = string.Empty;

    public SpaceSettingsItemViewModel? GetSpace(string id) =>
        Spaces.FirstOrDefault(space => string.Equals(space.Id, id, StringComparison.Ordinal));

    public void ShowSpacesMessage(string title, string message, InfoBarSeverity severity)
    {
        SpacesInfo.Title = title;
        SpacesInfo.Message = message;
        SpacesInfo.Severity = severity;
        SpacesInfo.IsOpen = true;
    }

    public void SetMicrosoftConfig(string clientId, string tenantId)
    {
        MicrosoftClientIdBox.Text = clientId;
        MicrosoftTenantIdBox.Text = string.IsNullOrWhiteSpace(tenantId) ? "common" : tenantId;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e) => ThemeChanged?.Invoke(sender, e);

    private void OnSettingsSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsSectionList.SelectedItem is ListViewItem item)
        {
            ShowSection(item.Tag?.ToString() ?? "appearance");
        }
    }

    private void ShowSection(string section)
    {
        SetSectionVisibility("AppearancePanel", section == "appearance");
        SetSectionVisibility("IntegrationsPanel", section == "integrations");
        SetSectionVisibility("SpacesPanel", section == "spaces");
        SetSectionVisibility("BackupsPanel", section == "backups");
        SetSectionVisibility("AboutPanel", section == "about");
    }

    private void SetSectionVisibility(string name, bool isVisible)
    {
        if (FindName(name) is FrameworkElement section)
        {
            section.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnConnectTodoistClicked(object sender, RoutedEventArgs e) => ConnectTodoistClicked?.Invoke(sender, e);

    private void OnConnectMicrosoftClicked(object sender, RoutedEventArgs e) => ConnectMicrosoftClicked?.Invoke(sender, e);

    private void OnDisconnectTodoistClicked(object sender, RoutedEventArgs e) => DisconnectTodoistClicked?.Invoke(sender, e);

    private void OnDisconnectMicrosoftClicked(object sender, RoutedEventArgs e) => DisconnectMicrosoftClicked?.Invoke(sender, e);

    private void OnCreateBackupClicked(object sender, RoutedEventArgs e) => CreateBackupClicked?.Invoke(sender, e);

    private void OnRestoreBackupClicked(object sender, RoutedEventArgs e) => RestoreBackupClicked?.Invoke(sender, e);

    private void OnRefreshBackupsClicked(object sender, RoutedEventArgs e) => RefreshBackupsClicked?.Invoke(sender, e);

    private void OnExportBackupClicked(object sender, RoutedEventArgs e) => ExportBackupClicked?.Invoke(sender, e);

    private void OnRestoreSelectedBackupClicked(object sender, RoutedEventArgs e) => RestoreSelectedBackupClicked?.Invoke(sender, e);

    private void OnDeleteBackupClicked(object sender, RoutedEventArgs e) => DeleteBackupClicked?.Invoke(sender, e);

    private void OnAutoBackupToggled(object sender, RoutedEventArgs e) => AutoBackupToggled?.Invoke(sender, e);

    private void OnAutoSyncToggled(object sender, RoutedEventArgs e) => AutoSyncToggled?.Invoke(sender, e);

    private void OnAddSpaceClicked(object sender, RoutedEventArgs e) => AddSpaceClicked?.Invoke(sender, e);

    private void OnRenameSpaceClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag?.ToString() is { Length: > 0 } id)
        {
            RenameSpaceClicked?.Invoke(this, id);
        }
    }

    private void OnArchiveSpaceClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag?.ToString() is { Length: > 0 } id)
        {
            ArchiveSpaceClicked?.Invoke(this, id);
        }
    }
}
