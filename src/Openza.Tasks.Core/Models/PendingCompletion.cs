namespace Openza.Tasks.Core.Models;

public sealed record PendingCompletion
{
    public string Id { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string ProviderTaskId { get; init; } = string.Empty;
    public bool Completed { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public int RetryCount { get; init; }
}
