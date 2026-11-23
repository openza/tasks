using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudLifecycleIntegrationTests
{
    [Fact]
    public async Task Step_backfill_is_local_during_cloud_removal_without_undeliverable_intents()
    {
        using var f = await Fixture.CreateAsync();
        var (parent, provider) = await PrepareMicrosoftStepBackfillAsync(f);
        var original = f.Service.CurrentConnection!;
        f.Tasks.PersonalCloudConnection = () => throw new InvalidOperationException("Local import must not resolve the cloud.");
        SyncSummary? duringRemoval = null;
        f.Http.BeforeWorkerDelete = async () => duringRemoval = await new TaskSyncEngine(f.Tasks).SyncAsync(provider);
        await f.Service.RemoveAsync();
        Assert.Null(f.Service.CurrentConnection);
        Assert.NotNull(duringRemoval);
        Assert.True(duringRemoval.Success, duringRemoval.Error);
        Assert.Equal(2, (await f.Tasks.GetTasksAsync(new() { ParentId = parent.Id, IncludeSubtasks = true })).Count);
        await f.Tasks.EnsureNoPendingPersonalCloudIntentsAsync();
        var retry = await new TaskSyncEngine(f.Tasks).SyncAsync(provider);
        Assert.True(retry.Success, retry.Error);
        Assert.Equal(2, (await f.Tasks.GetTasksAsync(new() { ParentId = parent.Id, IncludeSubtasks = true })).Count);
        Assert.Empty(await f.Tasks.GetPendingPersonalCloudOutboxAsync(original.InstanceId, original.DeviceId));
    }

    [Fact]
    public async Task Upgraded_cloud_step_backfill_waits_for_workspace_download_before_seeding()
    {
        using var f = await Fixture.CreateAsync(23);
        var (parent, provider) = await PrepareMicrosoftStepBackfillAsync(f);
        f.Http.HealthWorkspace = true;
        await f.Service.UpgradeAsync();
        var cloud = f.Service.CurrentConnection!;
        f.Tasks.PersonalCloudConnection = () => cloud;
        Assert.True((await new TaskSyncEngine(f.Tasks).SyncAsync(provider)).Success);
        Assert.Empty(await f.Tasks.GetPendingPersonalCloudOutboxAsync(cloud.InstanceId, cloud.DeviceId));
        await f.Tasks.ApplyPersonalCloudChangePageAsync(cloud.InstanceId, cloud.DeviceId,
            PersonalCloudWorkspaceTests.Page(1, spaces: [PersonalCloudWorkspaceTests.Space(SpaceIds.Default)]));
        await f.Tasks.StageUnpublishedTasksForPersonalCloudAsync(cloud.InstanceId, cloud.DeviceId);
        var outbox = await f.Tasks.GetPendingPersonalCloudOutboxAsync(cloud.InstanceId, cloud.DeviceId);
        Assert.DoesNotContain(outbox, entry => entry.Operation == "space.put" && entry.EntityId == SpaceIds.Default);
        foreach (var child in await f.Tasks.GetTasksAsync(new() { ParentId = parent.Id, IncludeSubtasks = true }))
        {
            Assert.Contains(outbox, entry => entry.EntityId == child.Id && entry.Operation == "task.createRich");
            Assert.Contains(outbox, entry => entry.EntityId == child.Id && entry.Operation == "task.setParent");
        }
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
