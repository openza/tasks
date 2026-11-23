using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed partial class MicrosoftCategoryAdoptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Todoist_adoption_does_not_acquire_a_category_authoring_lease(bool connected)
    {
        var store = await StoreAsync();
        var source = Source("Release") with { IntegrationId = IntegrationIds.Todoist, ProviderConnectionId = "todoist_default" };
        await store.UpsertProviderSourceItemAsync(source);
        store.PersonalCloudConnection = () => connected ? CategoryCloud() : null;
        using (PersonalCloudConnectionLease.AcquireReplacement(store.DatabasePath))
            Assert.Empty((await store.AdoptProviderSourceItemAsync(source.Id))!.Labels);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public async Task Unpaired_category_backfill_does_not_block_a_cloud_connection_change()
    {
        var store = await StoreAsync();
        var source = Source("Release") with { SnapshotJson = "{}" };
        var parent = await AdoptAsync(store, source);
        store.PersonalCloudConnection = () => null;
        using (PersonalCloudConnectionLease.AcquireReplacement(store.DatabasePath))
        {
            var result = await new TaskSyncEngine(store).SyncAsync(new CategoryBatchProvider([new() { ExternalId = source.ExternalId,
                IntegrationId = IntegrationIds.MicrosoftToDo, Title = source.Title, ProviderMetadataJson = Source("Release").SnapshotJson }]));
            Assert.True(result.Success, result.Error);
            Assert.Equal("Release", Assert.Single((await store.GetTaskAsync(parent.Id))!.Labels).Name);
        }
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public async Task Marked_refresh_and_sync_do_not_resolve_cloud_or_acquire_a_category_lease()
    {
        var store = await StoreAsync();
        var source = Source("Release");
        var parent = await AdoptAsync(store, source);
        await store.SetTaskLabelsAsync(parent.Id, []);
        store.PersonalCloudConnection = () => throw new InvalidOperationException("Marked tasks must not resolve a cloud target.");
        using (PersonalCloudConnectionLease.AcquireReplacement(store.DatabasePath))
        {
            await store.UpsertProviderSourceItemAsync(source);
            var result = await new TaskSyncEngine(store).SyncAsync(new CategoryBatchProvider([new() { ExternalId = source.ExternalId,
                IntegrationId = IntegrationIds.MicrosoftToDo, Title = source.Title, ProviderMetadataJson = source.SnapshotJson }]));
            Assert.True(result.Success, result.Error);
        }
        Assert.Empty((await store.GetTaskAsync(parent.Id))!.Labels);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("attached")]
    public async Task Backfill_without_new_attachments_does_not_resolve_cloud_or_acquire_a_lease(string mode)
    {
        var store = await StoreAsync();
        var parent = await AdoptAsync(store, Source() with { SnapshotJson = "{}" });
        if (mode == "attached") await store.SetTaskLabelsAsync(parent.Id, [new() { Id = "existing", Name = "Release" }]);
        var source = mode == "attached" ? Source("Release") : mode == "empty" ? Source() : Source() with { SnapshotJson = "{}" };
        store.PersonalCloudConnection = () => throw new InvalidOperationException("No attachment work must not resolve a target.");
        using (PersonalCloudConnectionLease.AcquireReplacement(store.DatabasePath))
            await store.UpsertProviderSourceItemAsync(source);
        var updated = (await store.GetTaskAsync(parent.Id))!;
        Assert.Equal(mode == "attached" ? 1 : 0, updated.Labels.Count);
        if (mode != "missing") Assert.True(Applied(updated));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public async Task Unpublished_category_adoption_does_not_acquire_a_cloud_lease()
    {
        var store = await StoreAsync();
        var reads = 0;
        store.PersonalCloudConnection = () => ++reads == 1 ? CategoryCloud() : null;
        var parent = await AdoptAsync(store, Source("Release"));
        Assert.Equal(1, reads);
        Assert.Single(parent.Labels);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        using var replacement = PersonalCloudConnectionLease.AcquireReplacement(store.DatabasePath);
    }

    private static PersonalCloudConnectionProfile CategoryCloud() => new() { InstanceId = Instance, DeviceId = Device,
        ServiceOrigin = new("https://categories.example.test"), ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow };
}
