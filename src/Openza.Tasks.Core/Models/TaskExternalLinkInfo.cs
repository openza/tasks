namespace Openza.Tasks.Core.Models;

public static class TaskExternalLinkKinds
{
    public const string Issue = "issue";
}

public sealed record TaskExternalLinkInfo
{
    public string Id { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string IntegrationId { get; init; } = string.Empty;
    public string? ConnectionId { get; init; }
    public string ExternalId { get; init; } = string.Empty;
    public string Kind { get; init; } = TaskExternalLinkKinds.Issue;
    public string DisplayName { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? MetadataJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
}
