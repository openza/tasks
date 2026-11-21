using System.Text.RegularExpressions;
using System.Text.Json;
using System.Globalization;
using Openza.Tasks.Core.Models;

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
        string? project = null;
        var exported = markdown.StartsWith("# Openza Tasks Export", StringComparison.Ordinal);
        var exportedLabels = exported
            ? markdown.Split('\n').Select(line => Regex.Match(line, @"^\| (.+) \| (#[0-9a-fA-F]{6}) \| .+ \|\s*$"))
                .Where(match => match.Success).Select(match => new LabelItem { Name = Unescape(match.Groups[1].Value), Color = match.Groups[2].Value }).ToArray()
            : [];
        foreach (var line in markdown.Split('\n'))
        {
            if (exported && line.StartsWith("### ", StringComparison.Ordinal))
                project = Unescape(line[4..].Trim()) is var name && name != "No Project" ? name : null;
            if (tasks.Count > 0 && line.StartsWith("  <!-- openza-task:", StringComparison.Ordinal) && line.TrimEnd().EndsWith(" -->", StringComparison.Ordinal))
            {
                try
                {
                    var data = JsonSerializer.Deserialize<ParsedTask>(line.Trim()[17..^4]);
                    if (data is not null && !string.IsNullOrWhiteSpace(data.Title) && data.Priority is >= 1 and <= 4
                        && data.Labels is not null && data.Labels.All(label => label is not null && !string.IsNullOrWhiteSpace(label.Name))
                        && Enum.IsDefined(data.WorkflowStatus) && Enum.IsDefined(data.CompletionState))
                        tasks[^1] = data;
                }
                catch (JsonException) { }
                continue;
            }
            if (tasks.Count > 0 && line.StartsWith("  - _", StringComparison.Ordinal) && line.TrimEnd().EndsWith('_'))
            {
                tasks[^1] = tasks[^1] with { Notes = Unescape(line.TrimEnd()[5..^1]) };
                continue;
            }
            var task = ParseLine(line, exported);
            if (task is not null)
            {
                tasks.Add(task with
                {
                    ProjectName = project,
                    Priority = exported && task.Priority == 2 && !line.Contains("Priority:", StringComparison.Ordinal) ? 4 : task.Priority,
                    Labels = task.Labels.Select(label => exportedLabels.FirstOrDefault(item => item.Name.Replace(' ', '_') == label.Name.Replace(' ', '_')) ?? label).ToArray(),
                });
            }
        }

        return tasks;
    }

    public static ParsedTask? ParseLine(string line) => ParseLine(line, exported: false);

    private static ParsedTask? ParseLine(string line, bool exported)
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

        if (!exported)
        {
            return new ParsedTask(
                title.Length > 500 ? title[..500] : title,
                string.Equals(match.Groups["state"].Value, "x", StringComparison.OrdinalIgnoreCase));
        }

        var priority = 2;
        DateOnly? date = null, deadline = null;
        var labels = new List<LabelItem>();
        var tags = Regex.Match(title, @"(?: @[^\s]+)+$");
        if (tags.Success)
        {
            labels.AddRange(tags.Value.Trim().Split(' ').Select(tag => new LabelItem { Name = tag[1..].Replace('_', ' ') }));
            title = title[..tags.Index];
        }
        var metadata = Regex.Match(title, @" \((?<values>(?:(?:Priority: (?:High|Medium|Normal|Low)|(?:Date|Deadline): \d{4}-\d{2}-\d{2})(?:, )?)+)\)$");
        if (metadata.Success)
        {
            foreach (var value in metadata.Groups["values"].Value.Split(", "))
            {
                if (value.StartsWith("Priority: ", StringComparison.Ordinal))
                    priority = value[10..] switch { "High" => 1, "Medium" => 2, "Normal" => 3, _ => 4 };
                else if (DateOnly.TryParseExact(value[(value.IndexOf(':') + 2)..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    if (value.StartsWith("Date:", StringComparison.Ordinal)) date = parsed;
                    else deadline = parsed;
                }
            }
            title = title[..metadata.Index];
        }
        title = Unescape(title);
        return new ParsedTask(
            title.Length > 500 ? title[..500] : title,
            string.Equals(match.Groups["state"].Value, "x", StringComparison.OrdinalIgnoreCase))
        { Priority = priority, PlannedOn = date, DeadlineOn = deadline, Labels = labels };
    }

    private static string Unescape(string text) => Regex.Replace(text, @"\\([\\*_`\[\]|])", "$1");

    [GeneratedRegex(@"^\s*[-*+]\s*\[(?<state>[ xX])\]\s*(?<title>.+?)\s*$", RegexOptions.Compiled)]
    private static partial Regex CheckboxPattern();
}

public sealed record ParsedTask(string Title, bool IsCompleted)
{
    public int Priority { get; init; } = 2;
    public DateOnly? PlannedOn { get; init; }
    public DateOnly? DeadlineOn { get; init; }
    public string? Notes { get; init; }
    public string? ProjectName { get; init; }
    public TaskWorkflowStatus WorkflowStatus { get; init; } = TaskWorkflowStatus.Inbox;
    public TaskCompletionState CompletionState { get; init; }
    public IReadOnlyList<LabelItem> Labels { get; init; } = [];
}
