using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Controls;

public sealed class TaskRowActionRequestedEventArgs(string taskId, TaskRowActionKind action)
{
    public string TaskId { get; } = taskId;
    public TaskRowActionKind Action { get; } = action;
    public DateOnly? Date { get; init; }
    public TaskItemStatus? Status { get; init; }
    public int? Priority { get; init; }
}

public enum TaskRowActionKind
{
    SetDate,
    ClearDate,
    ChangeProject,
    ChangeLabels,
    SetStatus,
    SetPriority,
    MoveToSpace,
    Delete,
}
