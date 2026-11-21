using System.Security.Cryptography;
using System.Text.Json;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Application.Sync;

public sealed class MicrosoftToDoImportService(SqliteTaskStore store, IConnectedSyncProviderResolver providers)
{
    public async Task<LocalImportResult> ImportAsync(string spaceId,
        Func<PersonalCloudConnectionProfile?>? cloudConnection = null, CancellationToken cancellationToken = default)
    {
        var configured = await providers.ResolveAsync(IntegrationIds.MicrosoftToDo, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Connect a Microsoft To Do account first.");
        var snapshot = await configured.Provider.FetchSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var projects = snapshot.Projects.ToDictionary(list => list.Id, list => new ProjectItem
        {
            Id = LocalId("project", list.Id),
            SpaceId = spaceId,
            Name = string.IsNullOrWhiteSpace(list.Name) ? "Untitled list" : list.Name,
            Color = list.Color,
            CreatedAt = now,
        }, StringComparer.Ordinal);
        var tasks = snapshot.Tasks.Concat(snapshot.CompletedTasks).Select(item =>
        {
            if (item.ProjectId is not { } listId || !projects.TryGetValue(listId, out var project))
                throw new InvalidDataException("A Microsoft To Do task is missing its list.");

            return new TaskItem
            {
                Id = LocalId("task", listId, item.Id),
                Title = string.IsNullOrWhiteSpace(item.Title) ? "Untitled task" : item.Title,
                Notes = item.Description,
                SpaceId = spaceId,
                ProjectId = project.Id,
                CompletionState = item.CompletionState,
                WorkflowStatus = TaskWorkflowStatus.None,
                Priority = item.Priority,
                DeadlineOn = item.DeadlineOn,
                DeadlineAt = item.DeadlineAt,
                CreatedAt = item.CreatedAt,
                CompletedAt = item.IsCompleted ? item.CompletedAt ?? now : null,
            };
        }).ToArray();

        using var lease = PersonalCloudConnectionLease.AcquireAuthoring(store.DatabasePath);
        return await store.AddLocalCopiesAsync(spaceId, projects.Values, tasks, cloudConnection?.Invoke(), cancellationToken)
            .ConfigureAwait(false);

        string LocalId(string kind, string itemId, string? childId = null) =>
            $"{kind}_msimport_{Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new[] { configured.AccountBinding, spaceId, itemId, childId })))}";
    }
}
