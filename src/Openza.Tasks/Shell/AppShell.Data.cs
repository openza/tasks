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

            var microsoftToken = await _microsoftAuth.GetAccessTokenAsync(GetMicrosoftClientId(), GetMicrosoftTenantId(), interactiveIfNeeded: false).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(microsoftToken))
            {
                summaries.Add(await _syncEngine.SyncAsync(new MicrosoftToDoProvider(_httpClient, microsoftToken)).ConfigureAwait(true));
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
        var clientId = SettingsPage.MicrosoftClientId;
        var tenantId = SettingsPage.MicrosoftTenantId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            ShowInfo("Microsoft client ID required", "Add the public Azure app client ID for Microsoft To Do sign-in.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            _settings.Settings.MicrosoftToDoClientId = clientId;
            _settings.Settings.MicrosoftToDoTenantId = tenantId;
            await _settings.SaveAsync().ConfigureAwait(true);

            var result = await _microsoftAuth.SignInAsync(clientId, tenantId).ConfigureAwait(true);
            await _store.SetIntegrationConfiguredAsync(IntegrationIds.MicrosoftToDo, true).ConfigureAwait(true);
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
        await _microsoftAuth.SignOutAsync(GetMicrosoftClientId(), GetMicrosoftTenantId()).ConfigureAwait(true);
        await _store.SetIntegrationConfiguredAsync(IntegrationIds.MicrosoftToDo, false).ConfigureAwait(true);
        await RefreshSettingsStateAsync().ConfigureAwait(true);
        ShowInfo("Microsoft To Do disconnected", "Existing synced tasks stay in your local database.", InfoBarSeverity.Informational);
    }

    private async void OnCreateBackupClicked(object sender, RoutedEventArgs e)
    {
        var path = await _backupService.CreateBackupAsync().ConfigureAwait(true);
        await RefreshBackupListAsync().ConfigureAwait(true);
        ShowInfo("Backup created", path, InfoBarSeverity.Success);
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
            ShowInfo("Select a backup", "Choose a backup to export.", InfoBarSeverity.Warning);
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
            ShowInfo("Select a backup", "Choose a backup to restore.", InfoBarSeverity.Warning);
            return;
        }

        await RestoreBackupPathAsync(backup.Path).ConfigureAwait(true);
    }

    private async void OnDeleteBackupClicked(object sender, RoutedEventArgs e)
    {
        var backup = SettingsPage.SelectedBackup;
        if (backup is null)
        {
            ShowInfo("Select a backup", "Choose a backup to delete.", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete backup",
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
        ShowInfo("Backup deleted", backup.FileName, InfoBarSeverity.Success);
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

    private async Task RestoreBackupPathAsync(string path)
    {
        var dialog = new ContentDialog
        {
            Title = "Restore backup",
            Content = "This will replace the current local database. A safety copy of the current database is created first.",
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
            await _backupService.RestoreBackupAsync(path).ConfigureAwait(true);
            await _store.InitializeAsync().ConfigureAwait(true);
            await LoadProjectsAsync().ConfigureAwait(true);
            await LoadLabelsAsync().ConfigureAwait(true);
            await RefreshTasksAsync().ConfigureAwait(true);
            await RefreshBackupListAsync().ConfigureAwait(true);
            ShowInfo("Backup restored", path, InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowInfo("Restore failed", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task RefreshSettingsStateAsync()
    {
        var todoistConnected = !string.IsNullOrWhiteSpace(await _credentials.GetAsync(TodoistTokenKey).ConfigureAwait(true));
        var microsoftConnected = await _microsoftAuth.IsConnectedAsync(GetMicrosoftClientId(), GetMicrosoftTenantId()).ConfigureAwait(true);
        SettingsPage.SetProviderStatus(todoistConnected, microsoftConnected);
        SyncPage.SetProviderStatus(todoistConnected, microsoftConnected);
        SettingsPage.SetMicrosoftConfig(GetMicrosoftClientId(), GetMicrosoftTenantId());
        await RefreshBackupListAsync().ConfigureAwait(true);
        await RefreshSourceItemsAsync().ConfigureAwait(true);
    }

    private Task RefreshBackupListAsync()
    {
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
            await _backupService.CreateBackupAsync().ConfigureAwait(true);
            _settings.Settings.LastAutoBackupAt = DateTimeOffset.Now;
            await _settings.SaveAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
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
