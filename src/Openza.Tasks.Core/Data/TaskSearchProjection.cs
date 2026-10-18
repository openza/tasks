using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

/// <summary>SQL-free global search ranking and projection over an already scoped read.</summary>
internal static class TaskSearchProjection
{
    public static IReadOnlyList<GlobalSearchResult> Build(
        GlobalSearchQuery query, IReadOnlyList<TaskItem> tasks, IReadOnlyList<ProjectItem> projects,
        IReadOnlySet<string>? activeSpaceIds, IReadOnlyList<string> tokens)
    {
        var projectNames = projects.ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);
        var limit = query.Limit <= 0 ? 30 : query.Limit;

        var taskResults = tasks
            .Where(task => activeSpaceIds is null || activeSpaceIds.Contains(task.SpaceId))
            .Where(task => MatchesSearchTokens(tokens, task.Title, task.Notes, task.SourceDescription))
            .Select(task =>
            {
                projectNames.TryGetValue(task.ProjectId ?? string.Empty, out var projectName);
                return new GlobalSearchResult
                {
                    Kind = GlobalSearchResultKind.Task,
                    Id = task.Id,
                    SpaceId = task.SpaceId,
                    Title = task.Title,
                    Subtitle = BuildTaskSearchSubtitle(task, projectName),
                    Snippet = BuildSearchSnippet(tokens, task.Notes, task.SourceDescription),
                    Score = CalculateSearchScore(query.SearchText, tokens, task.Title, task.Notes, task.SourceDescription),
                    ProjectId = task.ProjectId,
                };
            })
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Title, StringComparer.CurrentCultureIgnoreCase);

        var projectResults = projects
            .Where(project => activeSpaceIds is null || activeSpaceIds.Contains(project.SpaceId))
            .Where(project => MatchesSearchTokens(tokens, project.Name, project.Description))
            .Select(project => new GlobalSearchResult
            {
                Kind = GlobalSearchResultKind.Project,
                Id = project.Id,
                SpaceId = project.SpaceId,
                Title = project.Name,
                Subtitle = project.IsCompleted ? "Completed project" : "Project",
                Snippet = BuildSearchSnippet(tokens, project.Description),
                Score = CalculateSearchScore(query.SearchText, tokens, project.Name, project.Description),
            })
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Title, StringComparer.CurrentCultureIgnoreCase);

        return taskResults
            .Concat(projectResults)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Kind)
            .ThenBy(result => result.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;

    public static IReadOnlyList<string> SplitSearchTokens(string? searchText) =>
        string.IsNullOrWhiteSpace(searchText)
            ? []
            : searchText
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();

    private static bool MatchesSearchTokens(IReadOnlyList<string> tokens, params string?[] values) =>
        tokens.All(token => values.Any(value => Contains(value, token)));

    private static int CalculateSearchScore(string searchText, IReadOnlyList<string> tokens, string title, params string?[] bodyValues)
    {
        var normalizedSearch = searchText.Trim();
        if (title.Equals(normalizedSearch, StringComparison.CurrentCultureIgnoreCase))
        {
            return 1000;
        }

        if (title.StartsWith(normalizedSearch, StringComparison.CurrentCultureIgnoreCase))
        {
            return 900;
        }

        if (title.Contains(normalizedSearch, StringComparison.CurrentCultureIgnoreCase))
        {
            return 800;
        }

        var titleTokenScore = tokens.Any(token => title.StartsWith(token, StringComparison.CurrentCultureIgnoreCase))
            ? 700
            : tokens.Any(token => title.Contains(token, StringComparison.CurrentCultureIgnoreCase))
                ? 600
                : 0;
        if (titleTokenScore > 0)
        {
            return titleTokenScore;
        }

        return bodyValues.Any(value => tokens.Any(token => Contains(value, token))) ? 300 : 0;
    }

    private static string BuildTaskSearchSubtitle(TaskItem task, string? projectName)
    {
        var state = task.IsCompleted ? "Completed task" : "Task";
        return string.IsNullOrWhiteSpace(projectName) ? state : $"{state} · {projectName}";
    }

    private static string BuildSearchSnippet(IReadOnlyList<string> tokens, params string?[] values)
    {
        var value = values
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .FirstOrDefault(text => tokens.Any(token => Contains(text, token)));
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim().ReplaceLineEndings(" ");
        return trimmed.Length <= 140 ? trimmed : $"{trimmed[..137]}...";
    }

}
