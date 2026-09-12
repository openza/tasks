using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class TaskListItemViewModel
{
    public TaskListItemViewModel(
        TaskItem task,
        string? projectName,
        TaskListKind viewKind = TaskListKind.Open,
        bool isProjectView = false,
        string subtaskProgressText = "",
        string matchingSubtaskText = "")
    {
        Task = task;
        ProjectName = projectName ?? string.Empty;
        ViewKind = viewKind;
        IsProjectView = isProjectView;
        SubtaskProgressText = subtaskProgressText;
        MatchingSubtaskText = matchingSubtaskText;
    }

    public TaskItem Task { get; }
    public string Title => Task.Title;
    public bool IsCompleted => Task.IsCompleted;
    public string ProjectName { get; }
    public TaskListKind ViewKind { get; }
    public bool IsProjectView { get; }
    public string SubtaskProgressText { get; }
    public string MatchingSubtaskText { get; }
    public string Status => Task.IsCompleted ? "Completed" : FormatStatus(Task.WorkflowStatus);
    public string DateText => FormatDate(Task);
    public string SourceText => IntegrationIds.DisplayName(Task.SourceIntegrationId ?? Task.IntegrationId);
    public string MetadataText => string.Join(" · ", new[]
    {
        IsProjectView ? string.Empty : ProjectName,
        Task.RecurrenceRule is null ? DateText : $"Repeating {DateText}".Trim(),
        StatusText,
        (Task.SourceIntegrationId ?? Task.IntegrationId) == IntegrationIds.Local ? string.Empty : SourceText,
        LabelSummaryText,
        SubtaskProgressText,
        MatchingSubtaskText,
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public bool HasPriority => PriorityText.Length > 0;
    public string PriorityText => Task.Priority switch
    {
        1 => "Urgent",
        2 => "High",
        _ => "",
    };
    public string StatusText => Task.WorkflowStatus switch
    {
        TaskWorkflowStatus.Next when ViewKind != TaskListKind.NextActions => "Next",
        TaskWorkflowStatus.Waiting when ViewKind != TaskListKind.Waiting => "Waiting",
        TaskWorkflowStatus.Someday when ViewKind != TaskListKind.Someday => "Someday",
        TaskWorkflowStatus.Inbox when ViewKind != TaskListKind.Inbox => "Inbox",
        _ => string.Empty,
    };
    public string LabelSummaryText
    {
        get
        {
            var visibleLabels = Task.Labels
                .OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(2)
                .Select(label => $"@{label.Name}")
                .ToList();
            return Task.Labels.Count <= 2
                ? string.Join(", ", visibleLabels)
                : $"{string.Join(", ", visibleLabels)} +{Task.Labels.Count - 2}";
        }
    }

    private static string FormatStatus(TaskWorkflowStatus status) => status switch
    {
        TaskWorkflowStatus.Next => "Next",
        TaskWorkflowStatus.Waiting => "Waiting",
        TaskWorkflowStatus.Someday => "Someday",
        TaskWorkflowStatus.Inbox => "Inbox",
        TaskWorkflowStatus.None => "None",
        _ => "Open",
    };

    private static string FormatDate(TaskItem task)
    {
        var date = task.PlannedOn ?? task.DeadlineOn;
        if (date is null)
        {
            return string.Empty;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        if (date == today)
        {
            return "Today";
        }

        if (date == today.AddDays(1))
        {
            return "Tomorrow";
        }

        return date.Value.ToString("ddd, d MMM");
    }
}
