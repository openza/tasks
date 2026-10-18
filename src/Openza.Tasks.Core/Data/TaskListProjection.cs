using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

/// <summary>SQL-free list membership, ordering, hierarchy and snapshot count policy.</summary>
public static class TaskListProjection
{
    public static IReadOnlyList<TaskItem> Build(IReadOnlyList<TaskItem> tasks, TaskQuery query, DateOnly? localToday = null)
    {
        var today = localToday ?? DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);

        IEnumerable<TaskItem> filtered = tasks.Where(t => t.IntegrationId == IntegrationIds.Local);
        if (!string.IsNullOrWhiteSpace(query.SpaceId))
        {
            filtered = filtered.Where(t => t.SpaceId == query.SpaceId);
        }

        filtered = query.Kind switch
        {
            TaskListKind.Inbox => filtered.Where(t => t.IsOpen && t.WorkflowStatus == TaskWorkflowStatus.Inbox && string.IsNullOrWhiteSpace(t.ProjectId)),
            TaskListKind.NextActions => filtered.Where(t => t.IsOpen && t.WorkflowStatus == TaskWorkflowStatus.Next),
            TaskListKind.Waiting => filtered.Where(t => t.IsOpen && t.WorkflowStatus == TaskWorkflowStatus.Waiting),
            TaskListKind.Someday => filtered.Where(t => t.IsOpen && t.WorkflowStatus == TaskWorkflowStatus.Someday),
            TaskListKind.Today => filtered.Where(t => t.IsOpen && HasRelevantDateOn(t, today, query.DateScope)),
            TaskListKind.Calendar => filtered.Where(t => t.IsOpen && HasRelevantDate(t, query.DateScope)),
            TaskListKind.Overdue => filtered.Where(t => t.IsOpen && HasRelevantDateBefore(t, today, query.DateScope)),
            TaskListKind.Open => filtered.Where(t => t.IsOpen),
            TaskListKind.Completed => filtered.Where(t => t.IsCompleted),
            _ => filtered,
        };

        if (query.RepeatScope != TaskRepeatScope.Include)
        {
            filtered = filtered.Where(t => MatchesRepeatScope(t, query.RepeatScope));
        }

        if (!string.IsNullOrWhiteSpace(query.ProjectId))
        {
            filtered = filtered.Where(t => t.ProjectId == query.ProjectId);
        }

        if (!string.IsNullOrWhiteSpace(query.ParentId))
        {
            filtered = filtered.Where(t => t.ParentId == query.ParentId);
        }

        if (!string.IsNullOrWhiteSpace(query.LabelId) || !string.IsNullOrWhiteSpace(query.LabelName))
        {
            filtered = filtered.Where(t => t.Labels.Any(label =>
                (!string.IsNullOrWhiteSpace(query.LabelId) && label.Id == query.LabelId) ||
                (!string.IsNullOrWhiteSpace(query.LabelName) && string.Equals(label.Name, query.LabelName, StringComparison.CurrentCultureIgnoreCase))));
        }

        if (query.Priority is not null)
        {
            filtered = filtered.Where(t => t.Priority == query.Priority);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var search = query.SearchText.Trim();
            filtered = filtered.Where(t =>
                Contains(t.Title, search) ||
                Contains(t.Notes, search) ||
                Contains(t.SourceDescription, search));
        }

        filtered = SortTasks(filtered, query);

        var sorted = filtered.ToList();
        return query.IncludeSubtasks || !string.IsNullOrWhiteSpace(query.ParentId)
            ? sorted
            : ArrangeWithSubtasks(tasks, sorted, query);
    }

    public static TaskCountSummary Counts(IReadOnlyList<TaskItem> tasks, string? spaceId, DateOnly? localToday = null)
    {
        var today = localToday ?? DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);
        var topLevelTasks = tasks
            .Where(task => task.IntegrationId == IntegrationIds.Local)
            .Where(task => string.IsNullOrWhiteSpace(spaceId) || task.SpaceId == spaceId)
            .Where(task => string.IsNullOrWhiteSpace(task.ParentId))
            .ToList();

        var byProject = topLevelTasks
            .Where(task => task.IsOpen && !string.IsNullOrWhiteSpace(task.ProjectId))
            .GroupBy(task => task.ProjectId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var nextByProject = topLevelTasks
            .Where(task => task.IsOpen && !string.IsNullOrWhiteSpace(task.ProjectId))
            .GroupBy(task => task.ProjectId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(task => task.WorkflowStatus == TaskWorkflowStatus.Next), StringComparer.Ordinal);

        return new TaskCountSummary(
            topLevelTasks.Count(task => task.IsOpen && task.WorkflowStatus == TaskWorkflowStatus.Inbox && string.IsNullOrWhiteSpace(task.ProjectId)),
            topLevelTasks.Count(task => task.IsOpen && task.WorkflowStatus == TaskWorkflowStatus.Next),
            topLevelTasks.Count(task => task.IsOpen && task.WorkflowStatus == TaskWorkflowStatus.Waiting),
            topLevelTasks.Count(task => task.IsOpen && task.WorkflowStatus == TaskWorkflowStatus.Someday),
            topLevelTasks.Count(task => task.IsOpen && HasRelevantDateOn(task, today, TaskDateScope.All)),
            topLevelTasks.Count(task => task.IsOpen && HasRelevantDate(task, TaskDateScope.All)),
            topLevelTasks.Count(task => task.IsOpen && HasRelevantDateBefore(task, today, TaskDateScope.All)),
            topLevelTasks.Count(task => task.IsOpen),
            topLevelTasks.Count,
            topLevelTasks.Count(task => task.IsCompleted),
            byProject,
            nextByProject);
    }

    private static IEnumerable<TaskItem> SortTasks(IEnumerable<TaskItem> tasks, TaskQuery query)
    {
        var descending = query.SortDirection == TaskSortDirection.Descending;
        return query.SortMode switch
        {
            TaskSortMode.Date => descending
                ? tasks.OrderBy(t => RelevantDate(t, query.DateScope) is null).ThenByDescending(t => RelevantDate(t, query.DateScope)).ThenByDescending(t => t.CreatedAt)
                : tasks.OrderBy(t => RelevantDate(t, query.DateScope) is null).ThenBy(t => RelevantDate(t, query.DateScope)).ThenByDescending(t => t.CreatedAt),
            TaskSortMode.CreatedNewest => descending
                ? tasks.OrderBy(t => t.CreatedAt)
                : tasks.OrderByDescending(t => t.CreatedAt),
            TaskSortMode.Title => descending
                ? tasks.OrderByDescending(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                : tasks.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            TaskSortMode.Project => descending
                ? tasks.OrderByDescending(t => t.ProjectId ?? string.Empty, StringComparer.Ordinal).ThenBy(t => t.Priority).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                : tasks.OrderBy(t => t.ProjectId ?? string.Empty, StringComparer.Ordinal).ThenBy(t => t.Priority).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => descending
                ? tasks.OrderBy(t => t.IsCompleted).ThenByDescending(t => t.Priority).ThenBy(t => RelevantDate(t, query.DateScope) is null).ThenByDescending(t => RelevantDate(t, query.DateScope)).ThenByDescending(t => t.CreatedAt)
                : tasks.OrderBy(t => t.IsCompleted).ThenBy(t => t.Priority).ThenBy(t => RelevantDate(t, query.DateScope) is null).ThenBy(t => RelevantDate(t, query.DateScope)).ThenByDescending(t => t.CreatedAt),
        };
    }

    private static IReadOnlyList<TaskItem> ArrangeWithSubtasks(
        IReadOnlyList<TaskItem> allTasks,
        IReadOnlyList<TaskItem> matchedTasks,
        TaskQuery query)
    {
        var byId = allTasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var childrenByParent = allTasks
            .Where(task => !string.IsNullOrWhiteSpace(task.ParentId))
            .Where(task => task.IntegrationId == IntegrationIds.Local)
            .Where(task => string.IsNullOrWhiteSpace(query.SpaceId) || task.SpaceId == query.SpaceId)
            .GroupBy(task => task.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(task => task.IsCompleted).ThenBy(task => task.Priority).ThenBy(task => task.CreatedAt).ToList(), StringComparer.Ordinal);
        var matchedIds = matchedTasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        var added = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TaskItem>();

        foreach (var task in matchedTasks)
        {
            if (!string.IsNullOrWhiteSpace(task.ParentId) && byId.TryGetValue(task.ParentId, out var parent))
            {
                AddTask(parent, includeAllChildren: false);
                continue;
            }

            AddTask(task, includeAllChildren: true);
        }

        return result;

        void AddTask(TaskItem task, bool includeAllChildren)
        {
            if (!added.Add(task.Id))
            {
                return;
            }

            result.Add(task);
            if (!childrenByParent.TryGetValue(task.Id, out var children))
            {
                return;
            }

            foreach (var child in children)
            {
                if (includeAllChildren || matchedIds.Contains(child.Id))
                {
                    AddTask(child, includeAllChildren: true);
                }
            }
        }
    }

    private static bool HasRelevantDate(TaskItem task, TaskDateScope scope)
    {
        return RelevantDates(task, scope).Any();
    }

    private static bool MatchesRepeatScope(TaskItem task, TaskRepeatScope scope)
    {
        var isRepeating = !string.IsNullOrWhiteSpace(task.EffectiveRecurrenceRule);
        return scope switch
        {
            TaskRepeatScope.Exclude => !isRepeating,
            TaskRepeatScope.Only => isRepeating,
            _ => true,
        };
    }

    private static bool HasRelevantDateOn(TaskItem task, DateOnly date, TaskDateScope scope)
    {
        return RelevantDates(task, scope).Any(value => DateOnly.FromDateTime(value.LocalDateTime) == date);
    }

    private static bool HasRelevantDateBefore(TaskItem task, DateOnly date, TaskDateScope scope)
    {
        return RelevantDates(task, scope).Any(value => DateOnly.FromDateTime(value.LocalDateTime) < date);
    }

    private static DateTimeOffset? RelevantDate(TaskItem task, TaskDateScope scope)
    {
        var dates = RelevantDates(task, scope).ToList();
        return dates.Count == 0 ? null : dates.Min();
    }

    private static IEnumerable<DateTimeOffset> RelevantDates(TaskItem task, TaskDateScope scope)
    {
        if (task.PlannedMoment is { } planned)
        {
            yield return planned;
        }

        if (task.DeadlineMoment is { } deadline)
        {
            yield return deadline;
        }

        if (task.ScheduledStart is { } scheduledStart)
        {
            yield return scheduledStart;
        }
    }

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;
}
