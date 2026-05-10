namespace Openza.Tasks.Core.Models;

public static class IntegrationIds
{
    public const string Local = "openza_tasks";
    public const string Todoist = "todoist";
    public const string MicrosoftToDo = "msToDo";
    public const string Obsidian = "obsidian";
}

public sealed record IntegrationInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Color { get; init; } = "#808080";
    public string? Icon { get; init; }
    public string? LogoPath { get; init; }
    public bool IsActive { get; init; }
    public bool IsConfigured { get; init; }
    public string? ConfigJson { get; init; }
    public DateTimeOffset? LastSyncAt { get; init; }
    public string? SyncToken { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
