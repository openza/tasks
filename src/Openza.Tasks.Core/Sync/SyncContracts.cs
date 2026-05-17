using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Sync;

public interface ISyncProvider
{
    string IntegrationId { get; }
    string ProviderConnectionId => IntegrationId switch
    {
        IntegrationIds.Local => "local_default",
        IntegrationIds.Todoist => "todoist_default",
        IntegrationIds.MicrosoftToDo => "mstodo_default",
        _ => IntegrationId,
    };
    Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default);
    Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default);
}

public sealed record ProviderSnapshot(
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<ProjectItem> Projects,
    IReadOnlyList<LabelItem> Labels,
    string? SyncToken = null)
{
    public IReadOnlyList<TaskItem> CompletedTasks { get; init; } = [];
}

public sealed record SyncSummary(
    string Provider,
    bool Success,
    int TasksAdded,
    int TasksUpdated,
    int TasksDeleted,
    int ProjectsSynced,
    int LabelsSynced,
    int CompletionsSynced,
    string? Error = null,
    string? NewSyncToken = null)
{
    public static SyncSummary Failed(string provider, Exception exception) =>
        new(provider, false, 0, 0, 0, 0, 0, 0, exception.Message);
}

public sealed record ConflictPolicy(bool DeleteOrphans)
{
    public static ConflictPolicy Default { get; } = new(DeleteOrphans: false);
}
