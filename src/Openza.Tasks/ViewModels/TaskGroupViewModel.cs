using System.Collections.ObjectModel;
using Openza.Tasks.Core.Data;

namespace Openza.Tasks.ViewModels;

public sealed class TaskGroupViewModel
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SortKey { get; set; } = string.Empty;
    public bool IsExpanded { get; set; } = true;
    public ObservableCollection<TaskListItemViewModel> Tasks { get; } = [];
    public int Count => Tasks.Count(task => !task.IsSubtask);
    public string CountText => Count == 1 ? "1 task" : $"{Count} tasks";

    public static IReadOnlyList<TaskGroupViewModel> Build(
        IEnumerable<TaskListItemViewModel> tasks,
        TaskGroupMode mode,
        IReadOnlyDictionary<string, bool>? previousExpansionStates = null)
    {
        if (mode == TaskGroupMode.None)
        {
            return [];
        }

        var groups = new Dictionary<string, TaskGroupViewModel>(StringComparer.Ordinal);
        var groupsByTaskId = new Dictionary<string, IReadOnlyList<TaskGroupViewModel>>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            var targetGroups = task.IsSubtask &&
                !string.IsNullOrWhiteSpace(task.Task.ParentId) &&
                groupsByTaskId.TryGetValue(task.Task.ParentId, out var parentGroups)
                    ? parentGroups
                    : GetOrCreateGroups(task);

            groupsByTaskId[task.Id] = targetGroups;
            foreach (var group in targetGroups)
            {
                group.Tasks.Add(task);
            }
        }

        return groups.Values
            .OrderBy(group => group.SortKey, StringComparer.Ordinal)
            .ThenBy(group => group.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        IReadOnlyList<TaskGroupViewModel> GetOrCreateGroups(TaskListItemViewModel task)
        {
            var taskGroups = new List<TaskGroupViewModel>();
            foreach (var assignment in TaskGroupBuilder.GetAssignments(task.Task, task.Project, mode))
            {
                if (!groups.TryGetValue(assignment.Key, out var group))
                {
                    group = new TaskGroupViewModel
                    {
                        Key = assignment.Key,
                        Title = assignment.Title,
                        SortKey = assignment.SortKey,
                        IsExpanded = previousExpansionStates?.TryGetValue(assignment.Key, out var isExpanded) != true || isExpanded,
                    };
                    groups.Add(assignment.Key, group);
                }

                taskGroups.Add(group);
            }

            return taskGroups;
        }
    }
}
