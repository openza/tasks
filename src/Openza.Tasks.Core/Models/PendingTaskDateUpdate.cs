namespace Openza.Tasks.Core.Models;

public sealed record PendingTaskDateUpdate
{
    public string Id { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string ProviderTaskId { get; init; } = string.Empty;
    public DateOnly? PlannedOn { get; init; }
    public DateTimeOffset? PlannedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public int RetryCount { get; init; }
}
