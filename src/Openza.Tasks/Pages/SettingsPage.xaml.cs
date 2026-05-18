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
    public event RoutedEventHandler? OpenBackupFolderClicked;
    public event RoutedEventHandler? ExportBackupClicked;
    public event RoutedEventHandler? RestoreSelectedBackupClicked;
    public event RoutedEventHandler? DeleteBackupClicked;
    public event RoutedEventHandler? AutoBackupToggled;
    public event RoutedEventHandler? OneDriveBackupToggled;
    public event RoutedEventHandler? OneDriveEncryptionToggled;
    public event RoutedEventHandler? UploadOneDriveBackupClicked;
    public event RoutedEventHandler? RefreshOneDriveBackupsClicked;
    public event RoutedEventHandler? RestoreOneDriveBackupClicked;
    public event RoutedEventHandler? ChangeOneDriveAccountClicked;
    public event RoutedEventHandler? ChangeOneDrivePassphraseClicked;
    public event RoutedEventHandler? AutoSyncToggled;
    public event RoutedEventHandler? AddSpaceClicked;
    public event TypedEventHandler<SettingsPage, string>? RenameSpaceClicked;
    public event TypedEventHandler<SettingsPage, string>? ArchiveSpaceClicked;

    public ObservableCollection<BackupInfo> Backups { get; } = [];

    public ObservableCollection<CloudBackupInfo> CloudBackups { get; } = [];

    public ObservableCollection<SpaceSettingsItemViewModel> Spaces { get; } = [];

    public SettingsPage()
    {
        InitializeComponent();
    }

    public string SelectedTheme => (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";

    public string TodoistToken => TodoistTokenBox.Password;

    public BackupInfo? SelectedBackup => BackupsList.SelectedItem as BackupInfo;

    public CloudBackupInfo? SelectedCloudBackup => OneDriveBackupsList.SelectedItem as CloudBackupInfo;

    public bool AutoBackupEnabled
    {
        get => AutoBackupSwitch.IsOn;
        set => AutoBackupSwitch.IsOn = value;
    }

    public bool CloudBackupEnabled
    {
        get => OneDriveBackupSwitch.IsOn;
        set => OneDriveBackupSwitch.IsOn = value;
    }

    public bool CloudBackupEncryptionEnabled
    {
        get => OneDriveEncryptionSwitch.IsOn;
        set => OneDriveEncryptionSwitch.IsOn = value;
    }

    public bool AutoSyncEnabled
    {
        get => AutoSyncSwitch.IsOn;
        set => AutoSyncSwitch.IsOn = value;
    }

    public string NewSpaceName => NewSpaceNameBox.Text.Trim();

    public void SetProviderStatus(bool todoistConnected, string? microsoftAccountUsername)
    {
        var microsoftConnected = !string.IsNullOrWhiteSpace(microsoftAccountUsername);
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
            $"Connected as {microsoftAccountUsername}.",
            "Choose a Microsoft account for Microsoft To Do.",
            "Change account");
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

    public void SetBackupFolder(string path)
    {
        BackupFolderText.Text = path;
    }

    public void SetCloudBackupAvailable(bool available)
    {
        OneDriveBackupExpander.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetCloudBackupStatus(
        bool enabled,
        bool encrypted,
        bool isBusy,
        string? accountUsername,
        DateTimeOffset? lastBackupAt,
        string? error)
    {
        var hasAccount = !string.IsNullOrWhiteSpace(accountUsername);
        var accountText = hasAccount
            ? $"Backup account: {accountUsername}."
            : "No backup account selected.";
        if (isBusy)
        {
            OneDriveBackupStatusText.Text = "Uploading";
            OneDriveBackupSummaryText.Text = $"{accountText} Openza is updating OneDrive backups.";
        }
        else if (!enabled)
        {
            OneDriveBackupStatusText.Text = "Off";
            OneDriveBackupSummaryText.Text = $"{accountText} OneDrive backup is off. Turn it on for durable protection beyond this app install.";
        }
        else if (!hasAccount)
        {
            OneDriveBackupStatusText.Text = "Sign in required";
            OneDriveBackupSummaryText.Text = "Choose a Microsoft account for OneDrive backup.";
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            OneDriveBackupStatusText.Text = "Needs attention";
            OneDriveBackupSummaryText.Text = $"{accountText} {error}";
        }
        else
        {
            OneDriveBackupStatusText.Text = encrypted ? "Encrypted" : "Connected";
            OneDriveBackupSummaryText.Text = lastBackupAt is null
                ? $"{accountText} OneDrive backup is ready."
                : $"{accountText} Last OneDrive backup: {lastBackupAt.Value.LocalDateTime:g}.";
        }

        OneDriveBackupDetailText.Text = encrypted
            ? "Backups are encrypted before upload. Restore on a new PC requires the passphrase."
            : "Files are protected by your Microsoft account and OneDrive, not by Openza end-to-end encryption.";
    }

    public void SetCloudBackups(IEnumerable<CloudBackupInfo> backups)
    {
        CloudBackups.Clear();
        foreach (var backup in backups)
        {
            CloudBackups.Add(backup);
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
        SettingsContentScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
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

    private void OnOpenBackupFolderClicked(object sender, RoutedEventArgs e) => OpenBackupFolderClicked?.Invoke(sender, e);

    private void OnExportBackupClicked(object sender, RoutedEventArgs e) => ExportBackupClicked?.Invoke(sender, e);

    private void OnRestoreSelectedBackupClicked(object sender, RoutedEventArgs e) => RestoreSelectedBackupClicked?.Invoke(sender, e);

    private void OnDeleteBackupClicked(object sender, RoutedEventArgs e) => DeleteBackupClicked?.Invoke(sender, e);

    private void OnAutoBackupToggled(object sender, RoutedEventArgs e) => AutoBackupToggled?.Invoke(sender, e);

    private void OnOneDriveBackupToggled(object sender, RoutedEventArgs e) => OneDriveBackupToggled?.Invoke(sender, e);

    private void OnOneDriveEncryptionToggled(object sender, RoutedEventArgs e) => OneDriveEncryptionToggled?.Invoke(sender, e);

    private void OnUploadOneDriveBackupClicked(object sender, RoutedEventArgs e) => UploadOneDriveBackupClicked?.Invoke(sender, e);

    private void OnRefreshOneDriveBackupsClicked(object sender, RoutedEventArgs e) => RefreshOneDriveBackupsClicked?.Invoke(sender, e);

    private void OnRestoreOneDriveBackupClicked(object sender, RoutedEventArgs e) => RestoreOneDriveBackupClicked?.Invoke(sender, e);

    private void OnChangeOneDriveAccountClicked(object sender, RoutedEventArgs e) => ChangeOneDriveAccountClicked?.Invoke(sender, e);

    private void OnChangeOneDrivePassphraseClicked(object sender, RoutedEventArgs e) => ChangeOneDrivePassphraseClicked?.Invoke(sender, e);

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
