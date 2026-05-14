using System.Collections.ObjectModel;
using Openza.Tasks.Core.Data;

namespace Openza.Tasks.ViewModels;

public sealed class TaskGroupViewModel
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SortKey { get; set; } = string.Empty;
    public ObservableCollection<TaskListItemViewModel> Tasks { get; } = [];
    public int Count => Tasks.Count;
    public string CountText => Count == 1 ? "1 task" : $"{Count} tasks";

    public static IReadOnlyList<TaskGroupViewModel> Build(IEnumerable<TaskListItemViewModel> tasks, TaskGroupMode mode)
    {
        if (mode == TaskGroupMode.None)
        {
            return [];
        }

        var groups = new Dictionary<string, TaskGroupViewModel>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            foreach (var assignment in TaskGroupBuilder.GetAssignments(task.Task, task.Project, mode))
            {
                if (!groups.TryGetValue(assignment.Key, out var group))
                {
                    group = new TaskGroupViewModel
                    {
                        Key = assignment.Key,
                        Title = assignment.Title,
                        SortKey = assignment.SortKey,
                    };
                    groups.Add(assignment.Key, group);
                }

                group.Tasks.Add(task);
            }
        }

        return groups.Values
            .OrderBy(group => group.SortKey, StringComparer.Ordinal)
            .ThenBy(group => group.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
