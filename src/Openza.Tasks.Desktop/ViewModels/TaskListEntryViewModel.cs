namespace Openza.Tasks.Desktop.ViewModels;

public sealed class TaskListEntryViewModel
{
    private TaskListEntryViewModel()
    {
    }

    public bool IsHeader { get; private init; }
    public bool IsTask => !IsHeader;
    public string GroupTitle { get; private init; } = string.Empty;
    public string GroupCount { get; private init; } = string.Empty;
    public string GroupKey { get; private init; } = string.Empty;
    public bool IsGroupExpanded { get; private init; }
    public string GroupExpandGlyph => IsGroupExpanded ? "▾" : "▸";
    public TaskListItemViewModel? Task { get; private init; }

    public static TaskListEntryViewModel Header(string key, string title, int count, bool isExpanded) => new()
    {
        IsHeader = true,
        GroupKey = key,
        GroupTitle = title,
        GroupCount = count == 1 ? "1 task" : $"{count} tasks",
        IsGroupExpanded = isExpanded,
    };

    public static TaskListEntryViewModel Item(TaskListItemViewModel task) => new() { Task = task };
}
