using Openza.Tasks.Core.Services;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Desktop.ViewModels.Navigation;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public void ReportError(string message) => SetPersistentStatusMessage(message);
    public void ReportStatus(string message) => StatusMessage = message;
    private bool _showFirstRunInbox;
    public string StartupView => _showFirstRunInbox ? "Inbox" : _preferencesStore.Load().LastView;
    public Task SaveLastViewAsync(string view) => _preferencesStore.UpdateAsync(preferences => preferences with { LastView = view });
    public string BuildTaskCopyText(string field) => !HasSelectedTask ? string.Empty : field switch
    {
        "title" => DetailTitle.Trim(),
        "notes" => DetailNotes.Trim(),
        "source" => $"{SourceTaskHeader}\nTitle: {SourceTaskTitle}\nProject: {SourceTaskProject}\nDate: {SourceTaskDate}\nDeadline: {SourceTaskDeadline}\nPriority: {SourceTaskPriority}\nCreated: {SourceTaskCreated}\nRecurring: {SourceTaskRecurrence}" +
            (string.IsNullOrWhiteSpace(SelectedTask?.Task.SourceUrl) ? "" : $"\nURL: {SelectedTask.Task.SourceUrl}"),
        "metadata" => $"Created: {LocalTaskCreated}\nModified: {LocalTaskUpdated}\nStatus: {StatusFromIndex(DetailStatusIndex)}\nProject: {DetailProject?.Title}\nDate: {FormatTaskDate(DetailDate)}\nDeadline: {FormatTaskDate(DetailDeadline)}\nPriority: {FormatPriority(DetailPriorityIndex + 1)}\nLabels: {(string.IsNullOrWhiteSpace(DetailLabels) ? "No labels" : DetailLabels)}\nSource: {SourceTaskName}",
        _ => string.Empty,
    };
    private readonly TaskSelectionState _taskSelectionState = new();
    private string _lastSyncResult = "Not synced in this session.";
    public string LastSyncResult { get => _lastSyncResult; private set => SetProperty(ref _lastSyncResult, value); }
    public bool AutomaticRestorePointsEnabled => _preferencesStore.Load().AutomaticRestorePointsEnabled;
    public bool ShowGetStarted => _preferencesStore.Load().ShowGetStarted;
    private int _spaceTaskCount;
    public bool IsGetStartedVisible => ShowGetStarted && SelectedNavigation?.Kind == Openza.Tasks.Core.Data.TaskListKind.Inbox &&
        SelectedProject is null && _spaceTaskCount == 0 && HasNoTasks && !HasConnectedTasks && !HasActiveListFilters;

    public void CloseTaskDetails()
    {
        _taskSelectionState.ClearDisplayed();
        SelectedTask = null;
    }

    private TaskViewContext CurrentTaskViewContext => new(
        _currentSpaceId, SelectedNavigation?.Kind ?? TaskListKind.Inbox, SelectedProject?.Project.Id);

    private void RememberTaskSelection() => _taskSelectionState.RememberDisplayed(SelectedTask?.Task.Id);

    private string? RememberedTaskSelection() => _taskSelectionState.RememberedFor(CurrentTaskViewContext);

    public async Task SetAutomaticRestorePointsAsync(bool enabled)
    {
        await _preferencesStore.UpdateAsync(preferences => preferences with { AutomaticRestorePointsEnabled = enabled });
        OnPropertyChanged(nameof(AutomaticRestorePointsEnabled));
    }

    public async Task DismissGetStartedAsync()
    {
        await _preferencesStore.UpdateAsync(preferences => preferences with { ShowGetStarted = false });
        OnPropertyChanged(nameof(ShowGetStarted));
        OnPropertyChanged(nameof(IsGetStartedVisible));
    }

    public async Task<BackupInfo?> FindStartupRecoveryAsync()
    {
        if (_backupService.Value is not { } backup || !await backup.IsCurrentDatabaseFreshAsync()) return null;
        return await backup.FindLatestRestorableBackupAsync();
    }

    public Task ExportSelectedRestorePointAsync(string destination)
    {
        var selected = SelectedRestorePoint;
        if (selected is null || _backupService.Value is not { } backup) return Task.CompletedTask;
        return RunBusyAsync(async () =>
        {
            await backup.ExportBackupAsync(selected.Backup.Path, destination);
            StatusMessage = "Selected restore point exported";
        });
    }
}
