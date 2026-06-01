using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.ViewModels;

public sealed class TasksViewModel : ObservableObject
{
    private string _title = "Inbox";
    private string _subtitle = "0 tasks";
    private bool _isEmpty = true;
    private bool _isGetStartedVisible;
    private string _emptyTitle = "Nothing here";
    private string _emptyMessage = "Add a task or change your filters.";
    private string _searchText = string.Empty;
    private ProjectItem? _selectedProject;
    private bool _isGrouped;
    private TaskGroupMode _currentGroupMode = TaskGroupMode.None;
    private bool _areRowQuickActionsEnabled = true;

    public ObservableCollection<TaskListItemViewModel> Tasks { get; } = [];
    public ObservableCollection<TaskGroupViewModel> TaskGroups { get; } = [];
    public ObservableCollection<TaskListEntryViewModel> TaskEntries { get; } = [];
    public ObservableCollection<ProjectGroupViewModel> ProjectGroups { get; } = [];
    public ObservableCollection<ProjectPaneEntryViewModel> ProjectEntries { get; } = [];
    public ObservableCollection<ProjectListItemViewModel> ProjectOptions { get; } = [];
    public ObservableCollection<LabelItem> LabelOptions { get; } = [];

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        set
        {
            if (SetProperty(ref _isEmpty, value))
            {
                OnPropertyChanged(nameof(EmptyVisibility));
                OnPropertyChanged(nameof(ListVisibility));
                OnPropertyChanged(nameof(UngroupedListVisibility));
                OnPropertyChanged(nameof(GroupedListVisibility));
            }
        }
    }

    public Visibility EmptyVisibility => IsEmpty && !IsGetStartedVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ListVisibility => IsEmpty ? Visibility.Collapsed : Visibility.Visible;

    public Visibility UngroupedListVisibility => !IsEmpty && !IsGrouped ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GroupedListVisibility => !IsEmpty && IsGrouped ? Visibility.Visible : Visibility.Collapsed;

    public bool IsGetStartedVisible
    {
        get => _isGetStartedVisible;
        set
        {
            if (SetProperty(ref _isGetStartedVisible, value))
            {
                OnPropertyChanged(nameof(EmptyVisibility));
            }
        }
    }

    public string EmptyTitle
    {
        get => _emptyTitle;
        set => SetProperty(ref _emptyTitle, value);
    }

    public string EmptyMessage
    {
        get => _emptyMessage;
        set => SetProperty(ref _emptyMessage, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public ProjectItem? SelectedProject
    {
        get => _selectedProject;
        set => SetProperty(ref _selectedProject, value);
    }

    public bool IsGrouped
    {
        get => _isGrouped;
        set
        {
            if (SetProperty(ref _isGrouped, value))
            {
                OnPropertyChanged(nameof(UngroupedListVisibility));
                OnPropertyChanged(nameof(GroupedListVisibility));
            }
        }
    }

    public void SetRowQuickActionsEnabled(bool enabled)
    {
        if (_areRowQuickActionsEnabled == enabled)
        {
            return;
        }

        _areRowQuickActionsEnabled = enabled;
        foreach (var task in Tasks)
        {
            task.AreQuickActionsEnabled = enabled;
        }
    }

    public void SetTasks(IEnumerable<TaskListItemViewModel> tasks, TaskGroupMode groupMode)
    {
        var taskItems = tasks.ToList();
        foreach (var task in taskItems)
        {
            task.AreQuickActionsEnabled = _areRowQuickActionsEnabled;
        }

        SyncTasks(Tasks, taskItems);

        var expansionStates = TaskGroups.ToDictionary(group => group.Key, group => group.IsExpanded, StringComparer.Ordinal);
        TaskGroups.Clear();
        foreach (var group in TaskGroupViewModel.Build(Tasks, groupMode, expansionStates))
        {
            TaskGroups.Add(group);
        }

        _currentGroupMode = groupMode;
        IsGrouped = groupMode != TaskGroupMode.None;
        SyncTaskEntries(TaskEntries, BuildTaskEntries(groupMode));
        IsEmpty = taskItems.Count == 0;
    }

    public void ToggleTaskGroup(string key)
    {
        var group = TaskGroups.FirstOrDefault(group => string.Equals(group.Key, key, StringComparison.Ordinal));
        if (group is null)
        {
            return;
        }

        group.IsExpanded = !group.IsExpanded;
        SyncTaskEntries(TaskEntries, BuildTaskEntries(_currentGroupMode));
    }

    public void SetProjectGroups(IEnumerable<ProjectGroupViewModel> groups)
    {
        var expansionStates = ProjectGroups.ToDictionary(group => group.Id, group => group.IsExpanded, StringComparer.Ordinal);

        ProjectGroups.Clear();
        foreach (var group in groups)
        {
            if (expansionStates.TryGetValue(group.Id, out var isExpanded))
            {
                group.IsExpanded = isExpanded;
            }

            ProjectGroups.Add(group);
        }

        SyncProjectEntries(ProjectEntries, BuildProjectEntries());
    }

    public void ToggleProjectGroup(string id)
    {
        var group = ProjectGroups.FirstOrDefault(group => string.Equals(group.Id, id, StringComparison.Ordinal));
        if (group is null)
        {
            return;
        }

        group.IsExpanded = !group.IsExpanded;
        SyncProjectEntries(ProjectEntries, BuildProjectEntries());
    }

    private IReadOnlyList<TaskListEntryViewModel> BuildTaskEntries(TaskGroupMode groupMode)
    {
        if (groupMode == TaskGroupMode.None)
        {
            return Tasks.Select(task => TaskListEntryViewModel.ForTask(task)).ToList();
        }

        var entries = new List<TaskListEntryViewModel>();
        foreach (var group in TaskGroups)
        {
            entries.Add(TaskListEntryViewModel.ForGroup(group));
            if (!group.IsExpanded)
            {
                continue;
            }

            foreach (var task in group.Tasks)
            {
                entries.Add(TaskListEntryViewModel.ForTask(task, group.Key));
            }
        }

        return entries;
    }

    private IReadOnlyList<ProjectPaneEntryViewModel> BuildProjectEntries()
    {
        var entries = new List<ProjectPaneEntryViewModel>();
        foreach (var group in ProjectGroups)
        {
            if (group.ShowHeader)
            {
                entries.Add(ProjectPaneEntryViewModel.ForGroup(group));
            }

            if (!group.IsExpanded)
            {
                continue;
            }

            foreach (var project in group.Projects)
            {
                entries.Add(ProjectPaneEntryViewModel.ForProject(project, group.Id));
            }
        }

        return entries;
    }

    private static void SyncTasks(ObservableCollection<TaskListItemViewModel> target, IReadOnlyList<TaskListItemViewModel> source)
    {
        for (var index = 0; index < source.Count; index++)
        {
            if (index >= target.Count)
            {
                target.Add(source[index]);
                continue;
            }

            if (string.Equals(target[index].Id, source[index].Id, StringComparison.Ordinal))
            {
                target[index].UpdateFrom(source[index]);
                continue;
            }

            var existingIndex = -1;
            for (var candidate = index + 1; candidate < target.Count; candidate++)
            {
                if (string.Equals(target[candidate].Id, source[index].Id, StringComparison.Ordinal))
                {
                    existingIndex = candidate;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                var existing = target[existingIndex];
                target.RemoveAt(existingIndex);
                target.Insert(index, existing);
                existing.UpdateFrom(source[index]);
            }
            else
            {
                target.Insert(index, source[index]);
            }
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static void SyncTaskEntries(ObservableCollection<TaskListEntryViewModel> target, IReadOnlyList<TaskListEntryViewModel> source)
    {
        SyncEntries(target, source, entry => entry.Key, (targetEntry, sourceEntry) => targetEntry.UpdateFrom(sourceEntry));
    }

    private static void SyncProjectEntries(ObservableCollection<ProjectPaneEntryViewModel> target, IReadOnlyList<ProjectPaneEntryViewModel> source)
    {
        SyncEntries(target, source, entry => entry.Key, (targetEntry, sourceEntry) => targetEntry.UpdateFrom(sourceEntry));
    }

    private static void SyncEntries<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> source,
        Func<T, string> keySelector,
        Action<T, T> update)
    {
        for (var index = 0; index < source.Count; index++)
        {
            if (index >= target.Count)
            {
                target.Add(source[index]);
                continue;
            }

            if (string.Equals(keySelector(target[index]), keySelector(source[index]), StringComparison.Ordinal))
            {
                update(target[index], source[index]);
                continue;
            }

            var existingIndex = -1;
            for (var candidate = index + 1; candidate < target.Count; candidate++)
            {
                if (string.Equals(keySelector(target[candidate]), keySelector(source[index]), StringComparison.Ordinal))
                {
                    existingIndex = candidate;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                var existing = target[existingIndex];
                target.RemoveAt(existingIndex);
                target.Insert(index, existing);
                update(existing, source[index]);
            }
            else
            {
                target.Insert(index, source[index]);
            }
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
