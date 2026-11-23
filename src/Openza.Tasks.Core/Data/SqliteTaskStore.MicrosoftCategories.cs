using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    public Func<PersonalCloudConnectionProfile?>? PersonalCloudConnection { get; set; }

    private async Task<TaskItem> ApplyMicrosoftCategoriesAsync(SqliteConnection connection, ProviderSourceItem source,
        TaskItem task, CancellationToken cancellationToken, bool creatingCloudTask = false, MicrosoftCategoryAuthoring? authoring = null)
    {
        if (source.IntegrationId != IntegrationIds.MicrosoftToDo) return task;
        var metadata = JsonNode.Parse(task.SourceMetadataJson ?? "{}")!.AsObject();
        if (metadata["msToDoCategoriesApplied"]?.GetValue<bool>() == true) return task;

        using var snapshot = JsonDocument.Parse(source.SnapshotJson ?? "{}");
        if (!snapshot.RootElement.TryGetProperty("msToDo", out var microsoft) ||
            !microsoft.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array)
            return task;

        var labels = task.Labels.ToList();
        PersonalCloudConnectionProfile? cloud = null;
        long? version = null;
        var cloudResolved = false;
        var localLabels = new List<LabelItem>();
        var read = connection.CreateCommand();
        read.CommandText = "SELECT id,external_id,integration_id,provider_connection_id,name,color,description,sort_order,is_favorite,provider_metadata,created_at FROM labels WHERE integration_id='openza_tasks'";
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) localLabels.Add(ReadLabel(reader));

        foreach (var category in categories.EnumerateArray())
        {
            var name = category.GetProperty("name").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var label = localLabels.FirstOrDefault(item => string.Equals(item.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
            var isNew = label is null;
            if (label is null)
            {
                label = new LabelItem { Id = $"label_{Guid.NewGuid():N}", Name = name,
                    Color = category.TryGetProperty("color", out var color) ? color.GetString() ?? "#0078D4" : "#0078D4" };
            }
            if (labels.Any(item => item.Id == label.Id)) continue;
            if (!cloudResolved)
            {
                var target = creatingCloudTask ? null : PersonalCloudConnection?.Invoke();
                if (target is not null && await ReadPersonalCloudEntityVersionAsync(connection, target.InstanceId, task.Id, cancellationToken).ConfigureAwait(false) is not null)
                    authoring?.Acquire();
                cloud = creatingCloudTask ? null : authoring?.Cloud;
                version = cloud is null ? null : await ReadPersonalCloudEntityVersionAsync(connection, cloud.InstanceId, task.Id, cancellationToken).ConfigureAwait(false);
                cloudResolved = true;
                if (cloud is not null && version is not null)
                {
                    var pending = connection.CreateCommand();
                    pending.CommandText = "SELECT COUNT(*) FROM personal_cloud_outbox WHERE instance_id=@i AND entity_id=@t AND acknowledged_at IS NULL AND operation IN('task.create','task.createRich')";
                    pending.Parameters.AddWithValue("@i", cloud.InstanceId);
                    pending.Parameters.AddWithValue("@t", task.Id);
                    if (Convert.ToInt64(await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0)
                        return task;
                }
            }
            if (isNew)
            {
                await UpsertLabelCoreAsync(connection, label, cancellationToken).ConfigureAwait(false);
                localLabels.Add(label);
            }
            labels.Add(label);
            if (cloud is not null && version is not null)
            {
                await MutateTaskRelationshipWithPersonalCloudIntentCoreAsync(connection, task.Id, "task.addLabel", label.Id,
                    label.Name, version.Value, cloud.InstanceId, cloud.DeviceId, cancellationToken).ConfigureAwait(false);
                version = await ReadPersonalCloudEntityVersionAsync(connection, cloud.InstanceId, task.Id, cancellationToken).ConfigureAwait(false);
            }
            else
                await WorkspaceExecuteAsync(connection, "INSERT OR IGNORE INTO task_labels(task_id,label_id) VALUES(@t,@l)",
                    cancellationToken, ("@t", task.Id), ("@l", label.Id)).ConfigureAwait(false);
        }

        metadata["msToDoCategoriesApplied"] = true;
        var json = metadata.ToJsonString();
        await WorkspaceExecuteAsync(connection, "UPDATE tasks SET source_metadata=@m,revision=revision+1 WHERE id=@t",
            cancellationToken, ("@m", json), ("@t", task.Id)).ConfigureAwait(false);
        return task with { Labels = labels, SourceMetadataJson = json, Revision = task.Revision + 1 };
    }

    private sealed class MicrosoftCategoryAuthoring(SqliteTaskStore store) : IDisposable
    {
        private PersonalCloudConnectionLease? lease;
        public PersonalCloudConnectionProfile? Cloud { get; private set; }

        public void Acquire()
        {
            if (lease is not null) return;
            if (store.PersonalCloudConnection?.Invoke() is null) return;
            lease = PersonalCloudConnectionLease.AcquireAuthoring(store.DatabasePath);
            Cloud = store.PersonalCloudConnection?.Invoke();
            if (Cloud is null)
            {
                lease.Dispose();
                lease = null;
            }
        }

        public void Dispose() => lease?.Dispose();
    }

}
