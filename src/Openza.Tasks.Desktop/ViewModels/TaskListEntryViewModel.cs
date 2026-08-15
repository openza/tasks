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
    public TaskListItemViewModel? Task { get; private init; }

    public static TaskListEntryViewModel Header(string title, int count) => new()
    {
        IsHeader = true,
        GroupTitle = title,
        GroupCount = count == 1 ? "1 task" : $"{count} tasks",
    };

    public static TaskListEntryViewModel Item(TaskListItemViewModel task) => new() { Task = task };
}
