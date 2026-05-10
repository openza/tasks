using Openza.Tasks.Core.Models;
using Microsoft.UI.Xaml;

namespace Openza.Tasks.ViewModels;

public sealed class TaskListItemViewModel(TaskItem task, ProjectItem? project)
{
    public TaskItem Task { get; } = task;
    public ProjectItem? Project { get; } = project;

    public string Id => Task.Id;
    public string Title => Task.Title;
    public string Description => Task.Description ?? string.Empty;
    public string ProjectName => Project?.Name ?? "Inbox";
    public string SourceText => SourceName(Task.IntegrationId);
    public string PriorityText => Task.Priority switch
    {
        1 => "Urgent",
        2 => "High",
        3 => "Normal",
        _ => "Low",
    };
    public string DueText => Task.DueDate is null ? string.Empty : FormatDue(Task.DueDate.Value);
    public string LabelText => Task.Labels.Count == 0 ? string.Empty : string.Join(", ", Task.Labels.Select(label => label.Name));
    public string StatusText => Task.Status switch
    {
        TaskItemStatus.Next => "Next",
        TaskItemStatus.Waiting => "Waiting",
        TaskItemStatus.Someday => "Someday",
        _ => string.Empty,
    };
    public string SummaryText => string.Join("  ", new[] { ProjectName, PriorityText, DueText, SourceText }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string MetadataText => string.Join("  /  ", new[] { ProjectName, PriorityText, DueText, SourceText }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string SecondaryText => !string.IsNullOrWhiteSpace(Description) ? Description : LabelText;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasLabels => !string.IsNullOrWhiteSpace(LabelText);
    public Visibility StatusVisibility => string.IsNullOrWhiteSpace(StatusText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SecondaryTextVisibility => string.IsNullOrWhiteSpace(SecondaryText) ? Visibility.Collapsed : Visibility.Visible;
    public bool IsCompleted => Task.IsCompleted;
    public bool IsProviderTask => Task.IsProviderTask;
    public bool IsOverdue => Task.DueDate is not null && !Task.IsCompleted && Task.DueDate.Value.LocalDateTime.Date < DateTimeOffset.Now.Date;
    public string CompletionAutomationName => Task.IsCompleted ? "Reopen task" : "Complete task";

    public static string SourceName(string integrationId) => integrationId switch
    {
        IntegrationIds.Todoist => "Todoist",
        IntegrationIds.MicrosoftToDo => "Microsoft To Do",
        _ => "Openza Tasks",
    };

    private static string FormatDue(DateTimeOffset dueDate)
    {
        var date = dueDate.LocalDateTime.Date;
        var today = DateTimeOffset.Now.Date;
        if (date == today)
        {
            return "Today";
        }

        if (date == today.AddDays(1))
        {
            return "Tomorrow";
        }

        if (date < today)
        {
            var days = (today - date).Days;
            return days == 1 ? "1 day ago" : $"{days} days ago";
        }

        return dueDate.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture);
    }
}
