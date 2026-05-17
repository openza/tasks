using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json.Nodes;
using Openza.Tasks.Controls;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Pages;
using Openza.Tasks.ViewModels;

namespace Openza.Tasks.Shell;

public sealed partial class AppShell
{
    private async Task LoadLabelsAsync()
    {
        _allLabels.Clear();
        _allLabels.AddRange(await _store.GetLabelsAsync().ConfigureAwait(true));
        TasksPage.RefreshLabelFilter(_allLabels, _labelFilterId);
        TasksPage.SetLabelOptions(_allLabels);
    }

    private async Task RefreshTasksAsync()
    {
        if (!_uiReady)
        {
            return;
        }

        var selectedTaskBeforeRefresh = _selectedTaskId;
        var viewportBeforeRefresh = TasksPage.CaptureTaskListViewport();
        var projects = _allProjects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var selectedProject = _currentView == "tasks" ? GetSelectedProject() : null;
        var query = new TaskQuery
        {
            SpaceId = _currentSpaceId,
            Kind = _currentView switch
            {
                "inbox" => TaskListKind.Inbox,
                "today" => TaskListKind.Today,
                "calendar" => TaskListKind.Calendar,
                "overdue" => TaskListKind.Overdue,
                "waiting" => TaskListKind.Waiting,
                "someday" => TaskListKind.Someday,
                "completed" => TaskListKind.Completed,
                "tasks" => TaskListKind.Open,
                _ => TaskListKind.NextActions,
            },
            ProjectId = selectedProject?.Id,
            LabelId = _labelFilterId,
            DateScope = _dateScopeFilter,
            RepeatScope = _repeatScopeFilter,
            SearchText = TasksPage.SearchText,
            SortMode = _sortMode,
            Priority = _priorityFilter,
        };

        var tasks = await _store.GetTasksAsync(query).ConfigureAwait(true);
        var allSpaceTasks = await _store.GetTasksAsync(new TaskQuery
        {
            SpaceId = _currentSpaceId,
            Kind = TaskListKind.All,
            IncludeSubtasks = true,
        }).ConfigureAwait(true);
        var subtaskProgress = BuildSubtaskProgress(allSpaceTasks);
        var matchingSubtasks = BuildMatchingSubtaskText(tasks, query.SearchText);
        var taskItems = tasks
            .Where(task => string.IsNullOrWhiteSpace(task.ParentId))
            .Select(task =>
            {
                projects.TryGetValue(task.ProjectId ?? string.Empty, out var project);
                return new TaskListItemViewModel(
                    task,
                    project,
                    _currentView,
                    selectedProject is not null,
                    0,
                    subtaskProgress.TryGetValue(task.Id, out var progress) ? progress : string.Empty,
                    matchingSubtasks.TryGetValue(task.Id, out var matchingSubtask) ? matchingSubtask : string.Empty);
            })
            .ToList();

        TasksPage.ViewModel.SetTasks(taskItems, _groupMode);

        if (_selectedTaskId is not null && tasks.All(task => task.Id != _selectedTaskId))
        {
            _selectedTaskId = null;
            TasksPage.HideDetailsPane();
            TasksPage.ClearTaskSelection();
            TasksPage.DetailsPanel.ClearForNewTask(selectedProject, 3, DefaultStatusForCurrentView());
        }
        else if (_selectedTaskId is not null)
        {
            TasksPage.SelectTask(_selectedTaskId);
        }

        if (_selectedTaskId is not null && string.Equals(_selectedTaskId, selectedTaskBeforeRefresh, StringComparison.Ordinal))
        {
            TasksPage.RestoreTaskListViewport(viewportBeforeRefresh, _selectedTaskId);
        }

        var title = selectedProject?.Name ?? _currentView switch
        {
            "inbox" => "Inbox",
            "today" => "Today",
            "calendar" => "Calendar",
            "overdue" => "Overdue",
            "waiting" => "Waiting For",
            "someday" => "Someday",
            "completed" => "Completed",
            "tasks" => "Tasks",
            _ => "Next Actions",
        };
        var taskCount = taskItems.Count(task => !task.IsSubtask);
        var subtitle = $"{taskCount} task{(taskCount == 1 ? string.Empty : "s")}";
        if (selectedProject is not null)
        {
            subtitle = $"{subtitle} · {SourceName(selectedProject.IntegrationId)}";
        }

        TasksPage.ViewModel.SelectedProject = selectedProject;
        TasksPage.SetHeader(title, subtitle, selectedProject is not null);
        var counts = await _store.GetTaskCountsAsync(_currentSpaceId).ConfigureAwait(true);
        var sourceItems = await _store.GetProviderSourceItemsAsync(spaceId: _currentSpaceId, includeAdopted: false, includeIgnored: true).ConfigureAwait(true);
        TasksPage.SetGetStartedVisible(_settings.Settings.ShowGetStarted &&
            string.Equals(_currentView, "inbox", StringComparison.Ordinal) &&
            counts.All == 0 &&
            sourceItems.Count == 0 &&
            TasksPage.ViewModel.IsEmpty &&
            selectedProject is null &&
            string.IsNullOrWhiteSpace(TasksPage.SearchText) &&
            _priorityFilter is null &&
            _dateScopeFilter == TaskDateScope.All &&
            _repeatScopeFilter == TaskRepeatScope.Include &&
            _labelFilterId is null);
        ApplySourceItems(sourceItems);
    }

    private async void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_uiReady)
        {
            return;
        }

        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnSortChanged(object sender, EventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        ApplyViewControlsFromPage();
        await SaveTaskViewPreferencesAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnGroupChanged(object sender, EventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        ApplyViewControlsFromPage();
        await SaveTaskViewPreferencesAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnPriorityChanged(object sender, EventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        ApplyViewControlsFromPage();
        await SaveTaskViewPreferencesAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnRepeatScopeChanged(object sender, EventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        ApplyViewControlsFromPage();
        await SaveTaskViewPreferencesAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnLabelChanged(object sender, EventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        ApplyViewControlsFromPage();
        await SaveTaskViewPreferencesAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnTaskSelected(TasksPage sender, TaskListItemViewModel item)
    {
        if (!await SavePendingTaskDetailsAsync().ConfigureAwait(true))
        {
            if (_selectedTaskId is null)
            {
                TasksPage.ClearTaskSelection();
            }
            else
            {
                TasksPage.SelectTask(_selectedTaskId);
            }

            return;
        }

        await LoadTaskDetailsAsync(item.Id).ConfigureAwait(true);
    }

    private async Task LoadTaskDetailsAsync(string taskId)
    {
        var task = await _store.GetTaskAsync(taskId).ConfigureAwait(true);
        if (task is null)
        {
            return;
        }

        _selectedTaskId = task.Id;
        var projects = _allProjects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var childTasks = await _store.GetTasksAsync(new TaskQuery
        {
            SpaceId = task.SpaceId,
            ParentId = task.Id,
            Kind = TaskListKind.All,
            IncludeSubtasks = true,
        }).ConfigureAwait(true);
        var childItems = childTasks
            .Select(child =>
            {
                projects.TryGetValue(child.ProjectId ?? string.Empty, out var childProject);
                return new TaskListItemViewModel(child, childProject, _currentView, GetSelectedProject() is not null, 1);
            })
            .ToList();
        TasksPage.DetailsPanel.LoadTask(task, GetProject(task.ProjectId), childItems);
        TasksPage.SelectTask(task.Id);
        TasksPage.ShowDetailsPane();
    }

    private async void OnQuickAddClicked(object sender, RoutedEventArgs e)
    {
        var quickAdd = await TasksPage.ShowQuickAddAsync(DefaultProjectForQuickAdd(), DefaultStatusForCurrentView(), DefaultDateForCurrentView()).ConfigureAwait(true);
        if (quickAdd is null)
        {
            return;
        }

        var task = new TaskItem
        {
            Id = $"local_{Guid.NewGuid():N}",
            SpaceId = _currentSpaceId,
            IntegrationId = IntegrationIds.Local,
            Title = quickAdd.Title,
            Notes = EmptyToNull(quickAdd.Notes),
            ProjectId = quickAdd.ProjectId,
            Priority = quickAdd.Priority,
            PlannedOn = quickAdd.PlannedOn,
            Status = quickAdd.Status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Labels = quickAdd.BuildLabels(_allLabels, IntegrationIds.Local),
        };

        await _store.UpsertTaskAsync(task).ConfigureAwait(true);
        await LoadLabelsAsync().ConfigureAwait(true);
        await LoadProjectsAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        ShowInfo("Task created", task.Title, InfoBarSeverity.Success);

        if (quickAdd.OpenAfterCreate)
        {
            await LoadTaskDetailsAsync(task.Id).ConfigureAwait(true);
        }
    }

    private void OnConnectProvidersClicked(object sender, RoutedEventArgs e)
    {
        SelectNavigation("settings");
    }

    private void OnExploreSyncClicked(object sender, RoutedEventArgs e)
    {
        SelectNavigation("sync");
    }

    private async void OnDismissGetStartedClicked(object sender, RoutedEventArgs e)
    {
        _settings.Settings.ShowGetStarted = false;
        await _settings.SaveAsync().ConfigureAwait(true);
        TasksPage.SetGetStartedVisible(false);
    }

    private readonly record struct TaskDetailsDraft(
        string Snapshot,
        string Title,
        string Notes,
        string LabelsText,
        DateOnly? PlannedOn,
        DateOnly? DeadlineOn,
        string? ProjectId,
        TaskItemStatus Status,
        int Priority,
        string? SourceDateMismatchAcknowledgementKey);

    private TaskDetailsDraft CaptureTaskDetailsDraft()
    {
        var panel = TasksPage.DetailsPanel;
        return new TaskDetailsDraft(
            panel.Snapshot,
            panel.TitleText,
            panel.NotesText,
            panel.LabelsText,
            panel.SelectedPlannedOn,
            panel.SelectedDeadlineOn,
            panel.SelectedProject?.Id,
            panel.SelectedStatus,
            panel.SelectedPriority,
            panel.SourceDateMismatchAcknowledgementKey);
    }

    private async void OnDetailsAutoSaveRequested(object sender, RoutedEventArgs e)
    {
        await SaveTaskDetailsIfNeededAsync(showFeedback: false).ConfigureAwait(true);
    }

    private async Task<bool> SaveTaskDetailsIfNeededAsync(bool showFeedback)
    {
        if (_taskDetailsAutoSaveTask is { IsCompleted: false } currentSave)
        {
            _taskDetailsAutoSaveQueued = TasksPage.IsDetailsPaneOpen && TasksPage.DetailsPanel.HasUnsavedChanges;
            return await currentSave.ConfigureAwait(true);
        }

        if (!TasksPage.IsDetailsPaneOpen || !TasksPage.DetailsPanel.HasUnsavedChanges)
        {
            return true;
        }

        _taskDetailsAutoSaveTask = SaveTaskDetailsLoopAsync(showFeedback);
        try
        {
            return await _taskDetailsAutoSaveTask.ConfigureAwait(true);
        }
        finally
        {
            _taskDetailsAutoSaveTask = null;
        }
    }

    private async Task<bool> SaveTaskDetailsLoopAsync(bool showFeedback)
    {
        do
        {
            _taskDetailsAutoSaveQueued = false;
            if (!TasksPage.IsDetailsPaneOpen || !TasksPage.DetailsPanel.HasUnsavedChanges)
            {
                return true;
            }

            try
            {
                if (!await SaveTaskDetailsAsync(showFeedback).ConfigureAwait(true))
                {
                    return false;
                }
            }
            catch (Exception exception)
            {
                TasksPage.DetailsPanel.SetAutoSaveState("Could not save");
                ShowInfo("Could not save task", exception.Message, InfoBarSeverity.Error);
                return false;
            }

            showFeedback = false;
        }
        while (_taskDetailsAutoSaveQueued || TasksPage.DetailsPanel.HasUnsavedChanges);

        return true;
    }

    private async Task<bool> SaveTaskDetailsAsync(bool showFeedback)
    {
        var draft = CaptureTaskDetailsDraft();
        TaskItem? existing = _selectedTaskId is null ? null : await _store.GetTaskAsync(_selectedTaskId).ConfigureAwait(true);
        if (existing is null && draft.Title.Length == 0)
        {
            TasksPage.DetailsPanel.SetAutoSaveState("Add a title to save");
            if (showFeedback)
            {
                ShowInfo("Title required", "Add a task title before saving.", InfoBarSeverity.Warning);
            }

            return false;
        }

        var integrationId = existing?.IntegrationId ?? IntegrationIds.Local;
        var isLinkedProviderTask = existing?.HasProviderSource == true || existing?.IsProviderTask == true;
        var labelsIntegration = isLinkedProviderTask ? IntegrationIds.Local : integrationId;
        var task = existing is not null
            ? existing with
            {
                Title = existing.IsProviderTask && !existing.HasProviderSource ? existing.Title : draft.Title,
                Status = existing.IsCompleted ? existing.Status : draft.Status,
                ProjectId = draft.ProjectId,
                Priority = draft.Priority,
                PlannedOn = draft.PlannedOn,
                PlannedAt = PreserveExactTime(draft.PlannedOn, existing.PlannedOn, existing.PlannedAt),
                DeadlineOn = draft.DeadlineOn,
                DeadlineAt = PreserveExactTime(draft.DeadlineOn, existing.DeadlineOn, existing.DeadlineAt),
                Notes = EmptyToNull(draft.Notes),
                LocalMetadataJson = WithSourceDateMismatchAcknowledgement(existing.LocalMetadataJson, draft.SourceDateMismatchAcknowledgementKey),
                UpdatedAt = DateTimeOffset.UtcNow,
                Labels = BuildLabels(draft.LabelsText, labelsIntegration),
            }
            : new TaskItem
            {
                Id = existing?.Id ?? $"local_{Guid.NewGuid():N}",
                ExternalId = existing?.ExternalId,
                SpaceId = existing?.SpaceId ?? _currentSpaceId,
                IntegrationId = integrationId,
                Title = draft.Title,
                ProjectId = draft.ProjectId,
                ParentId = existing?.ParentId,
                Priority = draft.Priority,
                Status = existing?.IsCompleted == true ? existing.Status : draft.Status,
                PlannedOn = draft.PlannedOn,
                DeadlineOn = draft.DeadlineOn,
                Notes = EmptyToNull(draft.Notes),
                ProviderMetadataJson = existing?.ProviderMetadataJson,
                LocalMetadataJson = WithSourceDateMismatchAcknowledgement(existing?.LocalMetadataJson, draft.SourceDateMismatchAcknowledgementKey),
                CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = existing?.CompletedAt,
                Labels = BuildLabels(draft.LabelsText, labelsIntegration),
            };

        TasksPage.DetailsPanel.SetAutoSaveState("Saving...");
        await _store.UpsertTaskAsync(task).ConfigureAwait(true);
        _selectedTaskId = task.Id;
        if (!TasksPage.DetailsPanel.MarkSaved(draft.Snapshot))
        {
            _taskDetailsAutoSaveQueued = true;
        }

        await LoadLabelsAsync().ConfigureAwait(true);
        await LoadProjectsAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        TasksPage.DetailsPanel.SetAutoSaveState("Saved", autoDismiss: true);
        if (showFeedback)
        {
            ShowInfo("Task saved", task.Title, InfoBarSeverity.Success);
        }

        return true;
    }

    private static DateTimeOffset? PreserveExactTime(DateOnly? selectedDate, DateOnly? existingDate, DateTimeOffset? existingDateTime)
    {
        if (selectedDate is null || existingDateTime is null)
        {
            return null;
        }

        var exactDate = TaskDateValues.FromDateTimeOffset(existingDateTime);
        return existingDate == selectedDate && exactDate == selectedDate ? existingDateTime : null;
    }

    private static string? WithSourceDateMismatchAcknowledgement(string? localMetadataJson, string? acknowledgementKey)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(localMetadataJson)
                ? new JsonObject()
                : JsonNode.Parse(localMetadataJson)?.AsObject() ?? new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        var openza = root["openza"] as JsonObject;
        if (openza is null)
        {
            openza = new JsonObject();
            root["openza"] = openza;
        }

        if (string.IsNullOrWhiteSpace(acknowledgementKey))
        {
            openza.Remove("sourceDateMismatchAcknowledgementKey");
        }
        else
        {
            openza["sourceDateMismatchAcknowledgementKey"] = acknowledgementKey;
        }

        if (openza.Count == 0)
        {
            root.Remove("openza");
        }

        return root.Count == 0 ? null : root.ToJsonString();
    }

    private static Dictionary<string, string> BuildSubtaskProgress(IReadOnlyList<TaskItem> tasks)
    {
        return tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.ParentId))
            .GroupBy(task => task.ParentId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var total = group.Count();
                    var completed = group.Count(task => task.IsCompleted);
                    return $"{completed}/{total} subtasks";
                },
                StringComparer.Ordinal);
    }

    private static Dictionary<string, string> BuildMatchingSubtaskText(IReadOnlyList<TaskItem> visibleTasks, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return [];
        }

        return visibleTasks
            .Where(task => !string.IsNullOrWhiteSpace(task.ParentId))
            .GroupBy(task => task.ParentId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var matches = group
                        .Select(task => task.Title)
                        .Where(title => !string.IsNullOrWhiteSpace(title))
                        .Distinct(StringComparer.CurrentCultureIgnoreCase)
                        .Take(2)
                        .ToList();
                    if (matches.Count == 0)
                    {
                        return string.Empty;
                    }

                    var remaining = Math.Max(0, group.Count() - matches.Count);
                    var suffix = remaining > 0 ? $" +{remaining}" : string.Empty;
                    var prefix = matches.Count == 1 && remaining == 0 ? "Matching subtask" : "Matching subtasks";
                    return $"{prefix}: {string.Join(", ", matches)}{suffix}";
                },
                StringComparer.Ordinal);
    }

    private async void OnToggleCompleteClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
        {
            return;
        }

        var showFeedback = true;
        if (TryFindTaskRow(sender as DependencyObject, out var row) &&
            row is not null &&
            row.DataContext is TaskListItemViewModel { IsCompleted: false })
        {
            showFeedback = false;
        }

        await ToggleTaskCompletionAsync(id, showFeedback).ConfigureAwait(true);
    }

    private async void OnDetailsToggleCompleteClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskId is null)
        {
            return;
        }

        await ToggleTaskCompletionAsync(_selectedTaskId, showFeedback: true).ConfigureAwait(true);
    }

    private async Task ToggleTaskCompletionAsync(string id, bool showFeedback)
    {
        var task = await _store.GetTaskAsync(id).ConfigureAwait(true);
        if (task is null)
        {
            return;
        }

        var completed = !task.IsCompleted;
        if ((task.HasProviderSource || task.IsProviderTask) &&
            (!string.IsNullOrWhiteSpace(task.SourceProviderTaskId) || !string.IsNullOrWhiteSpace(task.ExternalId)))
        {
            await _store.QueueCompletionAsync(new PendingCompletion
            {
                Id = $"completion_{task.Id}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                TaskId = task.Id,
                Provider = task.SourceIntegrationId ?? task.IntegrationId,
                ProviderTaskId = BuildProviderTaskId(task),
                Completed = completed,
                CompletedAt = completed ? DateTimeOffset.UtcNow : null,
                CreatedAt = DateTimeOffset.UtcNow,
            }).ConfigureAwait(true);
        }

        if (completed)
        {
            await _store.CompleteTaskAsync(task.Id).ConfigureAwait(true);
        }
        else
        {
            await _store.ReopenTaskAsync(task.Id).ConfigureAwait(true);
        }

        await LoadProjectsAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        if (string.Equals(_selectedTaskId, id, StringComparison.Ordinal))
        {
            await LoadTaskDetailsAsync(id).ConfigureAwait(true);
        }

        if (showFeedback)
        {
            ShowInfo(
                completed ? "Task completed" : "Task reopened",
                task.Title,
                completed ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
        }
    }

    private static bool TryFindTaskRow(DependencyObject? element, out TaskRowControl? row)
    {
        while (element is not null)
        {
            if (element is TaskRowControl found)
            {
                row = found;
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        row = null;
        return false;
    }

    private async void OnDeleteTaskClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
        {
            return;
        }

        await DeleteTaskAsync(id).ConfigureAwait(true);
    }

    private async void OnDetailsDeleteTaskClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskId is null)
        {
            return;
        }

        await DeleteTaskAsync(_selectedTaskId).ConfigureAwait(true);
    }

    private async Task DeleteTaskAsync(string id)
    {
        var task = await _store.GetTaskAsync(id).ConfigureAwait(true);
        if (task is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete task",
            Content = task.Title,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _store.DeleteTaskAsync(id).ConfigureAwait(true);
        }
        catch (ProviderLinkedTaskDeleteException exception)
        {
            ShowInfo(
                "Task is linked",
                exception.Message,
                InfoBarSeverity.Warning);
            return;
        }
        catch (Exception exception)
        {
            ShowInfo(
                "Could not delete task",
                exception.Message,
                InfoBarSeverity.Error);
            return;
        }

        if (_selectedTaskId == id)
        {
            _selectedTaskId = null;
            TasksPage.HideDetailsPane();
            TasksPage.ClearTaskSelection();
            TasksPage.DetailsPanel.ClearForNewTask(null, 3, DefaultStatusForCurrentView());
        }

        await LoadProjectsAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnDetailsCancelEditClicked(object sender, RoutedEventArgs e)
    {
        if (!await SavePendingTaskDetailsAsync().ConfigureAwait(true))
        {
            return;
        }

        _selectedTaskId = null;
        TasksPage.HideDetailsPane();
        TasksPage.ClearTaskSelection();
        TasksPage.DetailsPanel.ClearForNewTask(null, 3, DefaultStatusForCurrentView());
    }

    private ProjectItem? DefaultProjectForQuickAdd() =>
        string.Equals(_currentView, "tasks", StringComparison.Ordinal)
            ? GetProject(_selectedProjectId)
            : null;

    private TaskItemStatus DefaultStatusForCurrentView() => _currentView switch
    {
        "next" => TaskItemStatus.Next,
        "waiting" => TaskItemStatus.Waiting,
        "someday" => TaskItemStatus.Someday,
        _ => TaskItemStatus.Inbox,
    };

    private DateTimeOffset? DefaultDateForCurrentView() =>
        string.Equals(_currentView, "today", StringComparison.Ordinal)
            ? DateTimeOffset.Now.Date
            : null;

    private async Task<bool> SavePendingTaskDetailsAsync()
    {
        if (!TasksPage.IsDetailsPaneOpen)
        {
            return true;
        }

        TasksPage.DetailsPanel.StopPendingAutoSave();
        return await SaveTaskDetailsIfNeededAsync(showFeedback: false).ConfigureAwait(true);
    }

    private IReadOnlyList<LabelItem> BuildLabels(string labelText, string integrationId)
    {
        return labelText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Select(name =>
                _allLabels.FirstOrDefault(label =>
                    string.Equals(label.Name, name, StringComparison.CurrentCultureIgnoreCase) &&
                    string.Equals(label.IntegrationId, integrationId, StringComparison.Ordinal)) ??
                _allLabels.FirstOrDefault(label =>
                    string.Equals(label.Name, name, StringComparison.CurrentCultureIgnoreCase)) ??
                new LabelItem
                {
                    Id = $"label_{Guid.NewGuid():N}",
                    IntegrationId = integrationId,
                    Name = name,
                    Color = "#808080",
                    CreatedAt = DateTimeOffset.UtcNow,
                })
            .ToList();
    }

    private static string BuildProviderTaskId(TaskItem task)
    {
        if (!string.IsNullOrWhiteSpace(task.SourceProviderTaskId))
        {
            return task.SourceProviderTaskId;
        }

        if (task.IntegrationId == IntegrationIds.MicrosoftToDo && task.ProjectId?.StartsWith("mstodo_", StringComparison.Ordinal) == true)
        {
            return $"{task.ProjectId["mstodo_".Length..]}|{task.ExternalId}";
        }

        return task.ExternalId ?? task.Id;
    }
}
