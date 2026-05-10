namespace Openza.Tasks.Core.Models;

public enum TaskItemStatus
{
    None,
    Next,
    Waiting,
    Someday,
    Completed,
    Cancelled,
}

public static class TaskStatusExtensions
{
    public static string ToStorageValue(this TaskItemStatus status) => status switch
    {
        TaskItemStatus.Next => "next",
        TaskItemStatus.Waiting => "waiting",
        TaskItemStatus.Someday => "someday",
        TaskItemStatus.Completed => "completed",
        TaskItemStatus.Cancelled => "cancelled",
        _ => "none",
    };

    public static TaskItemStatus FromStorageValue(string? value) => value switch
    {
        "next" => TaskItemStatus.Next,
        "waiting" => TaskItemStatus.Waiting,
        "someday" => TaskItemStatus.Someday,
        "completed" => TaskItemStatus.Completed,
        "done" => TaskItemStatus.Completed,
        "cancelled" => TaskItemStatus.Cancelled,
        // Legacy Openza/Flutter rows and provider "in progress" rows are open tasks
        // without a GTD workflow list until the user clarifies them.
        "pending" or "active" or "in_progress" or "inProgress" or null or "" => TaskItemStatus.None,
        _ => TaskItemStatus.None,
    };

    public static bool IsOpen(this TaskItemStatus status) =>
        status is not TaskItemStatus.Completed and not TaskItemStatus.Cancelled;
}
