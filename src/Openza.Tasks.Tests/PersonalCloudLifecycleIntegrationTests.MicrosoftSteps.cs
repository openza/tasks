using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudLifecycleIntegrationTests
{
    [Fact]
    public async Task Active_Microsoft_step_backfill_blocks_cloud_removal_before_remote_deletion()
    {
        using var f = await Fixture.CreateAsync();
        var (parent, provider) = await PrepareMicrosoftStepBackfillAsync(f);
        var original = f.Service.CurrentConnection!;
        Task? removal = null;
        var reads = 0;
        f.Tasks.PersonalCloudConnection = () =>
        {
            var current = f.Service.CurrentConnection;
            if (++reads == 2) removal = f.Service.RemoveAsync();
            return current;
        };
        var result = await new TaskSyncEngine(f.Tasks).SyncAsync(provider);
        Assert.True(result.Success, result.Error);
        Assert.Equal(2, (await f.Tasks.GetTasksAsync(new() { ParentId = parent.Id, IncludeSubtasks = true })).Count);
        Assert.NotEmpty(await f.Tasks.GetPendingPersonalCloudOutboxAsync(original.InstanceId, original.DeviceId));
        Assert.NotNull(removal);
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => removal!);
        Assert.Equal("connection_busy", error.Code);
        Assert.Equal(original, f.Service.CurrentConnection);
        Assert.DoesNotContain("worker-delete", f.Events);
        Assert.DoesNotContain("database-delete", f.Events);
    }

    [Fact]
    public async Task Cloud_removal_blocks_step_backfill_then_allows_unpaired_local_retry_without_undeliverable_intents()
    {
        using var f = await Fixture.CreateAsync();
        var (parent, provider) = await PrepareMicrosoftStepBackfillAsync(f);
        var original = f.Service.CurrentConnection!;
        f.Tasks.PersonalCloudConnection = () => f.Service.CurrentConnection;
        SyncSummary? duringRemoval = null;
        f.Http.BeforeWorkerDelete = async () => duringRemoval = await new TaskSyncEngine(f.Tasks).SyncAsync(provider);
        await f.Service.RemoveAsync();
        Assert.Null(f.Service.CurrentConnection);
        Assert.NotNull(duringRemoval);
        Assert.False(duringRemoval.Success, duringRemoval.Error);
        Assert.Contains("connection change", duringRemoval.Error);
        Assert.Empty(await f.Tasks.GetTasksAsync(new() { ParentId = parent.Id, IncludeSubtasks = true }));
        await f.Tasks.EnsureNoPendingPersonalCloudIntentsAsync();
        var retry = await new TaskSyncEngine(f.Tasks).SyncAsync(provider);
        Assert.True(retry.Success, retry.Error);
        Assert.Equal(2, (await f.Tasks.GetTasksAsync(new() { ParentId = parent.Id, IncludeSubtasks = true })).Count);
        Assert.Empty(await f.Tasks.GetPendingPersonalCloudOutboxAsync(original.InstanceId, original.DeviceId));
    }

    private static async Task<(TaskItem Parent, ISyncProvider Provider)> PrepareMicrosoftStepBackfillAsync(Fixture f)
    {
        var source = new ProviderSourceItem { Id = "source_steps", ExternalId = "external_steps", IntegrationId = IntegrationIds.MicrosoftToDo,
            ProviderConnectionId = "mstodo_default", Title = "Synthetic parent", SnapshotJson = "{}" };
        await f.Tasks.UpsertProviderSourceItemAsync(source);
        var parent = (await f.Tasks.AdoptProviderSourceItemAsync(source.Id))!;
        return (parent, new RemovalStepProvider(new() { Id = "remote_steps", ExternalId = source.ExternalId, IntegrationId = IntegrationIds.MicrosoftToDo,
            Title = source.Title, ProviderMetadataJson = """{"msToDo":{"checklistItems":[{"displayName":"First","isChecked":false},{"displayName":"Second","isChecked":true}]}}""" }));
    }

    private sealed class RemovalStepProvider(TaskItem task) : ISyncProvider
    {
        public string IntegrationId => IntegrationIds.MicrosoftToDo;
        public string ProviderConnectionId => "mstodo_default";
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderSnapshot([task], [], []));
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
