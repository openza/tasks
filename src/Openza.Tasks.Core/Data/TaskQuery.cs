using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

public sealed record TaskQuery
{
    public string? SpaceId { get; init; }
    public string? ProjectId { get; init; }
    public string? ParentId { get; init; }
    public string? LabelId { get; init; }
    public string? SearchText { get; init; }
    public TaskItemStatus? Status { get; init; }
    public TaskListKind Kind { get; init; } = TaskListKind.All;
    public TaskDateScope DateScope { get; init; } = TaskDateScope.All;
    public TaskRepeatScope RepeatScope { get; init; } = TaskRepeatScope.Include;
    public int? Priority { get; init; }
    public TaskSortMode SortMode { get; init; } = TaskSortMode.PriorityThenDate;
    public bool IncludeSubtasks { get; init; }
}

public enum TaskListKind
{
    Inbox,
    NextActions,
    Waiting,
    Someday,
    Today,
    Calendar,
    Overdue,
    Open,
    All,
    Completed,
}

public enum TaskDateScope
{
    All,
}

public enum TaskRepeatScope
{
    Include,
    Exclude,
    Only,
}

public enum TaskSortMode
{
    PriorityThenDate,
    Date,
    CreatedNewest,
    Title,
    Project,
}

public enum TaskGroupMode
{
    None,
    Date,
    Project,
    Status,
    Priority,
    Label,
    Source,
    Repeating,
    CreatedDate,
    CompletedDate,
}
