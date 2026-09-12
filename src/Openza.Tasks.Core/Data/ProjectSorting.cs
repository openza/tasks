using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

public enum ProjectSortMode { Default, Name, Created, Updated, OpenTaskCount }

public sealed record ProjectSortSettings
{
    public ProjectSortMode Mode { get; init; }
    public bool Descending { get; init; }
    public bool FavoritesFirst { get; init; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasDirection => Mode != ProjectSortMode.Default;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => $"Sort: {Mode switch
    {
        ProjectSortMode.Name => "Name", ProjectSortMode.Created => "Created date",
        ProjectSortMode.Updated => "Updated date", ProjectSortMode.OpenTaskCount => "Open tasks",
        _ => "Default order",
    }}{(HasDirection ? Descending ? " ↓" : " ↑" : "")}";

    public ProjectSortSettings Normalize() => Enum.IsDefined(Mode) ? this : this with { Mode = ProjectSortMode.Default };
    public static string SpaceKey(string? spaceId) => spaceId is null ? "all" : $"space:{spaceId}";
}

public static class ProjectSorting
{
    public static IReadOnlyList<ProjectItem> Sort(IEnumerable<ProjectItem> projects, ProjectSortSettings settings,
        IReadOnlyDictionary<string, int> openCounts)
    {
        settings = settings.Normalize();
        return projects.OrderBy(project => project, Comparer<ProjectItem>.Create((left, right) =>
        {
            var favorite = settings.FavoritesFirst ? right.IsFavorite.CompareTo(left.IsFavorite) : 0;
            if (favorite != 0) return favorite;
            // Missing updated dates remain last in either direction.
            if (settings.Mode == ProjectSortMode.Updated)
            {
                var missing = (left.UpdatedAt is null).CompareTo(right.UpdatedAt is null);
                if (missing != 0) return missing;
            }
            var comparison = settings.Mode switch
            {
                ProjectSortMode.Name => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name),
                ProjectSortMode.Created => left.CreatedAt.CompareTo(right.CreatedAt),
                ProjectSortMode.Updated => Nullable.Compare(left.UpdatedAt, right.UpdatedAt),
                ProjectSortMode.OpenTaskCount => openCounts.GetValueOrDefault(left.Id).CompareTo(openCounts.GetValueOrDefault(right.Id)),
                _ => left.SortOrder.CompareTo(right.SortOrder),
            };
            if (comparison != 0) return settings.HasDirection && settings.Descending ? -Math.Sign(comparison) : comparison;
            comparison = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Id, right.Id);
        })).ToArray();
    }
}
