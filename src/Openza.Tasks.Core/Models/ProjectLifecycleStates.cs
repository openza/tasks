namespace Openza.Tasks.Core.Models;

public static class ProjectLifecycleStates
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Archived = "archived";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Completed => Completed,
        Archived => Archived,
        _ => Active,
    };
}
