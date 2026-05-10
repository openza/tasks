using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

public sealed record TaskQuery
{
    public string? ProjectId { get; init; }
    public string? LabelId { get; init; }
    public string? SearchText { get; init; }
    public TaskItemStatus? Status { get; init; }
    public TaskListKind Kind { get; init; } = TaskListKind.All;
    public int? Priority { get; init; }
    public TaskSortMode SortMode { get; init; } = TaskSortMode.PriorityThenDueDate;
}

public enum TaskListKind
{
    Inbox,
    NextActions,
    Waiting,
    Someday,
    Today,
    Overdue,
    Open,
    All,
    Completed,
}

public enum TaskSortMode
{
    PriorityThenDueDate,
    DueDate,
    CreatedNewest,
    Title,
    Project,
}
