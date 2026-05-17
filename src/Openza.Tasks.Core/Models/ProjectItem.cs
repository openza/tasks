namespace Openza.Tasks.Core.Models;

public sealed record ProjectItem
{
    public string Id { get; init; } = string.Empty;
    public string? ExternalId { get; init; }
    public string SpaceId { get; init; } = SpaceIds.Default;
    public string IntegrationId { get; init; } = IntegrationIds.Local;
    public string? ProviderConnectionId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Color { get; init; } = "#808080";
    public string? Icon { get; init; }
    public string? ParentId { get; init; }
    public int SortOrder { get; init; }
    public bool IsFavorite { get; init; }
    public bool IsArchived { get; init; }
    public string Status { get; init; } = ProjectLifecycleStates.Active;
    public string? ProviderMetadataJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }

    public string EffectiveStatus => IsArchived
        ? ProjectLifecycleStates.Archived
        : ProjectLifecycleStates.Normalize(Status);

    public bool IsActive => EffectiveStatus == ProjectLifecycleStates.Active;
    public bool IsCompleted => EffectiveStatus == ProjectLifecycleStates.Completed;
}

public static class SpaceIds
{
    public const string Default = "space_default";
}
