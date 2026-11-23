using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class MicrosoftStepSyncFailureTests
{
    [Fact]
    public async Task Failed_batch_preserves_original_sync_error_without_staging_committed_children()
    {
        var directory = Path.Combine(Path.GetTempPath(), "openza-step-failure", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "tasks.db");
        var store = new SqliteTaskStore(path);
        try
        {
            await store.InitializeAsync();
            foreach (var id in new[] { "first", "second" })
            {
                await store.UpsertProviderSourceItemAsync(new() { Id = id, ExternalId = id, IntegrationId = IntegrationIds.MicrosoftToDo,
                    ProviderConnectionId = "mstodo_default", Title = id, SnapshotJson = "{}" });
                await store.AdoptProviderSourceItemAsync(id);
            }
            await store.UpsertTaskAsync(new() { Id = "unrelated", Title = "Unreadable only during staging" });
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = "UPDATE tasks SET priority='invalid' WHERE id='unrelated'";
                await command.ExecuteNonQueryAsync();
            }
            const string instance = "11111111-1111-4111-8111-111111111111";
            store.PersonalCloudConnection = () => new() { ServiceOrigin = new("https://cloud.example.test"), InstanceId = instance,
                DeviceId = "device", ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow };
            var result = await new TaskSyncEngine(store).SyncAsync(new SnapshotProvider());
            Assert.False(result.Success);
            Assert.Contains("Object", result.Error);
            var first = (await store.GetProviderSourceItemsAsync(IntegrationIds.MicrosoftToDo, includeAdopted: true)).Single(item => item.Id == "first");
            Assert.Single(await store.GetTasksAsync(new() { ParentId = first.AdoptedTaskId, IncludeSubtasks = true }));
            Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(instance, "device"));
        }
        finally { TestDirectory.Delete(directory); }
    }

    private sealed class SnapshotProvider : ISyncProvider
    {
        public string IntegrationId => IntegrationIds.MicrosoftToDo;
        public string ProviderConnectionId => "mstodo_default";
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderSnapshot(
            [new() { Id = "first", ExternalId = "first", Title = "first", IntegrationId = IntegrationIds.MicrosoftToDo,
                ProviderMetadataJson = """{"msToDo":{"checklistItems":[{"displayName":"Imported"}]}}""" },
             new() { Id = "second", ExternalId = "second", Title = "second", IntegrationId = IntegrationIds.MicrosoftToDo,
                ProviderMetadataJson = """{"msToDo":{"checklistItems":[7]}}""" }], [], []));
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
