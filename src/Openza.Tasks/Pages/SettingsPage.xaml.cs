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

    private string _backupFolderPath = string.Empty;
    private ListView? _restorePointDialogList;
    private ListView? _oneDriveBackupDialogList;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public string SelectedTheme => (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";

    public string TodoistToken => TodoistTokenBox.Password;

    public BackupInfo? SelectedBackup => _restorePointDialogList?.SelectedItem as BackupInfo;

    public CloudBackupInfo? SelectedCloudBackup => _oneDriveBackupDialogList?.SelectedItem as CloudBackupInfo;

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

        LatestRestorePointText.Text = Backups.FirstOrDefault()?.DisplayName ?? "No restore points yet.";
        RestorePointCountText.Text = Backups.Count == 1
            ? "1 restore point"
            : $"{Backups.Count} restore points";
    }

    public void SetBackupFolder(string path)
    {
        _backupFolderPath = path;
    }

    public void SetCloudBackupAvailable(bool available)
    {
        OneDriveBackupCard.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
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
        OneDriveBackupAccountText.Text = hasAccount ? accountUsername : "No account selected.";
        OneDriveRestoreButton.IsEnabled = enabled || hasAccount;
        if (isBusy)
        {
            OneDriveBackupStatusText.Text = "Uploading";
            OneDriveBackupSummaryText.Text = $"{accountText} Openza is updating OneDrive backups.";
            OneDrivePrimaryButton.Content = "Backing up";
            OneDrivePrimaryButton.IsEnabled = false;
        }
        else if (!enabled)
        {
            OneDriveBackupStatusText.Text = "Off";
            OneDriveBackupSummaryText.Text = $"{accountText} OneDrive backup is off. Turn it on for durable protection beyond this app install.";
            OneDrivePrimaryButton.Content = "Turn on";
            OneDrivePrimaryButton.IsEnabled = true;
        }
        else if (!hasAccount)
        {
            OneDriveBackupStatusText.Text = "Sign in required";
            OneDriveBackupSummaryText.Text = "Choose a Microsoft account for OneDrive backup.";
            OneDrivePrimaryButton.Content = "Turn on";
            OneDrivePrimaryButton.IsEnabled = true;
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            OneDriveBackupStatusText.Text = "Needs attention";
            OneDriveBackupSummaryText.Text = $"{accountText} {error}";
            OneDrivePrimaryButton.Content = "Back up now";
            OneDrivePrimaryButton.IsEnabled = true;
        }
        else
        {
            OneDriveBackupStatusText.Text = encrypted ? "Encrypted" : "Connected";
            OneDriveBackupSummaryText.Text = lastBackupAt is null
                ? $"{accountText} OneDrive backup is ready."
                : $"{accountText} Last OneDrive backup: {lastBackupAt.Value.LocalDateTime:g}.";
            OneDrivePrimaryButton.Content = "Back up now";
            OneDrivePrimaryButton.IsEnabled = true;
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
        if (FindName("SettingsContentScrollViewer") is ScrollViewer scrollViewer)
        {
            scrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        }
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

    private void OnOneDriveBackupPrimaryClicked(object sender, RoutedEventArgs e)
    {
        if (CloudBackupEnabled)
        {
            UploadOneDriveBackupClicked?.Invoke(sender, e);
            return;
        }

        OneDriveBackupSwitch.IsOn = true;
    }

    private async void OnExportBackupFileClicked(object sender, RoutedEventArgs e)
    {
        await ShowRestorePointDialogAsync(exportFirst: true);
    }

    private async void OnManageRestorePointsClicked(object sender, RoutedEventArgs e)
    {
        await ShowRestorePointDialogAsync(exportFirst: false);
    }

    private async void OnShowOneDriveRestoreDialogClicked(object sender, RoutedEventArgs e)
    {
        await ShowOneDriveRestoreDialogAsync();
    }

    private async Task ShowRestorePointDialogAsync(bool exportFirst)
    {
        var selectedDetails = new TextBlock
        {
            Text = "Select a restore point to restore, export, or delete.",
            TextWrapping = TextWrapping.Wrap,
        };

        _restorePointDialogList = new ListView
        {
            ItemsSource = Backups,
            DisplayMemberPath = nameof(BackupInfo.DisplayName),
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = 220,
            MaxHeight = 360,
        };
        if (Backups.Count > 0)
        {
            _restorePointDialogList.SelectedIndex = 0;
            selectedDetails.Text = Backups[0].DisplayName;
        }

        _restorePointDialogList.SelectionChanged += (_, _) =>
        {
            selectedDetails.Text = SelectedBackup?.DisplayName ?? "Select a restore point to restore, export, or delete.";
        };

        var dialog = new ContentDialog
        {
            Title = exportFirst ? "Export backup file" : "Manage restore points",
            CloseButtonText = "Done",
            XamlRoot = XamlRoot,
        };

        var createButton = new Button { Content = "Create" };
        createButton.Click += (_, args) => CreateBackupClicked?.Invoke(createButton, args);

        var refreshButton = new Button { Content = "Refresh" };
        refreshButton.Click += (_, args) => RefreshBackupsClicked?.Invoke(refreshButton, args);

        var openFolderButton = new Button { Content = "Open folder" };
        openFolderButton.Click += (_, args) => OpenBackupFolderClicked?.Invoke(openFolderButton, args);

        var restoreButton = new Button { Content = "Restore" };
        restoreButton.Click += (_, args) => InvokeAfterDialog(dialog, RestoreSelectedBackupClicked, restoreButton, args);

        var exportButton = new Button { Content = "Export" };
        exportButton.Click += (_, args) => InvokeAfterDialog(dialog, ExportBackupClicked, exportButton, args);

        var deleteButton = new Button { Content = "Delete" };
        deleteButton.Click += (_, args) => InvokeAfterDialog(dialog, DeleteBackupClicked, deleteButton, args);

        dialog.Content = new StackPanel
        {
            Spacing = 16,
            Width = 720,
            Children =
            {
                new TextBlock
                {
                    Text = "Restore points live inside this app package and are meant for rollback. Export one when you need a durable backup file.",
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(_backupFolderPath) ? "Restore point folder has not been loaded yet." : _backupFolderPath,
                    TextWrapping = TextWrapping.WrapWholeWords,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { createButton, refreshButton, openFolderButton },
                },
                _restorePointDialogList,
                selectedDetails,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { restoreButton, exportButton, deleteButton },
                },
            },
        };

        await dialog.ShowAsync();
    }

    private async Task ShowOneDriveRestoreDialogAsync()
    {
        var selectedDetails = new TextBlock
        {
            Text = "Select a OneDrive backup to restore.",
            TextWrapping = TextWrapping.Wrap,
        };

        _oneDriveBackupDialogList = new ListView
        {
            ItemsSource = CloudBackups,
            DisplayMemberPath = nameof(CloudBackupInfo.DisplayName),
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = 220,
            MaxHeight = 360,
        };
        if (CloudBackups.Count > 0)
        {
            _oneDriveBackupDialogList.SelectedIndex = 0;
            selectedDetails.Text = CloudBackups[0].DisplayName;
        }

        _oneDriveBackupDialogList.SelectionChanged += (_, _) =>
        {
            selectedDetails.Text = SelectedCloudBackup?.DisplayName ?? "Select a OneDrive backup to restore.";
        };

        var dialog = new ContentDialog
        {
            Title = "Restore from OneDrive",
            CloseButtonText = "Done",
            XamlRoot = XamlRoot,
        };

        var refreshButton = new Button { Content = "Refresh" };
        refreshButton.Click += (_, args) => RefreshOneDriveBackupsClicked?.Invoke(refreshButton, args);

        var restoreButton = new Button { Content = "Restore" };
        restoreButton.Click += (_, args) => InvokeAfterDialog(dialog, RestoreOneDriveBackupClicked, restoreButton, args);

        dialog.Content = new StackPanel
        {
            Spacing = 16,
            Width = 720,
            Children =
            {
                new TextBlock
                {
                    Text = "OneDrive backups are durable copies for reinstall, reset, device loss, or package removal.",
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { refreshButton, restoreButton },
                },
                _oneDriveBackupDialogList,
                selectedDetails,
            },
        };

        await dialog.ShowAsync();
    }

    private void InvokeAfterDialog(ContentDialog dialog, RoutedEventHandler? handler, object sender, RoutedEventArgs e)
    {
        dialog.Hide();
        DispatcherQueue.TryEnqueue(() => handler?.Invoke(sender, e));
    }

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
