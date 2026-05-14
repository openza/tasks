namespace Openza.Tasks.Core.Models;

public sealed record SpaceItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Color { get; init; } = "#808080";
    public string? Icon { get; init; }
    public int SortOrder { get; init; }
    public bool IsArchived { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
}
