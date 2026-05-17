namespace Openza.Tasks.Core.Models;

public enum TaskCompletionState
{
    Open,
    Completed,
    Cancelled,
}

public enum TaskWorkflowStatus
{
    None,
    Inbox,
    Next,
    Waiting,
    Someday,
}

public enum TaskItemStatus
{
    None,
    Inbox,
    Next,
    Waiting,
    Someday,
    Completed,
    Cancelled,
}

public static class TaskStatusExtensions
{
    public static string ToStorageValue(this TaskCompletionState state) => state switch
    {
        TaskCompletionState.Completed => "completed",
        TaskCompletionState.Cancelled => "cancelled",
        _ => "open",
    };

    public static TaskCompletionState CompletionFromStorageValue(string? value) => value switch
    {
        "completed" or "done" => TaskCompletionState.Completed,
        "cancelled" or "canceled" => TaskCompletionState.Cancelled,
        _ => TaskCompletionState.Open,
    };

    public static string ToStorageValue(this TaskWorkflowStatus status) => status switch
    {
        TaskWorkflowStatus.Inbox => "inbox",
        TaskWorkflowStatus.Next => "next",
        TaskWorkflowStatus.Waiting => "waiting",
        TaskWorkflowStatus.Someday => "someday",
        _ => "none",
    };

    public static TaskWorkflowStatus WorkflowFromStorageValue(string? value) => value switch
    {
        "inbox" => TaskWorkflowStatus.Inbox,
        "next" => TaskWorkflowStatus.Next,
        "waiting" => TaskWorkflowStatus.Waiting,
        "someday" => TaskWorkflowStatus.Someday,
        "pending" or "active" or "in_progress" or "inProgress" or null or "" => TaskWorkflowStatus.Inbox,
        _ => TaskWorkflowStatus.None,
    };

    public static string ToStorageValue(this TaskItemStatus status) => status switch
    {
        TaskItemStatus.Inbox => "inbox",
        TaskItemStatus.Next => "next",
        TaskItemStatus.Waiting => "waiting",
        TaskItemStatus.Someday => "someday",
        TaskItemStatus.Completed => "completed",
        TaskItemStatus.Cancelled => "cancelled",
        _ => "none",
    };

    public static TaskItemStatus FromStorageValue(string? value) => value switch
    {
        "inbox" => TaskItemStatus.Inbox,
        "next" => TaskItemStatus.Next,
        "waiting" => TaskItemStatus.Waiting,
        "someday" => TaskItemStatus.Someday,
        "completed" => TaskItemStatus.Completed,
        "done" => TaskItemStatus.Completed,
        "cancelled" => TaskItemStatus.Cancelled,
        // Legacy Openza/Flutter rows and provider "in progress" rows are open tasks
        // without a GTD workflow list until the user clarifies them.
        "pending" or "active" or "in_progress" or "inProgress" or null or "" => TaskItemStatus.Inbox,
        _ => TaskItemStatus.None,
    };

    public static bool IsOpen(this TaskItemStatus status) =>
        status is not TaskItemStatus.Completed and not TaskItemStatus.Cancelled;

    public static TaskCompletionState ToCompletionState(this TaskItemStatus status) => status switch
    {
        TaskItemStatus.Completed => TaskCompletionState.Completed,
        TaskItemStatus.Cancelled => TaskCompletionState.Cancelled,
        _ => TaskCompletionState.Open,
    };

    public static TaskWorkflowStatus ToWorkflowStatus(this TaskItemStatus status) => status switch
    {
        TaskItemStatus.Inbox => TaskWorkflowStatus.Inbox,
        TaskItemStatus.Next => TaskWorkflowStatus.Next,
        TaskItemStatus.Waiting => TaskWorkflowStatus.Waiting,
        TaskItemStatus.Someday => TaskWorkflowStatus.Someday,
        TaskItemStatus.None or TaskItemStatus.Completed or TaskItemStatus.Cancelled => TaskWorkflowStatus.None,
        _ => TaskWorkflowStatus.None,
    };

    public static TaskItemStatus ToLegacyStatus(this TaskWorkflowStatus workflowStatus, TaskCompletionState completionState)
    {
        if (completionState == TaskCompletionState.Completed)
        {
            return TaskItemStatus.Completed;
        }

        if (completionState == TaskCompletionState.Cancelled)
        {
            return TaskItemStatus.Cancelled;
        }

        return workflowStatus switch
        {
            TaskWorkflowStatus.Inbox => TaskItemStatus.Inbox,
            TaskWorkflowStatus.Next => TaskItemStatus.Next,
            TaskWorkflowStatus.Waiting => TaskItemStatus.Waiting,
            TaskWorkflowStatus.Someday => TaskItemStatus.Someday,
            _ => TaskItemStatus.None,
        };
    }
}
