using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Sync;

public sealed class TaskSyncEngine(ITaskStore store, ConflictPolicy? conflictPolicy = null)
{
    private readonly ConflictPolicy _conflictPolicy = conflictPolicy ?? ConflictPolicy.Default;

    public async Task<SyncSummary> SyncAsync(ISyncProvider provider, CancellationToken cancellationToken = default)
    {
        try
        {
            var completionsSynced = await SyncPendingCompletionsAsync(provider, cancellationToken).ConfigureAwait(false);
            var snapshot = await provider.FetchSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var existing = await store.GetTasksAsync(new TaskQuery(), cancellationToken).ConfigureAwait(false);
            var existingByExternalId = existing
                .Where(t => t.IntegrationId == provider.IntegrationId && !string.IsNullOrWhiteSpace(t.ExternalId))
                .ToDictionary(t => t.ExternalId!, StringComparer.Ordinal);
            var remoteExternalIds = new HashSet<string>(StringComparer.Ordinal);

            var tasksAdded = 0;
            var tasksUpdated = 0;

            foreach (var project in snapshot.Projects)
            {
                await store.UpsertProjectAsync(project, cancellationToken).ConfigureAwait(false);
            }

            foreach (var label in snapshot.Labels)
            {
                await store.UpsertLabelAsync(label, cancellationToken).ConfigureAwait(false);
            }

            foreach (var task in SortParentsFirst(snapshot.Tasks))
            {
                if (!string.IsNullOrWhiteSpace(task.ExternalId))
                {
                    remoteExternalIds.Add(task.ExternalId);
                }

                if (task.ExternalId is not null && existingByExternalId.TryGetValue(task.ExternalId, out var local))
                {
                    await store.UpsertTaskAsync(MergeProviderTask(local, task), cancellationToken).ConfigureAwait(false);
                    tasksUpdated++;
                }
                else
                {
                    await store.UpsertTaskAsync(task, cancellationToken).ConfigureAwait(false);
                    tasksAdded++;
                }
            }

            var tasksDeleted = 0;
            if (_conflictPolicy.DeleteOrphans)
            {
                foreach (var orphan in existingByExternalId.Values.Where(t => t.ExternalId is not null && !remoteExternalIds.Contains(t.ExternalId)))
                {
                    await store.DeleteTaskAsync(orphan.Id, cancellationToken).ConfigureAwait(false);
                    tasksDeleted++;
                }
            }

            await store.UpdateIntegrationSyncAsync(provider.IntegrationId, DateTimeOffset.UtcNow, snapshot.SyncToken, cancellationToken).ConfigureAwait(false);
            return new SyncSummary(
                provider.IntegrationId,
                true,
                tasksAdded,
                tasksUpdated,
                tasksDeleted,
                snapshot.Projects.Count,
                snapshot.Labels.Count,
                completionsSynced,
                NewSyncToken: snapshot.SyncToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SyncSummary.Failed(provider.IntegrationId, exception);
        }
    }

    public async Task<int> SyncPendingCompletionsAsync(ISyncProvider provider, CancellationToken cancellationToken = default)
    {
        var completions = await store.GetPendingCompletionsAsync(provider.IntegrationId, cancellationToken).ConfigureAwait(false);
        var synced = 0;
        foreach (var completion in completions)
        {
            await provider.CompleteTaskAsync(completion, cancellationToken).ConfigureAwait(false);
            await store.MarkCompletionSyncedAsync(completion.Id, cancellationToken).ConfigureAwait(false);
            synced++;
        }

        return synced;
    }

    private static TaskItem MergeProviderTask(TaskItem local, TaskItem remote)
    {
        // The app owns local enhancement fields after sync. Providers own completion,
        // while Openza owns local GTD workflow lists and notes.
        return remote with
        {
            Id = local.Id,
            ProjectId = local.ProjectId ?? remote.ProjectId,
            Status = ResolveMergedStatus(local.Status, remote.Status),
            Notes = local.Notes,
            CreatedAt = local.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static TaskItemStatus ResolveMergedStatus(TaskItemStatus local, TaskItemStatus remote)
    {
        if (!remote.IsOpen())
        {
            return remote;
        }

        return local.IsOpen() ? local : TaskItemStatus.None;
    }

    private static IEnumerable<TaskItem> SortParentsFirst(IEnumerable<TaskItem> tasks)
    {
        return tasks.OrderBy(t => t.ParentId is not null).ThenBy(t => t.CreatedAt);
    }
}
