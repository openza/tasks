using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Application.Tasks;

public sealed record CreateTaskRequest
{
    public required string Title { get; init; }
    public string? Notes { get; init; }
    public string? Space { get; init; }
    public string? Project { get; init; }
    public TaskWorkflowStatus Status { get; init; } = TaskWorkflowStatus.Inbox;
    public bool Completed { get; init; }
    public int Priority { get; init; } = 3;
    public DateOnly? PlannedOn { get; init; }
    public DateOnly? DeadlineOn { get; init; }
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
    public OptionalValue<TaskWorkflowStatus> Status { get; init; }
    public OptionalValue<bool> Completed { get; init; }
    public OptionalValue<int> Priority { get; init; }
    public OptionalValue<DateOnly?> PlannedOn { get; init; }
    public OptionalValue<DateOnly?> DeadlineOn { get; init; }
    public OptionalValue<IReadOnlyList<string>> Labels { get; init; }
    public OptionalValue<string?> LocalMetadataJson { get; init; }
}

public sealed class TaskConflictException(string taskId)
    : InvalidOperationException($"Task '{taskId}' changed after it was loaded. Refresh and try again.");

public sealed class ReferenceResolutionException(string message) : InvalidOperationException(message);
