using System.Text.RegularExpressions;

namespace Openza.Tasks.Core.Services;

public static partial class MarkdownTaskParser
{
    private const int MaxLineLength = 10_000;

    public static IReadOnlyList<ParsedTask> Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var tasks = new List<ParsedTask>();
        foreach (var line in markdown.Split('\n'))
        {
            var task = ParseLine(line);
            if (task is not null)
            {
                tasks.Add(task);
            }
        }

        return tasks;
    }

    public static ParsedTask? ParseLine(string line)
    {
        if (line.Length > MaxLineLength)
        {
            return null;
        }

        var match = CheckboxPattern().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var title = match.Groups["title"].Value.Trim();
        if (title.Length == 0)
        {
            return null;
        }

        return new ParsedTask(
            title.Length > 500 ? title[..500] : title,
            string.Equals(match.Groups["state"].Value, "x", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"^\s*[-*+]\s*\[(?<state>[ xX])\]\s*(?<title>.+?)\s*$", RegexOptions.Compiled)]
    private static partial Regex CheckboxPattern();
}

public sealed record ParsedTask(string Title, bool IsCompleted);
