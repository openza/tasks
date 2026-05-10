using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

public interface ITaskStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskItem>> GetTasksAsync(TaskQuery query, CancellationToken cancellationToken = default);
    Task<TaskCountSummary> GetTaskCountsAsync(CancellationToken cancellationToken = default);
    Task<TaskItem?> GetTaskAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectItem>> GetProjectsAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LabelItem>> GetLabelsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IntegrationInfo>> GetIntegrationsAsync(CancellationToken cancellationToken = default);
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
