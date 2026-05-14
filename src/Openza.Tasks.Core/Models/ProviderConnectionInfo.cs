namespace Openza.Tasks.Core.Models;

public sealed record ProviderConnectionInfo
{
    public string Id { get; init; } = string.Empty;
    public string WorkspaceId { get; init; } = "default";
    public string IntegrationId { get; init; } = IntegrationIds.Local;
    public string DisplayName { get; init; } = string.Empty;
    public string? AccountKey { get; init; }
    public string Status { get; init; } = "disconnected";
    public string? SettingsJson { get; init; }
    public DateTimeOffset? LastSyncAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
}
