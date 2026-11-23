using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    public Func<PersonalCloudConnectionProfile?>? PersonalCloudConnection { get; set; }

    private async Task<(string? Metadata, IReadOnlyList<TaskItem> Children)> ApplyMicrosoftStepsAsync(
        SqliteConnection connection, ProviderSourceItem source, string taskId, CancellationToken cancellationToken)
    {
        if (source.IntegrationId != IntegrationIds.MicrosoftToDo) return (null, []);
        var read = connection.CreateCommand();
        read.CommandText = """
            SELECT source_metadata,space_id,project_id FROM tasks WHERE id=@id
              AND COALESCE(json_extract(source_metadata,'$.msToDoStepsApplied'),0)=0
            """;
        read.Parameters.AddWithValue("@id", taskId);
        string? metadataJson; string spaceId; string? projectId;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return (null, []);
            metadataJson = reader.IsDBNull(0) ? null : reader.GetString(0);
            spaceId = reader.GetString(1);
            projectId = reader.IsDBNull(2) ? null : reader.GetString(2);
        }
        using var snapshot = JsonDocument.Parse(source.SnapshotJson ?? "{}");
        if (!snapshot.RootElement.TryGetProperty("msToDo", out var microsoft) ||
            !microsoft.TryGetProperty("checklistItems", out var checklist) || checklist.ValueKind != JsonValueKind.Array)
            return (null, []);

        var children = new List<TaskItem>();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        foreach (var item in checklist.EnumerateArray())
        {
            var title = item.GetProperty("displayName").GetString();
            if (string.IsNullOrWhiteSpace(title)) continue;
            var completed = item.TryGetProperty("isChecked", out var check) && check.GetBoolean();
            var completedAt = item.TryGetProperty("checkedDateTime", out var stamp) && stamp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed : now;
            var child = new TaskItem
            {
                Id = $"local_{Guid.NewGuid():N}", ParentId = taskId, SpaceId = spaceId, ProjectId = projectId, Title = title,
                WorkflowStatus = TaskWorkflowStatus.None,
                CompletionState = completed ? TaskCompletionState.Completed : TaskCompletionState.Open,
                CompletedAt = completed ? completedAt : null, CreatedAt = now.AddSeconds(children.Count - checklist.GetArrayLength()),
            };
            await UpsertTaskCoreAsync(connection, child, cancellationToken).ConfigureAwait(false);
            children.Add(child);
        }
        var metadata = JsonNode.Parse(metadataJson ?? "{}")!.AsObject();
        metadata["msToDoStepsApplied"] = true;
        var json = metadata.ToJsonString();
        await WorkspaceExecuteAsync(connection, "UPDATE tasks SET source_metadata=@m,revision=revision+1 WHERE id=@t",
            cancellationToken, ("@m", json), ("@t", taskId)).ConfigureAwait(false);
        return (json, children);
    }

    private async Task StageMicrosoftStepTasksAsync(CancellationToken cancellationToken)
    {
        if (PersonalCloudConnection?.Invoke() is { } cloud)
            await StageUnpublishedTasksForPersonalCloudAsync(cloud.InstanceId, cloud.DeviceId, cancellationToken).ConfigureAwait(false);
    }

    internal MicrosoftStepSyncBatch BeginMicrosoftStepSyncBatch(CancellationToken cancellationToken) => new(this, cancellationToken);

    internal sealed class MicrosoftStepSyncBatch : IAsyncDisposable
    {
        private readonly SqliteTaskStore store;
        private readonly CancellationToken cancellationToken;
        private readonly IDisposable? lease;
        private bool changed;

        internal MicrosoftStepSyncBatch(SqliteTaskStore store, CancellationToken cancellationToken)
        {
            this.store = store;
            this.cancellationToken = cancellationToken;
            lease = store.PersonalCloudConnection is null ? null : PersonalCloudConnectionLease.AcquireAuthoring(store.DatabasePath);
        }

        internal async Task UpsertAsync(ProviderSourceItem source) =>
            changed |= await store.UpsertProviderSourceWithStepsAsync(source, false, cancellationToken).ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (changed) await store.StageMicrosoftStepTasksAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { lease?.Dispose(); }
        }
    }
}
