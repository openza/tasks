using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

public interface ITaskStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SpaceItem>> GetSpacesAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task UpsertSpaceAsync(SpaceItem space, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskItem>> GetTasksAsync(TaskQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GlobalSearchResult>> SearchAsync(GlobalSearchQuery query, CancellationToken cancellationToken = default);
    Task<TaskCountSummary> GetTaskCountsAsync(string? spaceId = null, CancellationToken cancellationToken = default);
    Task<TaskItem?> GetTaskAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectItem>> GetProjectsAsync(string? spaceId = null, bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LabelItem>> GetLabelsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectItem>> GetProviderProjectsAsync(string integrationId, bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LabelItem>> GetProviderLabelsAsync(string integrationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IntegrationInfo>> GetIntegrationsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderConnectionInfo>> GetProviderConnectionsAsync(CancellationToken cancellationToken = default);
    Task UpsertProviderConnectionAsync(ProviderConnectionInfo connection, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderSourceItem>> GetProviderSourceItemsAsync(string? integrationId = null, string? spaceId = null, bool includeAdopted = false, bool includeIgnored = false, CancellationToken cancellationToken = default);
    Task UpsertProviderSourceItemAsync(ProviderSourceItem item, CancellationToken cancellationToken = default);
    Task DetachProviderSourceItemAsync(string sourceItemId, CancellationToken cancellationToken = default);
    Task<TaskItem?> AdoptProviderSourceItemAsync(string sourceItemId, string spaceId = SpaceIds.Default, CancellationToken cancellationToken = default);
    Task<bool> SkipProviderSourceItemAsync(string sourceItemId, CancellationToken cancellationToken = default);
    Task<bool> UnskipProviderSourceItemAsync(string sourceItemId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncRouteInfo>> GetSyncRoutesAsync(CancellationToken cancellationToken = default);
    Task UpsertSyncRouteAsync(SyncRouteInfo route, CancellationToken cancellationToken = default);
    Task RecordSyncRouteRunAsync(SyncRouteRunInfo run, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncRouteRunInfo>> GetSyncRouteRunsAsync(string? routeId = null, CancellationToken cancellationToken = default);
    Task UpsertTaskAsync(TaskItem task, CancellationToken cancellationToken = default);
    Task UpsertProjectAsync(ProjectItem project, CancellationToken cancellationToken = default);
    Task UpsertLabelAsync(LabelItem label, CancellationToken cancellationToken = default);
    Task SetTaskLabelsAsync(string taskId, IReadOnlyList<LabelItem> labels, CancellationToken cancellationToken = default);
    Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default);
    Task DeleteProjectAsync(string projectId, bool moveTasksToInbox, CancellationToken cancellationToken = default);
    Task CompleteTaskAsync(string taskId, CancellationToken cancellationToken = default);
    Task ReopenTaskAsync(string taskId, CancellationToken cancellationToken = default);
    Task QueueCompletionAsync(PendingCompletion completion, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingCompletion>> GetPendingCompletionsAsync(string provider, CancellationToken cancellationToken = default);
    Task MarkCompletionSyncedAsync(string completionId, CancellationToken cancellationToken = default);
    Task SetIntegrationConfiguredAsync(string id, bool configured, CancellationToken cancellationToken = default);
    Task SetIntegrationActiveAsync(string id, bool active, CancellationToken cancellationToken = default);
    Task UpdateIntegrationSyncAsync(string id, DateTimeOffset lastSyncAt, string? syncToken, CancellationToken cancellationToken = default);
}
