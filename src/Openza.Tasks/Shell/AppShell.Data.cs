using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Openza.Tasks.Shell;

public sealed partial class AppShell
{
    private async void OnImportClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_ownerWindow));
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".markdown");
        picker.FileTypeFilter.Add(".txt");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        var text = await FileIO.ReadTextAsync(file);
        var parsed = MarkdownTaskParser.Parse(text);
        if (parsed.Count > 0)
        {
            try
            {
                var backupPath = await _backupService.CreateBackupAsync(BackupReasons.PreImport).ConfigureAwait(true);
                await TryUploadCloudBackupAsync(backupPath, interactive: false, showResult: false).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                ShowInfo("Import blocked", $"A safety restore point could not be created: {exception.Message}", InfoBarSeverity.Error);
                return;
            }
        }

        foreach (var imported in parsed)
        {
            await _store.UpsertTaskAsync(new TaskItem
            {
                Id = $"local_{Guid.NewGuid():N}",
                SpaceId = _currentSpaceId,
                IntegrationId = IntegrationIds.Local,
                Title = imported.Title,
                ProjectId = null,
                Status = imported.IsCompleted ? TaskItemStatus.Completed : TaskItemStatus.Inbox,
                CompletedAt = imported.IsCompleted ? DateTimeOffset.UtcNow : null,
                CreatedAt = DateTimeOffset.UtcNow,
            }).ConfigureAwait(true);
        }

        await RefreshTasksAsync().ConfigureAwait(true);
        ShowInfo("Import complete", $"Imported {parsed.Count} task{(parsed.Count == 1 ? string.Empty : "s")}.", InfoBarSeverity.Success);
    }

    private async void OnExportClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_ownerWindow));
        picker.SuggestedFileName = "openza-tasks-export";
        picker.FileTypeChoices.Add("Markdown", [".md"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var tasks = await _store.GetTasksAsync(new TaskQuery { SpaceId = _currentSpaceId }).ConfigureAwait(true);
        var projects = await _store.GetProjectsAsync(_currentSpaceId, includeArchived: true).ConfigureAwait(true);
        var labels = await _store.GetLabelsAsync().ConfigureAwait(true);
        await FileIO.WriteTextAsync(file, MarkdownExporter.Export(tasks, projects, labels));
        ShowInfo("Export complete", file.Path, InfoBarSeverity.Success);
    }

    private async void OnSyncClicked(object sender, RoutedEventArgs e)
    {
        await RunSyncAsync(showNoIntegrations: true, showCompletionInfo: true).ConfigureAwait(true);
    }

    private async Task RunAutomaticSyncAsync()
    {
        if (!_settings.Settings.AutoSyncEnabled)
        {
            return;
        }

        await RunSyncAsync(showNoIntegrations: false, showCompletionInfo: false).ConfigureAwait(true);
    }

    private async Task RunSyncAsync(bool showNoIntegrations, bool showCompletionInfo)
    {
        if (_syncInProgress)
        {
            if (showCompletionInfo)
            {
                ShowInfo("Sync already running", "Openza is already refreshing connected apps.", InfoBarSeverity.Informational);
            }

            return;
        }

        _syncInProgress = true;
        SyncPage.SetSyncRunning(true);
        try
        {
            var summaries = new List<SyncSummary>();
            var todoistToken = await _credentials.GetAsync(TodoistTokenKey).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(todoistToken))
            {
                summaries.Add(await _syncEngine.SyncAsync(new TodoistProvider(_httpClient, todoistToken)).ConfigureAwait(true));
            }

            if (_settings.Settings.MicrosoftToDoAccount.IsConnected)
            {
                var microsoftResult = await _microsoftAuth.GetAccessTokenAsync(
                    MicrosoftGraphFeature.MicrosoftToDo,
                    _settings.Settings.MicrosoftToDoAccount,
                    MicrosoftGraphAuthService.MicrosoftToDoScopes,
                    interactiveIfNeeded: false).ConfigureAwait(true);
                if (microsoftResult is not null)
                {
                    _settings.Settings.MicrosoftToDoAccount = microsoftResult.Account;
                    await _settings.SaveAsync().ConfigureAwait(true);
                    summaries.Add(await _syncEngine.SyncAsync(new MicrosoftToDoProvider(_httpClient, microsoftResult.AccessToken)).ConfigureAwait(true));
                }
                else
                {
                    await _settings.SaveAsync().ConfigureAwait(true);
                    summaries.Add(new SyncSummary(IntegrationIds.MicrosoftToDo, false, 0, 0, 0, 0, 0, 0, _settings.Settings.MicrosoftToDoAccount.LastAuthStatus));
                }
            }

            await LoadProjectsAsync().ConfigureAwait(true);
            if (IsTaskView(_currentView))
            {
                await RefreshTasksAsync().ConfigureAwait(true);
            }
            await RefreshSourceItemsAsync().ConfigureAwait(true);

            if (summaries.Count == 0)
            {
                if (showNoIntegrations)
                {
                    ShowInfo("No integrations connected", "Connect Todoist or Microsoft To Do in Settings.", InfoBarSeverity.Warning);
                }

                SyncPage.SetLastSyncMessage("No integrations are connected.");
                return;
            }

            var failures = summaries.Where(s => !s.Success).ToList();
            var summaryText = string.Join("  ", summaries.Select(s => $"{SourceName(s.Provider)}: +{s.TasksAdded}, updated {s.TasksUpdated}, completions {s.CompletionsSynced}{(s.Success ? string.Empty : $" ({s.Error})")}"));
            SyncPage.SetLastSyncMessage($"{(failures.Count == 0 ? "Last sync completed" : "Last sync needs attention")}: {summaryText}");
            if (showCompletionInfo)
            {
                ShowInfo(
                    failures.Count == 0 ? "Sync complete" : "Sync needs attention",
                    summaryText,
                    failures.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            }
        }
        catch (Exception exception)
        {
            SyncPage.SetLastSyncMessage($"Sync failed: {exception.Message}");
            AppLog.Write(exception);
            if (showCompletionInfo)
            {
                ShowInfo("Sync failed", exception.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            _syncInProgress = false;
            SyncPage.SetSyncRunning(false);
        }
    }

    private async void OnConnectTodoistClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SettingsPage.TodoistToken))
        {
            ShowInfo("Todoist token required", "Paste your Todoist API token.", InfoBarSeverity.Warning);
            return;
        }

        await _credentials.SaveAsync(TodoistTokenKey, SettingsPage.TodoistToken).ConfigureAwait(true);
        await _store.SetIntegrationConfiguredAsync(IntegrationIds.Todoist, true).ConfigureAwait(true);
        SettingsPage.ClearTodoistToken();
        await RefreshSettingsStateAsync().ConfigureAwait(true);
        ShowInfo("Todoist connected", _settings.Settings.AutoSyncEnabled ? "Openza will sync Todoist automatically." : "Use Sync to import tasks.", InfoBarSeverity.Success);
        await RunAutomaticSyncAsync().ConfigureAwait(true);
    }

    private async void OnConnectMicrosoftClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _microsoftAuth.ConnectAsync(
                MicrosoftGraphFeature.MicrosoftToDo,
                MicrosoftGraphAuthService.MicrosoftToDoScopes).ConfigureAwait(true);
            var previousAccount = _settings.Settings.MicrosoftToDoAccount;
            _settings.Settings.MicrosoftToDoAccount = result.Account;
            await _microsoftAuth.DisconnectAsync(
                    previousAccount,
                    [_settings.Settings.MicrosoftToDoAccount, _settings.Settings.OneDriveBackupAccount])
                .ConfigureAwait(true);
            await _store.SetIntegrationConfiguredAsync(IntegrationIds.MicrosoftToDo, true).ConfigureAwait(true);
            await _settings.SaveAsync().ConfigureAwait(true);
            await RefreshSettingsStateAsync().ConfigureAwait(true);
            var message = string.IsNullOrWhiteSpace(result.Username)
                ? "Microsoft To Do connected."
                : $"{result.Username} connected.";
            ShowInfo("Microsoft To Do connected", _settings.Settings.AutoSyncEnabled ? $"{message} Openza will sync automatically." : $"{message} Use Sync to import tasks.", InfoBarSeverity.Success);
            await RunAutomaticSyncAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowInfo("Microsoft sign-in failed", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnDisconnectTodoistClicked(object sender, RoutedEventArgs e)
    {
        await _credentials.RemoveAsync(TodoistTokenKey).ConfigureAwait(true);
        await _store.SetIntegrationConfiguredAsync(IntegrationIds.Todoist, false).ConfigureAwait(true);
        await RefreshSettingsStateAsync().ConfigureAwait(true);
        ShowInfo("Todoist disconnected", "Existing synced tasks stay in your local database.", InfoBarSeverity.Informational);
    }

    private async void OnDisconnectMicrosoftClicked(object sender, RoutedEventArgs e)
    {
        var disconnectedAccount = _settings.Settings.MicrosoftToDoAccount;
        _settings.Settings.MicrosoftToDoAccount = new MicrosoftGraphAccountState();
        await _microsoftAuth.DisconnectAsync(
                disconnectedAccount,
                [_settings.Settings.OneDriveBackupAccount])
            .ConfigureAwait(true);
        await _store.SetIntegrationConfiguredAsync(IntegrationIds.MicrosoftToDo, false).ConfigureAwait(true);
        await _settings.SaveAsync().ConfigureAwait(true);
        await RefreshSettingsStateAsync().ConfigureAwait(true);
        ShowInfo("Microsoft To Do disconnected", "Existing synced tasks stay in your local database.", InfoBarSeverity.Informational);
    }

    private async void OnCreateBackupClicked(object sender, RoutedEventArgs e)
    {
        var path = await _backupService.CreateBackupAsync(BackupReasons.Manual).ConfigureAwait(true);
        await RefreshBackupListAsync().ConfigureAwait(true);
        await TryUploadCloudBackupAsync(path, interactive: true, showResult: true).ConfigureAwait(true);
        if (!_settings.Settings.OneDriveBackupEnabled)
        {
            ShowInfo("Restore point created", path, InfoBarSeverity.Success);
        }
    }

    private async void OnOpenBackupFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_backupService.BackupDirectory);
            var folder = await StorageFolder.GetFolderFromPathAsync(_backupService.BackupDirectory);
            await Windows.System.Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception exception)
        {
            ShowInfo("Could not open restore point folder", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnRestoreBackupClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_ownerWindow));
        picker.FileTypeFilter.Add(".db");
        picker.FileTypeFilter.Add(".sqlite");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await RestoreBackupPathAsync(file.Path).ConfigureAwait(true);
    }

    private async void OnRefreshBackupsClicked(object sender, RoutedEventArgs e)
    {
        await RefreshBackupListAsync().ConfigureAwait(true);
    }

    private async void OnExportBackupClicked(object sender, RoutedEventArgs e)
    {
        var backup = SettingsPage.SelectedBackup;
        if (backup is null)
        {
            ShowInfo("Select a restore point", "Choose a restore point to export as a durable backup file.", InfoBarSeverity.Warning);
            return;
        }

        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_ownerWindow));
        picker.SuggestedFileName = backup.FileName;
        picker.FileTypeChoices.Add("SQLite database", [".db"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await _backupService.ExportBackupAsync(backup.Path, file.Path).ConfigureAwait(true);
        ShowInfo("Backup exported", file.Path, InfoBarSeverity.Success);
    }

    private async void OnRestoreSelectedBackupClicked(object sender, RoutedEventArgs e)
    {
        var backup = SettingsPage.SelectedBackup;
        if (backup is null)
        {
            ShowInfo("Select a restore point", "Choose a restore point to restore.", InfoBarSeverity.Warning);
            return;
        }

        await RestoreBackupPathAsync(backup.Path).ConfigureAwait(true);
    }

    private async void OnDeleteBackupClicked(object sender, RoutedEventArgs e)
    {
        var backup = SettingsPage.SelectedBackup;
        if (backup is null)
        {
            ShowInfo("Select a restore point", "Choose a restore point to delete.", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete restore point",
            Content = backup.FileName,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _backupService.DeleteBackupAsync(backup.Path).ConfigureAwait(true);
        await RefreshBackupListAsync().ConfigureAwait(true);
        ShowInfo("Restore point deleted", backup.FileName, InfoBarSeverity.Success);
    }

    private async void OnAutoBackupToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        _settings.Settings.AutoBackupEnabled = SettingsPage.AutoBackupEnabled;
        await _settings.SaveAsync().ConfigureAwait(true);
    }

    private async void OnOneDriveBackupToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        if (!IsOneDriveBackupAvailable())
        {
            ResetOneDriveBackupSwitches();
            ShowInfo("OneDrive backup unavailable", "Cloud backup is not available for this app flavor.", InfoBarSeverity.Warning);
            return;
        }

        if (!SettingsPage.CloudBackupEnabled)
        {
            _settings.Settings.OneDriveBackupEnabled = false;
            await _settings.SaveAsync().ConfigureAwait(true);
            RefreshCloudBackupStatus(isBusy: false);
            ShowInfo("OneDrive backup off", "Local restore points continue to work normally.", InfoBarSeverity.Informational);
            return;
        }

        try
        {
            await EnsureOneDriveAccessAsync(interactive: true).ConfigureAwait(true);
            if (_settings.Settings.OneDriveBackupEncryptionEnabled &&
                string.IsNullOrWhiteSpace(await _credentials.GetAsync(OneDrivePassphraseKey).ConfigureAwait(true)))
            {
                var passphrase = await PromptForPassphraseAsync("Set cloud backup passphrase", confirm: true).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(passphrase))
                {
                    throw new InvalidOperationException("A passphrase is required for encrypted OneDrive backup.");
                }

                await _credentials.SaveAsync(OneDrivePassphraseKey, passphrase).ConfigureAwait(true);
            }

            _settings.Settings.OneDriveBackupEnabled = true;
            _settings.Settings.LastOneDriveBackupError = string.Empty;
            await _settings.SaveAsync().ConfigureAwait(true);
            RefreshCloudBackupStatus(isBusy: true);
            await TryUploadPendingCloudBackupsAsync(interactive: true, showResult: true).ConfigureAwait(true);
            await RefreshCloudBackupListAsync(interactive: true, showResult: false).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _settings.Settings.OneDriveBackupEnabled = false;
            _settings.Settings.LastOneDriveBackupError = exception.Message;
            await _settings.SaveAsync().ConfigureAwait(true);
            ResetOneDriveBackupSwitches();
            RefreshCloudBackupStatus(isBusy: false);
            ShowInfo("OneDrive backup not enabled", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnOneDriveEncryptionToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        if (SettingsPage.CloudBackupEncryptionEnabled)
        {
            var passphrase = await PromptForPassphraseAsync("Set cloud backup passphrase", confirm: true).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(passphrase))
            {
                _suppressSettingsEvents = true;
                SettingsPage.CloudBackupEncryptionEnabled = false;
                _suppressSettingsEvents = false;
                return;
            }

            await _credentials.SaveAsync(OneDrivePassphraseKey, passphrase).ConfigureAwait(true);
            _settings.Settings.OneDriveBackupEncryptionEnabled = true;
            await _settings.SaveAsync().ConfigureAwait(true);
            RefreshCloudBackupStatus(isBusy: false);
            ShowInfo("Cloud backup encryption on", "Remember this passphrase. A new PC needs it to restore encrypted backups.", InfoBarSeverity.Success);
        }
        else
        {
            await _credentials.RemoveAsync(OneDrivePassphraseKey).ConfigureAwait(true);
            _settings.Settings.OneDriveBackupEncryptionEnabled = false;
            await _settings.SaveAsync().ConfigureAwait(true);
            RefreshCloudBackupStatus(isBusy: false);
            ShowInfo("Cloud backup encryption off", "Future cloud backups will rely on your Microsoft account and OneDrive protection.", InfoBarSeverity.Informational);
        }
    }

    private async void OnUploadOneDriveBackupClicked(object sender, RoutedEventArgs e)
    {
        if (!EnsureCloudBackupEnabledForAction())
        {
            return;
        }

        try
        {
            var path = await _backupService.CreateBackupAsync(BackupReasons.Manual).ConfigureAwait(true);
            await RefreshBackupListAsync().ConfigureAwait(true);
            await TryUploadCloudBackupAsync(path, interactive: true, showResult: true).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowInfo("Cloud upload failed", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnRefreshOneDriveBackupsClicked(object sender, RoutedEventArgs e)
    {
        if (!EnsureOneDriveAvailableForAction())
        {
            return;
        }

        await RefreshCloudBackupListAsync(interactive: true, showResult: true).ConfigureAwait(true);
    }

    private async void OnRestoreOneDriveBackupClicked(object sender, RoutedEventArgs e)
    {
        if (!EnsureOneDriveAvailableForAction())
        {
            return;
        }

        var backup = SettingsPage.SelectedCloudBackup;
        if (backup is null)
        {
            ShowInfo("Select a cloud backup", "Choose a OneDrive backup to restore.", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Restore OneDrive backup",
            Content = "This will download the selected backup and replace the current local database. A local pre-restore restore point is created first.",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var passphrase = await GetPassphraseForRestoreAsync(backup).ConfigureAwait(true);
            if (string.Equals(backup.EncryptionMode, CloudBackupEncryptionModes.Passphrase, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(passphrase))
            {
                return;
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"openza-onedrive-restore-{Guid.NewGuid():N}.db");
            var restoreStartedAt = DateTime.Now;
            try
            {
                await CreateCloudBackupService(interactive: true).DownloadBackupAsync(backup, tempPath, passphrase).ConfigureAwait(true);
                await _backupService.RestoreBackupAsync(tempPath).ConfigureAwait(true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }

            await _store.InitializeAsync().ConfigureAwait(true);
            await LoadProjectsAsync().ConfigureAwait(true);
            await LoadLabelsAsync().ConfigureAwait(true);
            await RefreshTasksAsync().ConfigureAwait(true);
            await RefreshBackupListAsync().ConfigureAwait(true);
            await TryUploadNewestPreRestoreBackupAsync(restoreStartedAt, interactive: true).ConfigureAwait(true);
            ShowInfo("OneDrive backup restored", backup.DisplayName, InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowInfo("OneDrive restore failed", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnChangeOneDriveAccountClicked(object sender, RoutedEventArgs e)
    {
        if (!EnsureOneDriveAvailableForAction())
        {
            return;
        }

        try
        {
            var previousAccount = _settings.Settings.OneDriveBackupAccount;
            var result = await _microsoftAuth.ConnectAsync(
                    MicrosoftGraphFeature.OneDriveBackup,
                    MicrosoftGraphAuthService.OneDriveBackupScopes)
                .ConfigureAwait(true);
            _settings.Settings.OneDriveBackupAccount = result.Account;
            _settings.Settings.LastOneDriveBackupError = string.Empty;
            await _microsoftAuth.DisconnectAsync(
                    previousAccount,
                    [_settings.Settings.MicrosoftToDoAccount, _settings.Settings.OneDriveBackupAccount])
                .ConfigureAwait(true);
            await _settings.SaveAsync().ConfigureAwait(true);
            RefreshCloudBackupStatus(isBusy: false);
            ShowInfo("OneDrive backup account selected", result.Username ?? "Microsoft account connected.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            _settings.Settings.LastOneDriveBackupError = exception.Message;
            await _settings.SaveAsync().ConfigureAwait(true);
            RefreshCloudBackupStatus(isBusy: false);
            ShowInfo("OneDrive sign-in failed", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnChangeOneDrivePassphraseClicked(object sender, RoutedEventArgs e)
    {
        var passphrase = await PromptForPassphraseAsync("Change cloud backup passphrase", confirm: true).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            return;
        }

        await _credentials.SaveAsync(OneDrivePassphraseKey, passphrase).ConfigureAwait(true);
        _settings.Settings.OneDriveBackupEncryptionEnabled = true;
        await _settings.SaveAsync().ConfigureAwait(true);
        _suppressSettingsEvents = true;
        SettingsPage.CloudBackupEncryptionEnabled = true;
        _suppressSettingsEvents = false;
        RefreshCloudBackupStatus(isBusy: false);
        ShowInfo("Passphrase updated", "Future encrypted cloud backups will use the new passphrase.", InfoBarSeverity.Success);
    }

    private async void OnAutoSyncToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        _settings.Settings.AutoSyncEnabled = SettingsPage.AutoSyncEnabled;
        await _settings.SaveAsync().ConfigureAwait(true);
        StartAutoSyncTimer();
        if (_settings.Settings.AutoSyncEnabled)
        {
            await RunAutomaticSyncAsync().ConfigureAwait(true);
            ShowInfo("Automatic sync on", "Connected apps refresh while Openza Tasks is running.", InfoBarSeverity.Success);
        }
        else
        {
            ShowInfo("Automatic sync off", "Use Sync when you want to refresh connected apps.", InfoBarSeverity.Informational);
        }
    }

    private void StartAutoSyncTimer()
    {
        _autoSyncTimer?.Stop();
        _autoSyncTimer = null;

        if (!_settings.Settings.AutoSyncEnabled)
        {
            return;
        }

        var intervalMinutes = Math.Clamp(
            _settings.Settings.AutoSyncIntervalMinutes <= 0 ? DefaultAutoSyncIntervalMinutes : _settings.Settings.AutoSyncIntervalMinutes,
            1,
            60);
        _autoSyncTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(intervalMinutes),
        };
        _autoSyncTimer.Tick += async (_, _) => await RunAutomaticSyncAsync().ConfigureAwait(true);
        _autoSyncTimer.Start();
    }

    private async Task TryUploadCloudBackupAsync(string localBackupPath, bool interactive, bool showResult)
    {
        if (!_settings.Settings.OneDriveBackupEnabled || !IsOneDriveBackupAvailable())
        {
            return;
        }

        var backup = _backupService.ListBackupInfo()
            .FirstOrDefault(item => string.Equals(item.Path, localBackupPath, StringComparison.OrdinalIgnoreCase));
        if (backup is null)
        {
            return;
        }

        try
        {
            RefreshCloudBackupStatus(isBusy: true);
            var options = await CreateCloudBackupOptionsAsync(interactive).ConfigureAwait(true);
            var uploaded = await CreateCloudBackupService(interactive).UploadBackupAsync(backup, options).ConfigureAwait(true);
            if (uploaded is not null)
            {
                _settings.Settings.LastOneDriveBackupAt = DateTimeOffset.Now;
                _settings.Settings.LastOneDriveBackupStatus = "Uploaded";
                _settings.Settings.LastOneDriveBackupError = string.Empty;
                await _settings.SaveAsync().ConfigureAwait(true);
                if (showResult)
                {
                    ShowInfo("OneDrive backup uploaded", uploaded.DisplayName, InfoBarSeverity.Success);
                }
            }
        }
        catch (Exception exception)
        {
            _settings.Settings.LastOneDriveBackupStatus = "Failed";
            _settings.Settings.LastOneDriveBackupError = exception.Message;
            await _settings.SaveAsync().ConfigureAwait(true);
            AppLog.Write(exception);
            if (showResult)
            {
                ShowInfo("Restore point created", $"OneDrive upload failed: {exception.Message}", InfoBarSeverity.Warning);
            }
        }
        finally
        {
            RefreshCloudBackupStatus(isBusy: false);
        }
    }

    private async Task TryUploadPendingCloudBackupsAsync(bool interactive, bool showResult)
    {
        if (!_settings.Settings.OneDriveBackupEnabled || !IsOneDriveBackupAvailable())
        {
            return;
        }

        try
        {
            RefreshCloudBackupStatus(isBusy: true);
            var options = await CreateCloudBackupOptionsAsync(interactive).ConfigureAwait(true);
            var uploaded = await CreateCloudBackupService(interactive)
                .UploadPendingBackupsAsync(_backupService.ListBackupInfo(), options)
                .ConfigureAwait(true);
            if (uploaded.Count > 0)
            {
                _settings.Settings.LastOneDriveBackupAt = DateTimeOffset.Now;
                _settings.Settings.LastOneDriveBackupStatus = "Uploaded";
                _settings.Settings.LastOneDriveBackupError = string.Empty;
                await _settings.SaveAsync().ConfigureAwait(true);
            }

            if (showResult)
            {
                ShowInfo(
                    "OneDrive backup ready",
                    uploaded.Count == 0 ? "No new restore points needed uploading." : $"Uploaded {uploaded.Count} backup{(uploaded.Count == 1 ? string.Empty : "s")}.",
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception exception)
        {
            _settings.Settings.LastOneDriveBackupStatus = "Failed";
            _settings.Settings.LastOneDriveBackupError = exception.Message;
            await _settings.SaveAsync().ConfigureAwait(true);
            AppLog.Write(exception);
            if (showResult)
            {
                ShowInfo("OneDrive upload failed", exception.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            RefreshCloudBackupStatus(isBusy: false);
        }
    }

    private async Task TryUploadNewestPreRestoreBackupAsync(DateTime restoreStartedAt, bool interactive)
    {
        var backup = _backupService.ListBackupInfo()
            .Where(item => string.Equals(item.Reason, BackupReasons.PreRestore, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.CreatedAt >= restoreStartedAt.AddSeconds(-2))
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
        if (backup is not null)
        {
            await TryUploadCloudBackupAsync(backup.Path, interactive, showResult: false).ConfigureAwait(true);
        }
    }

    private async Task RefreshCloudBackupListAsync(bool interactive, bool showResult)
    {
        if (!IsOneDriveBackupAvailable() || (!_settings.Settings.OneDriveBackupEnabled && !interactive))
        {
            SettingsPage.SetCloudBackups([]);
            RefreshCloudBackupStatus(isBusy: false);
            return;
        }

        try
        {
            RefreshCloudBackupStatus(isBusy: true);
            await EnsureOneDriveAccessAsync(interactive).ConfigureAwait(true);
            var backups = await CreateCloudBackupService(interactive)
                .ListBackupsAsync(_backupService.Context.AppFlavor)
                .ConfigureAwait(true);
            SettingsPage.SetCloudBackups(backups);
            _settings.Settings.LastOneDriveBackupError = string.Empty;
            await _settings.SaveAsync().ConfigureAwait(true);
            if (showResult)
            {
                ShowInfo("OneDrive backups refreshed", $"{backups.Count} backup{(backups.Count == 1 ? string.Empty : "s")} found.", InfoBarSeverity.Success);
            }
        }
        catch (Exception exception)
        {
            _settings.Settings.LastOneDriveBackupError = exception.Message;
            await _settings.SaveAsync().ConfigureAwait(true);
            AppLog.Write(exception);
            if (showResult)
            {
                ShowInfo("Could not refresh OneDrive", exception.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            RefreshCloudBackupStatus(isBusy: false);
        }
    }

    private CloudBackupService CreateCloudBackupService(bool interactive) =>
        new(
            new OneDriveBackupProvider(
                _httpClient,
                cancellationToken => GetOneDriveAccessTokenAsync(interactive, cancellationToken)),
            _backupService.RetentionPolicy);

    private async Task<CloudBackupOptions> CreateCloudBackupOptionsAsync(bool interactive)
    {
        var passphrase = _settings.Settings.OneDriveBackupEncryptionEnabled
            ? await _credentials.GetAsync(OneDrivePassphraseKey).ConfigureAwait(true)
            : null;
        if (_settings.Settings.OneDriveBackupEncryptionEnabled && string.IsNullOrWhiteSpace(passphrase) && interactive)
        {
            passphrase = await PromptForPassphraseAsync("Enter cloud backup passphrase", confirm: false).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(passphrase))
            {
                await _credentials.SaveAsync(OneDrivePassphraseKey, passphrase).ConfigureAwait(true);
            }
        }

        return new CloudBackupOptions(
            _settings.Settings.OneDriveBackupEnabled,
            _settings.Settings.OneDriveBackupEncryptionEnabled,
            passphrase,
            _backupService.Context.AppFlavor);
    }

    private async Task<string?> GetOneDriveAccessTokenAsync(bool interactive, CancellationToken cancellationToken)
    {
        var result = await _microsoftAuth.GetAccessTokenAsync(
            MicrosoftGraphFeature.OneDriveBackup,
            _settings.Settings.OneDriveBackupAccount,
            MicrosoftGraphAuthService.OneDriveBackupScopes,
            interactive,
            cancellationToken).ConfigureAwait(true);
        if (result is null)
        {
            await _settings.SaveAsync().ConfigureAwait(true);
            return null;
        }

        _settings.Settings.OneDriveBackupAccount = result.Account;
        _settings.Settings.LastOneDriveBackupError = string.Empty;
        await _settings.SaveAsync().ConfigureAwait(true);
        return result.AccessToken;
    }

    private async Task EnsureOneDriveAccessAsync(bool interactive)
    {
        var token = await GetOneDriveAccessTokenAsync(interactive, CancellationToken.None).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Sign in to the OneDrive backup account before using cloud backup.");
        }
    }

    private async Task<string?> GetPassphraseForRestoreAsync(CloudBackupInfo backup)
    {
        if (!string.Equals(backup.EncryptionMode, CloudBackupEncryptionModes.Passphrase, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await _credentials.GetAsync(OneDrivePassphraseKey).ConfigureAwait(true) ??
            await PromptForPassphraseAsync("Enter cloud backup passphrase", confirm: false).ConfigureAwait(true);
    }

    private async Task<string?> PromptForPassphraseAsync(string title, bool confirm)
    {
        var passphraseBox = new PasswordBox
        {
            PlaceholderText = "Passphrase",
            MinWidth = 320,
        };
        var confirmBox = new PasswordBox
        {
            PlaceholderText = "Confirm passphrase",
            MinWidth = 320,
            Visibility = confirm ? Visibility.Visible : Visibility.Collapsed,
        };
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = confirm
                        ? "Store this passphrase somewhere safe. Encrypted OneDrive backups cannot be restored without it."
                        : "Encrypted OneDrive backups require the passphrase used when they were uploaded.",
                    TextWrapping = TextWrapping.Wrap,
                },
                passphraseBox,
                confirmBox,
            },
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(passphraseBox.Password))
        {
            ShowInfo("Passphrase required", "Enter a cloud backup passphrase.", InfoBarSeverity.Warning);
            return null;
        }

        if (confirm && passphraseBox.Password != confirmBox.Password)
        {
            ShowInfo("Passphrases do not match", "Enter the same passphrase twice.", InfoBarSeverity.Warning);
            return null;
        }

        return passphraseBox.Password;
    }

    private void RefreshCloudBackupStatus(bool isBusy)
    {
        SettingsPage.SetCloudBackupAvailable(IsOneDriveBackupAvailable());
        SettingsPage.SetCloudBackupStatus(
            IsOneDriveBackupAvailable() && _settings.Settings.OneDriveBackupEnabled,
            _settings.Settings.OneDriveBackupEncryptionEnabled,
            isBusy,
            _settings.Settings.OneDriveBackupAccount.Username,
            _settings.Settings.LastOneDriveBackupAt,
            _settings.Settings.LastOneDriveBackupError);
    }

    private bool EnsureCloudBackupEnabledForAction()
    {
        if (!EnsureOneDriveAvailableForAction())
        {
            return false;
        }

        if (!_settings.Settings.OneDriveBackupEnabled)
        {
            ShowInfo("OneDrive backup is off", "Turn on OneDrive backup first.", InfoBarSeverity.Warning);
            return false;
        }

        return true;
    }

    private bool EnsureOneDriveAvailableForAction()
    {
        if (!IsOneDriveBackupAvailable())
        {
            ShowInfo("OneDrive backup unavailable", "Cloud backup is not available for this app flavor.", InfoBarSeverity.Warning);
            return false;
        }

        return true;
    }

    private void ResetOneDriveBackupSwitches()
    {
        _suppressSettingsEvents = true;
        SettingsPage.CloudBackupEnabled = false;
        _suppressSettingsEvents = false;
    }

    private async Task RestoreBackupPathAsync(string path)
    {
        var dialog = new ContentDialog
        {
            Title = "Restore database",
            Content = "This will replace the current local database. A safety restore point of the current database is created first.",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var restoreStartedAt = DateTime.Now;
            await _backupService.RestoreBackupAsync(path).ConfigureAwait(true);
            await _store.InitializeAsync().ConfigureAwait(true);
            await LoadProjectsAsync().ConfigureAwait(true);
            await LoadLabelsAsync().ConfigureAwait(true);
            await RefreshTasksAsync().ConfigureAwait(true);
            await RefreshBackupListAsync().ConfigureAwait(true);
            await TryUploadNewestPreRestoreBackupAsync(restoreStartedAt, interactive: false).ConfigureAwait(true);
            ShowInfo("Database restored", path, InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowInfo("Restore failed", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task RefreshSettingsStateAsync()
    {
        var todoistConnected = !string.IsNullOrWhiteSpace(await _credentials.GetAsync(TodoistTokenKey).ConfigureAwait(true));
        var microsoftConnected = _settings.Settings.MicrosoftToDoAccount.IsConnected;
        SettingsPage.SetProviderStatus(todoistConnected, _settings.Settings.MicrosoftToDoAccount.Username);
        SyncPage.SetProviderStatus(todoistConnected, microsoftConnected);
        await RefreshBackupListAsync().ConfigureAwait(true);
        RefreshCloudBackupStatus(isBusy: false);
        await RefreshSourceItemsAsync().ConfigureAwait(true);
    }

    private Task RefreshBackupListAsync()
    {
        SettingsPage.SetBackupFolder(_backupService.BackupDirectory);
        SettingsPage.SetBackups(_backupService.ListBackupInfo());
        return Task.CompletedTask;
    }

    private async Task TryCreateStartupBackupAsync()
    {
        if (_settings.Settings.LastAutoBackupAt?.LocalDateTime.Date == DateTimeOffset.Now.Date)
        {
            return;
        }

        try
        {
            var backupPath = await _backupService.CreateBackupAsync(BackupReasons.Daily).ConfigureAwait(true);
            _settings.Settings.LastAutoBackupAt = DateTimeOffset.Now;
            await _settings.SaveAsync().ConfigureAwait(true);
            await TryUploadCloudBackupAsync(backupPath, interactive: false, showResult: false).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
        }
    }

    private async Task ShowStartupRecoveryPromptAsync(BackupInfo backup)
    {
        var dialog = new ContentDialog
        {
            Title = "Restore available restore point?",
            Content = $"This app data looks new or empty, but a local restore point is available:\n\n{backup.DisplayName}\n\nRestore it before continuing?",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Keep current data",
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var restoreStartedAt = DateTime.Now;
            await _backupService.RestoreBackupAsync(backup.Path).ConfigureAwait(true);
            await _store.InitializeAsync().ConfigureAwait(true);
            await TryUploadNewestPreRestoreBackupAsync(restoreStartedAt, interactive: false).ConfigureAwait(true);
            ShowInfo("Database restored", backup.Path, InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowInfo("Restore failed", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnAddSourceClicked(object? sender, string sourceItemId)
    {
        var task = await AddSourceItemToOpenzaAsync(sourceItemId).ConfigureAwait(true);
        if (task is null)
        {
            ShowInfo("Cannot add task", "The connected-app task is no longer available.", InfoBarSeverity.Warning);
            await RefreshSourceItemsAsync().ConfigureAwait(true);
            return;
        }

        await RefreshAfterAddingSourceItemsAsync().ConfigureAwait(true);
        ShowInfo("Added to Inbox", task.Title, InfoBarSeverity.Success);
    }

    private async void OnAddAllConnectedTasksClicked(object sender, RoutedEventArgs e)
    {
        var items = await _store.GetProviderSourceItemsAsync(spaceId: _currentSpaceId, includeAdopted: false).ConfigureAwait(true);
        if (items.Count == 0)
        {
            ShowInfo("Nothing to add", "No connected-app tasks are waiting.", InfoBarSeverity.Informational);
            return;
        }

        var added = 0;
        foreach (var item in items)
        {
            if (await AddSourceItemToOpenzaAsync(item.Id).ConfigureAwait(true) is not null)
            {
                added++;
            }
        }

        await RefreshAfterAddingSourceItemsAsync().ConfigureAwait(true);
        ShowInfo("Tasks added", $"{added} task{(added == 1 ? string.Empty : "s")} added to Inbox.", InfoBarSeverity.Success);
    }

    private async void OnSkipSourceClicked(object? sender, string sourceItemId)
    {
        if (!await _store.SkipProviderSourceItemAsync(sourceItemId).ConfigureAwait(true))
        {
            ShowInfo("Cannot skip task", "The connected-app task is no longer available.", InfoBarSeverity.Warning);
            await RefreshSourceItemsAsync().ConfigureAwait(true);
            return;
        }

        await RefreshSourceItemsAsync().ConfigureAwait(true);
        ShowInfo("Skipped", "The task was hidden from Inbox intake. Todoist was not changed.", InfoBarSeverity.Informational);
    }

    private async void OnUnskipSourceClicked(object? sender, string sourceItemId)
    {
        if (!await _store.UnskipProviderSourceItemAsync(sourceItemId).ConfigureAwait(true))
        {
            ShowInfo("Cannot restore task", "The skipped task is no longer available.", InfoBarSeverity.Warning);
            await RefreshSourceItemsAsync().ConfigureAwait(true);
            return;
        }

        await RefreshSourceItemsAsync().ConfigureAwait(true);
        ShowInfo("Restored to intake", "The task is visible in Inbox intake again.", InfoBarSeverity.Informational);
    }

    private async void OnShowSkippedConnectedTasksChanged(object sender, RoutedEventArgs e)
    {
        await RefreshSourceItemsAsync().ConfigureAwait(true);
    }

    private void OnReviewConnectedTasksClicked(object sender, RoutedEventArgs e)
    {
        _selectedTaskId = null;
        TasksPage.ClearTaskSelection();
    }

    private async Task RefreshSourceItemsAsync()
    {
        var allItems = await _store.GetProviderSourceItemsAsync(spaceId: _currentSpaceId, includeAdopted: false, includeIgnored: true).ConfigureAwait(true);
        ApplySourceItems(allItems);
    }

    private void ApplySourceItems(IReadOnlyList<ProviderSourceItem> items)
    {
        var waitingItems = items.Where(item => !item.IsSkipped).ToList();
        var skippedItems = items.Where(item => item.IsSkipped).ToList();
        var displayItems = TasksPage.ShowSkippedConnectedTasks || waitingItems.Count == 0
            ? items
            : waitingItems;
        SyncPage.SetSourceItems(waitingItems);
        TasksPage.SetConnectedTasks(
            displayItems,
            string.Equals(_currentView, "inbox", StringComparison.Ordinal),
            waitingItems.Count,
            skippedItems.Count);
    }

    private async Task<TaskItem?> AddSourceItemToOpenzaAsync(string sourceItemId)
    {
        return await _store.AdoptProviderSourceItemAsync(sourceItemId, _currentSpaceId).ConfigureAwait(true);
    }

    private async Task RefreshAfterAddingSourceItemsAsync()
    {
        await LoadProjectsAsync().ConfigureAwait(true);
        await LoadLabelsAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        await RefreshSourceItemsAsync().ConfigureAwait(true);
    }
}
