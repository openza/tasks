using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Application.Tasks;

public sealed record CreateTaskRequest
{
    public required string Title { get; init; }
    public string? Notes { get; init; }
    public string? Space { get; init; }
    public string? Project { get; init; }
    public string? ParentId { get; init; }
    public TaskWorkflowStatus Status { get; init; } = TaskWorkflowStatus.Inbox;
    public bool Completed { get; init; }
    public int Priority { get; init; } = 3;
    public DateOnly? PlannedOn { get; init; }
    public DateOnly? DeadlineOn { get; init; }
    public DateTimeOffset? ReminderAt { get; init; }
    public DateTimeOffset? ScheduledStart { get; init; }
    public int? DurationMinutes { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
}

public sealed record UpdateTaskRequest
{
    public required string TaskId { get; init; }
    public long? ExpectedRevision { get; init; }
    public OptionalValue<string?> Title { get; init; }
    public OptionalValue<string?> Notes { get; init; }
    public OptionalValue<string?> Space { get; init; }
    public OptionalValue<string?> Project { get; init; }
    public OptionalValue<string?> ParentId { get; init; }
    public OptionalValue<TaskWorkflowStatus> Status { get; init; }
    public OptionalValue<bool> Completed { get; init; }
    public OptionalValue<int> Priority { get; init; }
    public OptionalValue<DateOnly?> PlannedOn { get; init; }
    public OptionalValue<DateOnly?> DeadlineOn { get; init; }
    public OptionalValue<DateTimeOffset?> ReminderAt { get; init; }
    public OptionalValue<DateTimeOffset?> ScheduledStart { get; init; }
    public OptionalValue<DateTimeOffset?> ScheduledEnd { get; init; }
    public OptionalValue<int?> DurationMinutes { get; init; }
    public OptionalValue<IReadOnlyList<string>> Labels { get; init; }
    public OptionalValue<string?> LocalMetadataJson { get; init; }
}

public sealed record UpdateProjectRequest
{
    public required string ProjectId { get; init; }
    public OptionalValue<string?> Name { get; init; }
    public OptionalValue<string?> Description { get; init; }
    public OptionalValue<string?> Color { get; init; }
    public OptionalValue<string?> Icon { get; init; }
    public OptionalValue<string?> Status { get; init; }
    public OptionalValue<bool> IsFavorite { get; init; }
}

public sealed class TaskConflictException(string taskId)
    : InvalidOperationException($"Task '{taskId}' changed after it was loaded. Refresh and try again.");

public sealed class OperationRestrictedException(string message) : InvalidOperationException(message);

public sealed class ReferenceResolutionException(string message) : InvalidOperationException(message);
