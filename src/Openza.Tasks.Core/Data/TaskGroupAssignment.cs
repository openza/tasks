using System.Globalization;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

public sealed record TaskGroupAssignment(string Key, string Title, string SortKey);

public static class TaskGroupBuilder
{
    public static IReadOnlyList<TaskGroupAssignment> GetAssignments(TaskItem task, ProjectItem? project, TaskGroupMode mode)
    {
        return mode switch
        {
            TaskGroupMode.Date => [DateGroup(task)],
            TaskGroupMode.Project => [ProjectGroup(task, project)],
            TaskGroupMode.Status => [StatusGroup(task)],
            TaskGroupMode.Priority => [PriorityGroup(task)],
            TaskGroupMode.Label => LabelGroups(task),
            TaskGroupMode.Source => [SourceGroup(task)],
            TaskGroupMode.Repeating => [RepeatingGroup(task)],
            TaskGroupMode.CreatedDate => [CreatedDateGroup(task)],
            TaskGroupMode.CompletedDate => [CompletedDateGroup(task)],
            _ => [],
        };
    }

    private static TaskGroupAssignment DateGroup(TaskItem task)
    {
        var date = RelevantDate(task);
        return date is null
            ? new TaskGroupAssignment("date:none", "No date", "9")
            : DateAssignment("date", date.Value, ascending: true);
    }

    private static TaskGroupAssignment ProjectGroup(TaskItem task, ProjectItem? project)
    {
        var projectName = project?.Name ?? task.SourceProjectName ?? "No project";
        var key = project?.Id ?? task.ProjectId ?? task.SourceProjectName ?? "no-project";
        return new TaskGroupAssignment($"project:{key}", projectName, $"1:{projectName.ToUpperInvariant()}");
    }

    private static TaskGroupAssignment StatusGroup(TaskItem task)
    {
        return task.Status switch
        {
            TaskItemStatus.Inbox => new TaskGroupAssignment("status:inbox", "Inbox", "1"),
            TaskItemStatus.Next => new TaskGroupAssignment("status:next", "Next", "2"),
            TaskItemStatus.Waiting => new TaskGroupAssignment("status:waiting", "Waiting For", "3"),
            TaskItemStatus.Someday => new TaskGroupAssignment("status:someday", "Someday", "4"),
            TaskItemStatus.Completed => new TaskGroupAssignment("status:completed", "Completed", "8"),
            TaskItemStatus.Cancelled => new TaskGroupAssignment("status:cancelled", "Cancelled", "9"),
            _ => new TaskGroupAssignment("status:inbox", "Inbox", "1"),
        };
    }

    private static TaskGroupAssignment PriorityGroup(TaskItem task)
    {
        return task.Priority switch
        {
            1 => new TaskGroupAssignment("priority:1", "Urgent", "1"),
            2 => new TaskGroupAssignment("priority:2", "High", "2"),
            3 => new TaskGroupAssignment("priority:3", "Normal", "3"),
            _ => new TaskGroupAssignment("priority:4", "Low", "4"),
        };
    }

    private static IReadOnlyList<TaskGroupAssignment> LabelGroups(TaskItem task)
    {
        if (task.Labels.Count == 0)
        {
            return [new TaskGroupAssignment("label:none", "No label", "9")];
        }

        return task.Labels
            .OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(label => new TaskGroupAssignment(
                $"label:{label.Id}",
                label.Name,
                $"1:{label.Name.ToUpperInvariant()}"))
            .ToList();
    }

    private static TaskGroupAssignment SourceGroup(TaskItem task)
    {
        var source = IntegrationIds.DisplayName(task.SourceIntegrationId ?? task.IntegrationId);
        return new TaskGroupAssignment($"source:{task.SourceIntegrationId ?? task.IntegrationId}", source, $"1:{source.ToUpperInvariant()}");
    }

    private static TaskGroupAssignment RepeatingGroup(TaskItem task)
    {
        return string.IsNullOrWhiteSpace(task.RecurrenceRule)
            ? new TaskGroupAssignment("repeating:no", "One-time", "1")
            : new TaskGroupAssignment("repeating:yes", "Repeating", "2");
    }

    private static TaskGroupAssignment CreatedDateGroup(TaskItem task) =>
        DateAssignment("created", task.CreatedAt, ascending: false);

    private static TaskGroupAssignment CompletedDateGroup(TaskItem task)
    {
        return task.CompletedAt is null
            ? new TaskGroupAssignment("completed:none", "No completed date", "9")
            : DateAssignment("completed", task.CompletedAt.Value, ascending: false);
    }

    private static TaskGroupAssignment DateAssignment(string prefix, DateTimeOffset dateTime, bool ascending)
    {
        var date = dateTime.LocalDateTime.Date;
        var title = FormatDateTitle(date);
        var sortTicks = ascending ? date.Ticks : DateTimeOffset.MaxValue.Ticks - date.Ticks;
        return new TaskGroupAssignment(
            $"{prefix}:{date:yyyy-MM-dd}",
            title,
            $"0:{sortTicks:D19}");
    }

    private static DateTimeOffset? RelevantDate(TaskItem task)
    {
        if (task.PlannedMoment is not null)
        {
            return task.PlannedMoment;
        }

        if (task.DeadlineMoment is not null)
        {
            return task.DeadlineMoment;
        }

        return task.ScheduledStart;
    }

    private static string FormatDateTitle(DateTime date)
    {
        var today = DateTime.Today;
        if (date == today)
        {
            return "Today";
        }

        if (date == today.AddDays(1))
        {
            return "Tomorrow";
        }

        if (date == today.AddDays(-1))
        {
            return "Yesterday";
        }

        return date.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }
}
