using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudLifecycleIntegrationTests
{
    [Fact]
    public async Task Active_category_backfill_blocks_cloud_removal_before_remote_deletion()
    {
        using var f = await Fixture.CreateAsync();
        var (parent, provider) = await PrepareCategoryRemovalBackfillAsync(f);
        var original = f.Service.CurrentConnection!;
        Task? removal = null;
        var reads = 0;
        f.Tasks.PersonalCloudConnection = () =>
        {
            var current = f.Service.CurrentConnection;
            if (++reads == 1) removal = f.Service.RemoveAsync();
            return current;
        };
        var result = await new TaskSyncEngine(f.Tasks).SyncAsync(provider);
        Assert.True(result.Success, result.Error);
        Assert.Equal(0, reads);
        Assert.Empty((await f.Tasks.GetTaskAsync(parent.Id))!.Labels);
        await f.Tasks.ApplyPendingMicrosoftCategoriesAfterPullAsync(original);
        Assert.Equal("Release", Assert.Single((await f.Tasks.GetTaskAsync(parent.Id))!.Labels).Name);
        Assert.Contains(await f.Tasks.GetPendingPersonalCloudOutboxAsync(original.InstanceId, original.DeviceId), entry => entry.EntityId == parent.Id);
        Assert.NotNull(removal);
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => removal!);
        Assert.Equal("connection_busy", error.Code);
        Assert.Equal(original, f.Service.CurrentConnection);
        Assert.DoesNotContain("worker-delete", f.Events);
        Assert.DoesNotContain("database-delete", f.Events);
    }

    [Fact]
    public async Task Cloud_removal_leaves_published_category_backfill_deferred_without_undeliverable_intents()
    {
        using var f = await Fixture.CreateAsync();
        var (parent, provider) = await PrepareCategoryRemovalBackfillAsync(f);
        var original = f.Service.CurrentConnection!;
        f.Tasks.PersonalCloudConnection = () => f.Service.CurrentConnection;
        SyncSummary? duringRemoval = null;
        f.Http.BeforeWorkerDelete = async () => duringRemoval = await new TaskSyncEngine(f.Tasks).SyncAsync(provider);
        await f.Service.RemoveAsync();
        Assert.Null(f.Service.CurrentConnection);
        Assert.NotNull(duringRemoval);
        Assert.True(duringRemoval.Success, duringRemoval.Error);
        Assert.Empty((await f.Tasks.GetTaskAsync(parent.Id))!.Labels);
        var retry = await new TaskSyncEngine(f.Tasks).SyncAsync(provider);
        Assert.True(retry.Success, retry.Error);
        Assert.Empty((await f.Tasks.GetTaskAsync(parent.Id))!.Labels);
        Assert.Empty(await f.Tasks.GetPendingPersonalCloudOutboxAsync(original.InstanceId, original.DeviceId));
        await f.Tasks.EnsureNoPendingPersonalCloudIntentsAsync();
    }

    [Fact]
    public async Task Upgraded_cloud_category_backfill_waits_for_workspace_download_before_seeding()
    {
        using var f = await Fixture.CreateAsync(23);
        var source = new ProviderSourceItem { Id = "upgrade_category", ExternalId = "upgrade_category",
            IntegrationId = IntegrationIds.MicrosoftToDo, ProviderConnectionId = "mstodo_default", Title = "Parent", SnapshotJson = "{}" };
        await f.Tasks.UpsertProviderSourceItemAsync(source);
        var parent = (await f.Tasks.AdoptProviderSourceItemAsync(source.Id))!;
        f.Http.HealthWorkspace = true;
        await f.Service.UpgradeAsync();
        var cloud = f.Service.CurrentConnection!;
        f.Tasks.PersonalCloudConnection = () => cloud;
        await f.Tasks.UpsertProviderSourceItemAsync(source with { SnapshotJson = "{\"msToDo\":{\"categories\":[{\"name\":\"Release\"}]}}" });
        Assert.Empty(await f.Tasks.GetPendingPersonalCloudOutboxAsync(cloud.InstanceId, cloud.DeviceId));
        await f.Tasks.ApplyPersonalCloudChangePageAsync(cloud.InstanceId, cloud.DeviceId,
            PersonalCloudWorkspaceTests.Page(1, spaces: [PersonalCloudWorkspaceTests.Space(SpaceIds.Default)]));
        await f.Tasks.StageUnpublishedTasksForPersonalCloudAsync(cloud.InstanceId, cloud.DeviceId);
        var outbox = await f.Tasks.GetPendingPersonalCloudOutboxAsync(cloud.InstanceId, cloud.DeviceId);
        Assert.DoesNotContain(outbox, entry => entry.Operation == "space.put" && entry.EntityId == SpaceIds.Default);
        var create = Assert.Single(outbox, entry => entry.EntityId == parent.Id && entry.Operation == "task.createRich");
        using var json = System.Text.Json.JsonDocument.Parse(create.CanonicalJson);
        Assert.Equal("Release", json.RootElement.GetProperty("payload").GetProperty("task").GetProperty("labels")[0].GetProperty("name").GetString());
    }

    private static async Task<(TaskItem Parent, ISyncProvider Provider)> PrepareCategoryRemovalBackfillAsync(Fixture f)
    {
        var source = new ProviderSourceItem { Id = "source_categories", ExternalId = "external_categories", IntegrationId = IntegrationIds.MicrosoftToDo,
            ProviderConnectionId = "mstodo_default", Title = "Synthetic parent", SnapshotJson = "{}" };
        await f.Tasks.UpsertProviderSourceItemAsync(source);
        var parent = (await f.Tasks.AdoptProviderSourceItemAsync(source.Id))!;
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.Tasks.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        var known = connection.CreateCommand();
        known.CommandText = "INSERT INTO personal_cloud_entity_versions(instance_id,product,entity_id,entity_version,updated_at) VALUES(@i,'tasks',@t,1,0)";
        known.Parameters.AddWithValue("@i", f.Service.CurrentConnection!.InstanceId);
        known.Parameters.AddWithValue("@t", parent.Id);
        await known.ExecuteNonQueryAsync();
        return (parent, new RemovalCategoryProvider(new() { Id = "remote_categories", ExternalId = source.ExternalId, IntegrationId = IntegrationIds.MicrosoftToDo,
            Title = source.Title, ProviderMetadataJson = """{"msToDo":{"categories":[{"name":"Release","color":"#32CD32"}]}}""" }));
    }

    private sealed class RemovalCategoryProvider(TaskItem task) : ISyncProvider
    {
        public string IntegrationId => IntegrationIds.MicrosoftToDo;
        public string ProviderConnectionId => "mstodo_default";
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderSnapshot([task], [], []));
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
