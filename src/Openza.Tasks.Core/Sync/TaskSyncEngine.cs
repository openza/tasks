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
            var providerConnectionId = provider.ProviderConnectionId;
            var existing = await store.GetProviderSourceItemsAsync(provider.IntegrationId, includeAdopted: true, includeIgnored: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            var existingByExternalId = existing
                .Where(t => ProviderConnectionMatches(t.ProviderConnectionId, providerConnectionId, provider.IntegrationId))
                .ToDictionary(t => t.ExternalId, StringComparer.Ordinal);
            var projectNames = snapshot.Projects.ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);
            var remoteExternalIds = new HashSet<string>(StringComparer.Ordinal);

            var sourceItemsAdded = 0;
            var sourceItemsUpdated = 0;

            foreach (var incomingTask in SortParentsFirst(snapshot.Tasks))
            {
                var task = string.IsNullOrWhiteSpace(incomingTask.ProviderConnectionId)
                    ? incomingTask with { ProviderConnectionId = providerConnectionId }
                    : incomingTask;
                if (string.IsNullOrWhiteSpace(task.ExternalId))
                {
                    continue;
                }

                remoteExternalIds.Add(task.ExternalId);
                await store.UpsertProviderSourceItemAsync(ToProviderSourceItem(task, providerConnectionId, projectNames), cancellationToken).ConfigureAwait(false);
                if (existingByExternalId.ContainsKey(task.ExternalId))
                {
                    sourceItemsUpdated++;
                    continue;
                }

                sourceItemsAdded++;
            }

            foreach (var incomingCompletedTask in snapshot.CompletedTasks)
            {
                var completedTask = string.IsNullOrWhiteSpace(incomingCompletedTask.ProviderConnectionId)
                    ? incomingCompletedTask with { ProviderConnectionId = providerConnectionId }
                    : incomingCompletedTask;
                if (string.IsNullOrWhiteSpace(completedTask.ExternalId) ||
                    remoteExternalIds.Contains(completedTask.ExternalId) ||
                    !existingByExternalId.TryGetValue(completedTask.ExternalId, out var existingSource) ||
                    existingSource.AdoptionState != ProviderSourceAdoptionStates.Adopted)
                {
                    continue;
                }

                var mergedTask = MergeCompletedTask(completedTask, existingSource, providerConnectionId);
                await store.UpsertProviderSourceItemAsync(ToProviderSourceItem(mergedTask, providerConnectionId, projectNames), cancellationToken).ConfigureAwait(false);
                remoteExternalIds.Add(completedTask.ExternalId);
                sourceItemsUpdated++;
            }

            var tasksDeleted = 0;
            if (_conflictPolicy.DeleteOrphans)
            {
                foreach (var orphan in existingByExternalId.Values.Where(t =>
                    t.AdoptionState != ProviderSourceAdoptionStates.Ignored &&
                    !remoteExternalIds.Contains(t.ExternalId)))
                {
                    // Direction 2 keeps provider removals non-destructive: the Openza task stays,
                    // but the stale source link is detached so local cleanup can proceed.
                    await store.DetachProviderSourceItemAsync(orphan.Id, cancellationToken).ConfigureAwait(false);
                }
            }

            await store.UpdateIntegrationSyncAsync(provider.IntegrationId, DateTimeOffset.UtcNow, snapshot.SyncToken, cancellationToken).ConfigureAwait(false);
            return new SyncSummary(
                provider.IntegrationId,
                true,
                sourceItemsAdded,
                sourceItemsUpdated,
                tasksDeleted,
                0,
                0,
                completionsSynced,
                NewSyncToken: snapshot.SyncToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SyncSummary.Failed(provider.IntegrationId, exception);
        }
    }

    private static TaskItem MergeCompletedTask(TaskItem completedTask, ProviderSourceItem existingSource, string providerConnectionId)
    {
        var title = string.IsNullOrWhiteSpace(completedTask.Title) ? existingSource.Title : completedTask.Title;
        return completedTask with
        {
            IntegrationId = string.IsNullOrWhiteSpace(completedTask.IntegrationId) ? existingSource.IntegrationId : completedTask.IntegrationId,
            ProviderConnectionId = providerConnectionId,
            Title = title,
            Description = completedTask.Description ?? existingSource.Description,
            ProjectId = completedTask.ProjectId ?? existingSource.SourceProjectId,
            ParentId = completedTask.ParentId,
            Priority = completedTask.Priority,
            CompletionState = TaskCompletionState.Completed,
            PlannedOn = completedTask.PlannedOn ?? existingSource.PlannedOn,
            PlannedAt = completedTask.PlannedAt ?? existingSource.PlannedAt,
            DeadlineOn = completedTask.DeadlineOn ?? existingSource.DeadlineOn,
            DeadlineAt = completedTask.DeadlineAt ?? existingSource.DeadlineAt,
            RecurrenceRule = completedTask.RecurrenceRule ?? existingSource.RecurrenceRule,
            ProviderMetadataJson = completedTask.ProviderMetadataJson ?? existingSource.SnapshotJson,
        };
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

    private static ProviderSourceItem ToProviderSourceItem(
        TaskItem task,
        string providerConnectionId,
        IReadOnlyDictionary<string, string> projectNames)
    {
        var sourceProjectName = task.ProjectId is not null && projectNames.TryGetValue(task.ProjectId, out var projectName)
            ? projectName
            : null;

        return new ProviderSourceItem
        {
            IntegrationId = task.IntegrationId,
            ProviderConnectionId = providerConnectionId,
            ExternalId = task.ExternalId!,
            ProviderTaskId = BuildProviderTaskId(task),
            Title = task.Title,
            Description = task.Description,
            SourceProjectId = task.ProjectId,
            SourceProjectName = sourceProjectName,
            ParentExternalId = BuildParentExternalId(task),
            SuggestedSpaceId = task.SpaceId,
            Priority = task.Priority,
            CompletionState = task.CompletionState,
            PlannedOn = task.PlannedOn,
            PlannedAt = task.PlannedAt,
            DeadlineOn = task.DeadlineOn,
            DeadlineAt = task.DeadlineAt,
            RecurrenceRule = task.RecurrenceRule,
            SourceUrl = BuildSourceUrl(task),
            SnapshotJson = task.ProviderMetadataJson,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
    }

    private static bool ProviderConnectionMatches(string? taskConnectionId, string providerConnectionId, string integrationId)
    {
        if (string.IsNullOrWhiteSpace(taskConnectionId))
        {
            return string.Equals(providerConnectionId, integrationId, StringComparison.Ordinal) ||
                string.Equals(providerConnectionId, DefaultProviderConnectionId(integrationId), StringComparison.Ordinal);
        }

        return string.Equals(taskConnectionId, providerConnectionId, StringComparison.Ordinal);
    }

    private static string DefaultProviderConnectionId(string integrationId) => integrationId switch
    {
        IntegrationIds.Local => "local_default",
        IntegrationIds.Todoist => "todoist_default",
        IntegrationIds.MicrosoftToDo => "mstodo_default",
        _ => integrationId,
    };

    private static string BuildProviderTaskId(TaskItem task)
    {
        if (task.IntegrationId == IntegrationIds.MicrosoftToDo &&
            task.ProjectId?.StartsWith("mstodo_", StringComparison.Ordinal) == true &&
            !string.IsNullOrWhiteSpace(task.ExternalId))
        {
            return $"{task.ProjectId["mstodo_".Length..]}|{task.ExternalId}";
        }

        return task.ExternalId ?? task.Id;
    }

    private static string? BuildSourceUrl(TaskItem task)
    {
        return task.IntegrationId == IntegrationIds.Todoist && !string.IsNullOrWhiteSpace(task.ExternalId)
            ? $"https://todoist.com/showTask?id={Uri.EscapeDataString(task.ExternalId)}"
            : null;
    }

    private static string? BuildParentExternalId(TaskItem task)
    {
        if (string.IsNullOrWhiteSpace(task.ParentId))
        {
            return null;
        }

        var prefix = task.IntegrationId switch
        {
            IntegrationIds.Todoist => "todoist_",
            IntegrationIds.MicrosoftToDo => "mstodo_",
            _ => string.Empty,
        };

        return !string.IsNullOrWhiteSpace(prefix) && task.ParentId.StartsWith(prefix, StringComparison.Ordinal)
            ? task.ParentId[prefix.Length..]
            : task.ParentId;
    }

    private static IEnumerable<TaskItem> SortParentsFirst(IEnumerable<TaskItem> tasks)
    {
        return tasks.OrderBy(t => t.ParentId is not null).ThenBy(t => t.CreatedAt);
    }
}
