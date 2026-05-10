using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Pages;
using Openza.Tasks.ViewModels;
using Windows.ApplicationModel.DataTransfer;

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

        var projects = _allProjects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var selectedProject = _currentView == "tasks" ? GetSelectedProject() : null;
        var query = new TaskQuery
        {
            Kind = _currentView switch
            {
                "inbox" => TaskListKind.Inbox,
                "today" => TaskListKind.Today,
                "overdue" => TaskListKind.Overdue,
                "waiting" => TaskListKind.Waiting,
                "someday" => TaskListKind.Someday,
                "completed" => TaskListKind.Completed,
                "tasks" => TaskListKind.Open,
                _ => TaskListKind.NextActions,
            },
            ProjectId = selectedProject?.Id,
            LabelId = _labelFilterId,
            SearchText = TasksPage.SearchText,
            SortMode = _sortMode,
            Priority = _priorityFilter,
        };

        var tasks = await _store.GetTasksAsync(query).ConfigureAwait(true);
        TasksPage.ViewModel.Tasks.Clear();
        foreach (var task in tasks)
        {
            projects.TryGetValue(task.ProjectId ?? string.Empty, out var project);
            TasksPage.ViewModel.Tasks.Add(new TaskListItemViewModel(task, project));
        }

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

        var title = selectedProject?.Name ?? _currentView switch
        {
            "inbox" => "Inbox",
            "today" => "Today",
            "overdue" => "Overdue",
            "waiting" => "Waiting For",
            "someday" => "Someday",
            "completed" => "Completed",
            "tasks" => "Tasks",
            _ => "Next Actions",
        };
        var taskCount = TasksPage.ViewModel.Tasks.Count;
        var subtitle = $"{taskCount} task{(taskCount == 1 ? string.Empty : "s")}";
        if (selectedProject is not null)
        {
            subtitle = $"{subtitle} · {SourceName(selectedProject.IntegrationId)}";
        }

        TasksPage.ViewModel.SelectedProject = selectedProject;
        TasksPage.SetHeader(title, subtitle, selectedProject is not null);
        TasksPage.ViewModel.IsEmpty = TasksPage.ViewModel.Tasks.Count == 0;
    }

    private async void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_uiReady)
        {
            return;
        }

        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        _sortMode = TasksPage.SortTag switch
        {
            "due" => TaskSortMode.DueDate,
            "created" => TaskSortMode.CreatedNewest,
            "title" => TaskSortMode.Title,
            _ => TaskSortMode.PriorityThenDueDate,
        };
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnPriorityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        _priorityFilter = TasksPage.PriorityFilter;
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnLabelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        _labelFilterId = TasksPage.LabelFilterId;
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnTaskSelected(TasksPage sender, TaskListItemViewModel item)
    {
        if (!await ConfirmDiscardTaskEditsAsync().ConfigureAwait(true))
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
        TasksPage.DetailsPanel.LoadTask(task, GetProject(task.ProjectId));
        TasksPage.SelectTask(task.Id);
        TasksPage.ShowDetailsPane();
    }

    private async void OnQuickAddClicked(object sender, RoutedEventArgs e)
    {
        var quickAdd = await TasksPage.ShowQuickAddAsync(DefaultProjectForQuickAdd(), DefaultStatusForCurrentView(), DefaultDueDateForCurrentView()).ConfigureAwait(true);
        if (quickAdd is null)
        {
            return;
        }

        var task = new TaskItem
        {
            Id = $"local_{Guid.NewGuid():N}",
            IntegrationId = IntegrationIds.Local,
            Title = quickAdd.Title,
            ProjectId = quickAdd.ProjectId,
            Priority = quickAdd.Priority,
            DueDate = quickAdd.DueDate,
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

    private async void OnSaveTaskClicked(object sender, RoutedEventArgs e)
    {
        var title = TasksPage.DetailsPanel.TitleText;
        TaskItem? existing = _selectedTaskId is null ? null : await _store.GetTaskAsync(_selectedTaskId).ConfigureAwait(true);
        if (existing is null && title.Length == 0)
        {
            ShowInfo("Title required", "Add a task title before saving.", InfoBarSeverity.Warning);
            return;
        }

        var integrationId = existing?.IntegrationId ?? IntegrationIds.Local;
        var labelsIntegration = existing?.IsProviderTask == true ? IntegrationIds.Local : integrationId;
        var task = existing?.IsProviderTask == true
            ? existing with
            {
                Status = existing.IsCompleted ? existing.Status : TasksPage.DetailsPanel.SelectedStatus,
                Notes = EmptyToNull(TasksPage.DetailsPanel.NotesText),
                UpdatedAt = DateTimeOffset.UtcNow,
                Labels = BuildLabels(TasksPage.DetailsPanel.LabelsText, labelsIntegration),
            }
            : new TaskItem
            {
                Id = existing?.Id ?? $"local_{Guid.NewGuid():N}",
                ExternalId = existing?.ExternalId,
                IntegrationId = integrationId,
                Title = title,
                Description = EmptyToNull(TasksPage.DetailsPanel.DescriptionText),
                ProjectId = TasksPage.DetailsPanel.SelectedProject?.Id,
                Priority = TasksPage.DetailsPanel.SelectedPriority,
                Status = existing?.IsCompleted == true ? existing.Status : TasksPage.DetailsPanel.SelectedStatus,
                DueDate = TasksPage.DetailsPanel.SelectedDueDate,
                Notes = EmptyToNull(TasksPage.DetailsPanel.NotesText),
                ProviderMetadataJson = existing?.ProviderMetadataJson,
                CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = existing?.CompletedAt,
                Labels = BuildLabels(TasksPage.DetailsPanel.LabelsText, labelsIntegration),
            };

        await _store.UpsertTaskAsync(task).ConfigureAwait(true);
        _selectedTaskId = task.Id;
        TasksPage.DetailsPanel.ResetDirtyState();
        await LoadLabelsAsync().ConfigureAwait(true);
        await LoadProjectsAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        ShowInfo(existing?.IsProviderTask == true ? "Local fields saved" : "Task saved", task.Title, InfoBarSeverity.Success);
    }

    private async void OnToggleCompleteClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
        {
            return;
        }

        await ToggleTaskCompletionAsync(id).ConfigureAwait(true);
    }

    private async void OnDetailsToggleCompleteClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskId is null)
        {
            return;
        }

        await ToggleTaskCompletionAsync(_selectedTaskId).ConfigureAwait(true);
    }

    private async Task ToggleTaskCompletionAsync(string id)
    {
        var task = await _store.GetTaskAsync(id).ConfigureAwait(true);
        if (task is null)
        {
            return;
        }

        var completed = !task.IsCompleted;
        if (task.IsProviderTask && !string.IsNullOrWhiteSpace(task.ExternalId))
        {
            await _store.QueueCompletionAsync(new PendingCompletion
            {
                Id = $"completion_{task.Id}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                TaskId = task.Id,
                Provider = task.IntegrationId,
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

        await _store.DeleteTaskAsync(id).ConfigureAwait(true);
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

    private void OnCopyTaskIdClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(id);
        Clipboard.SetContent(package);
        ShowInfo("Copied", id, InfoBarSeverity.Informational);
    }

    private async void OnDetailsCancelEditClicked(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardTaskEditsAsync().ConfigureAwait(true))
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
        _ => TaskItemStatus.None,
    };

    private DateTimeOffset? DefaultDueDateForCurrentView() =>
        string.Equals(_currentView, "today", StringComparison.Ordinal)
            ? DateTimeOffset.Now.Date
            : null;

    private async Task<bool> ConfirmDiscardTaskEditsAsync()
    {
        if (!TasksPage.IsDetailsPaneOpen || !TasksPage.DetailsPanel.HasUnsavedChanges)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            Title = "Discard unsaved changes?",
            Content = "The selected task has unsaved changes.",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Keep editing",
            XamlRoot = XamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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
        if (task.IntegrationId == IntegrationIds.MicrosoftToDo && task.ProjectId?.StartsWith("mstodo_", StringComparison.Ordinal) == true)
        {
            return $"{task.ProjectId["mstodo_".Length..]}|{task.ExternalId}";
        }

        return task.ExternalId ?? task.Id;
    }
}
