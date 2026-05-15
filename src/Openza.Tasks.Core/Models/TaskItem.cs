namespace Openza.Tasks.Core.Models;

public sealed record TaskItem
{
    private TaskCompletionState _completionState = TaskCompletionState.Open;
    private TaskWorkflowStatus _workflowStatus = TaskWorkflowStatus.Inbox;

    public string Id { get; init; } = string.Empty;
    public string? ExternalId { get; init; }
    public string SpaceId { get; init; } = SpaceIds.Default;
    public string IntegrationId { get; init; } = IntegrationIds.Local;
    public string? ProviderConnectionId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? ProjectId { get; init; }
    public string? ParentId { get; init; }
    public int Priority { get; init; } = 2;
    public string? SourceIntegrationId { get; init; }
    public string? SourceConnectionId { get; init; }
    public string? SourceExternalId { get; init; }
    public string? SourceProviderTaskId { get; init; }
    public string? SourceUrl { get; init; }
    public string? SourceDescription { get; init; }
    public string? SourceProjectName { get; init; }
    public int? SourcePriority { get; init; }
    public DateOnly? SourcePlannedOn { get; init; }
    public DateTimeOffset? SourcePlannedAt { get; init; }
    public DateOnly? SourceDeadlineOn { get; init; }
    public DateTimeOffset? SourceDeadlineAt { get; init; }

    public TaskCompletionState CompletionState
    {
        get => _completionState;
        init => _completionState = value;
    }

    public TaskWorkflowStatus WorkflowStatus
    {
        get => _workflowStatus;
        init => _workflowStatus = value;
    }

    public TaskItemStatus Status
    {
        get => _workflowStatus.ToLegacyStatus(_completionState);
        init
        {
            _completionState = value.ToCompletionState();
            _workflowStatus = value.ToWorkflowStatus();
        }
    }

    public DateOnly? PlannedOn { get; init; }
    public DateTimeOffset? PlannedAt { get; init; }
    public DateOnly? DeadlineOn { get; init; }
    public DateTimeOffset? DeadlineAt { get; init; }
    public DateTimeOffset? ScheduledStart { get; init; }
    public DateTimeOffset? ScheduledEnd { get; init; }
    public int? DurationMinutes { get; init; }
    public string? RecurrenceRule { get; init; }
    public string? Notes { get; init; }
    public string? ProviderMetadataJson { get; init; }
    public string? SourceMetadataJson { get; init; }
    public string? LocalMetadataJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public IReadOnlyList<LabelItem> Labels { get; init; } = [];

    public bool IsCompleted => CompletionState == TaskCompletionState.Completed;
    public bool IsOpen => CompletionState == TaskCompletionState.Open;
    public bool IsProviderTask => IntegrationId != IntegrationIds.Local;
    public bool HasProviderSource => !string.IsNullOrWhiteSpace(SourceIntegrationId) && !string.IsNullOrWhiteSpace(SourceExternalId);
    public DateTimeOffset? PlannedMoment => TaskDateValues.PreferredMoment(PlannedOn, PlannedAt);
    public DateTimeOffset? DeadlineMoment => TaskDateValues.PreferredMoment(DeadlineOn, DeadlineAt);
    public DateTimeOffset? SourcePlannedMoment => TaskDateValues.PreferredMoment(SourcePlannedOn, SourcePlannedAt);
    public DateTimeOffset? SourceDeadlineMoment => TaskDateValues.PreferredMoment(SourceDeadlineOn, SourceDeadlineAt);
}
