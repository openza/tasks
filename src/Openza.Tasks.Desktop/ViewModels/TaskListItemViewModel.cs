using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class TaskListItemViewModel
{
    public TaskListItemViewModel(TaskItem task, string? projectName)
    {
        Task = task;
        ProjectName = projectName ?? string.Empty;
    }

    public TaskItem Task { get; }
    public string Title => Task.Title;
    public bool IsCompleted => Task.IsCompleted;
    public string ProjectName { get; }
    public string Status => Task.IsCompleted ? "Completed" : FormatStatus(Task.WorkflowStatus);
    public string DateText => FormatDate(Task);
    public string SourceText => IntegrationIds.DisplayName(Task.SourceIntegrationId ?? Task.IntegrationId);
    public string MetadataText => string.Join(" · ", new[]
    {
        ProjectName,
        Task.RecurrenceRule is null ? DateText : $"Repeating {DateText}".Trim(),
        SourceText == "Local" ? string.Empty : SourceText,
        Task.Labels.Count == 0 ? string.Empty : string.Join(", ", Task.Labels.Select(label => $"@{label.Name}")),
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public bool HasPriority => PriorityText.Length > 0;
    public string PriorityText => Task.Priority switch
    {
        1 => "P1",
        2 => "P2",
        3 => "P3",
        _ => "",
    };

    private static string FormatStatus(TaskWorkflowStatus status) => status switch
    {
        TaskWorkflowStatus.Next => "Next",
        TaskWorkflowStatus.Waiting => "Waiting",
        TaskWorkflowStatus.Someday => "Someday",
        TaskWorkflowStatus.Inbox => "Inbox",
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
