namespace Openza.Tasks.Core.Models;

public sealed record TaskItem
{
    public string Id { get; init; } = string.Empty;
    public string? ExternalId { get; init; }
    public string IntegrationId { get; init; } = IntegrationIds.Local;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? ProjectId { get; init; }
    public string? ParentId { get; init; }
    public int Priority { get; init; } = 2;
    public TaskItemStatus Status { get; init; } = TaskItemStatus.None;
    public DateTimeOffset? DueDate { get; init; }
    public string? DueTime { get; init; }
    public string? Notes { get; init; }
    public string? ProviderMetadataJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public IReadOnlyList<LabelItem> Labels { get; init; } = [];

    public bool IsCompleted => Status == TaskItemStatus.Completed;
    public bool IsOpen => Status.IsOpen();
    public bool IsProviderTask => IntegrationId != IntegrationIds.Local;
}
