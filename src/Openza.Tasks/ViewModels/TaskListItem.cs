using Openza.Tasks.Core.Models;

namespace Openza.Tasks.ViewModels;

public sealed class TaskListItem(TaskItem task, ProjectItem? project)
{
    public TaskItem Task { get; } = task;
    public string Id => Task.Id;
    public string Title => Task.Title;
    public string ProjectName => project?.Name ?? "No project";
    public string Description => string.IsNullOrWhiteSpace(Task.Description) ? string.Empty : Task.Description;
    public string PriorityText => Task.Priority switch
    {
        1 => "High",
        2 => "Medium",
        3 => "Normal",
        _ => "Low",
    };
    public string StatusText => Task.Status.ToStorageValue().Replace('_', ' ');
    public string DateText => Task.PlannedMoment is null ? "No date" : $"{Task.PlannedMoment:MMM d}";
    public string SourceText => IntegrationIds.DisplayName(Task.IntegrationId);
    public string CompletionGlyph => Task.IsCompleted ? "\uE73D" : "\uE739";
    public string LabelText => Task.Labels.Count == 0 ? string.Empty : string.Join(", ", Task.Labels.Select(l => l.Name));
}
