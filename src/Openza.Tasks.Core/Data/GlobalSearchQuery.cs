namespace Openza.Tasks.Core.Data;

public sealed record GlobalSearchQuery
{
    public string SearchText { get; init; } = string.Empty;
    public string? SpaceId { get; init; }
    public bool IncludeAllSpaces { get; init; }
    public bool IncludeCompletedTasks { get; init; }
    public int Limit { get; init; } = 30;
}

public enum GlobalSearchResultKind
{
    Task,
    Project,
}

public sealed record GlobalSearchResult
{
    public GlobalSearchResultKind Kind { get; init; }
    public string Id { get; init; } = string.Empty;
    public string SpaceId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Snippet { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public int Score { get; init; }
    public string? ProjectId { get; init; }
}
