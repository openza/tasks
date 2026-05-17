namespace Openza.Tasks.Core.Models;

public sealed record SyncRouteInfo
{
    public string Id { get; init; } = string.Empty;
    public string WorkspaceId { get; init; } = "default";
    public string Name { get; init; } = string.Empty;
    public string? SourceConnectionId { get; init; }
    public string? TargetConnectionId { get; init; }
    public string Mode { get; init; } = "one_way";
    public string Visibility { get; init; } = "optional";
    public string? ScheduleJson { get; init; }
    public bool IsEnabled { get; init; }
    public string? SettingsJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record SyncRouteRunInfo
{
    public string Id { get; init; } = string.Empty;
    public string? RouteId { get; init; }
    public string? ConnectionId { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; init; }
    public string Status { get; init; } = "started";
    public string? SummaryJson { get; init; }
    public string? Error { get; init; }
}
