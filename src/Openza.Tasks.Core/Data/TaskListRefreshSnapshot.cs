using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

public sealed record TaskListRefreshSnapshot(
    IReadOnlyList<TaskItem> VisibleTasks,
    IReadOnlyList<TaskItem> AllSpaceTasks,
    TaskCountSummary Counts);
