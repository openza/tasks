namespace Openza.Tasks.Core.Models;

public sealed record LabelItem
{
    public string Id { get; init; } = string.Empty;
    public string? ExternalId { get; init; }
    public string IntegrationId { get; init; } = IntegrationIds.Local;
    public string? ProviderConnectionId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Color { get; init; } = "#808080";
    public string? Description { get; init; }
    public int SortOrder { get; init; }
    public bool IsFavorite { get; init; }
    public string? ProviderMetadataJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
