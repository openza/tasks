using System.Text.Json;
using System.Text.Json.Nodes;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class MicrosoftCategoryAdoptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Published_category_backfill_waits_for_pull_and_uses_the_remote_version(bool disconnected)
    {
        var store = await StoreAsync();
        var task = await AdoptAsync(store, Source() with { SnapshotJson = "{}" });
        await CloudAsync(store, true);
        await store.StageUnpublishedTasksForPersonalCloudAsync(Instance, Device);
        await AcknowledgeCategoryOutboxAsync(store);
        store.PersonalCloudConnection = () => disconnected ? null : CategoryCloud();
        await store.UpsertProviderSourceItemAsync(Source("Release"));
        Assert.Empty((await store.GetTaskAsync(task.Id))!.Labels);
        Assert.DoesNotContain("msToDoCategoriesApplied", (await store.GetTaskAsync(task.Id))!.SourceMetadataJson!);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));

        store.PersonalCloudConnection = CategoryCloud;
        var remote = JsonNode.Parse(PersonalCloudK0Codec.Serialize(PersonalCloudWorkspaceTests.TaskRecord(task.Id, 2, null)))!.AsObject();
        remote["spaceId"] = SpaceIds.Default;
        remote["notes"] = "Remote notes";
        remote["labels"] = new JsonArray();
        remote["source"] = JsonNode.Parse(PersonalCloudK0Codec.Serialize(PersonalCloudK0Mapper.CreateSafeSourceDescriptor(task)));
        remote["sourceOwnerDeviceId"] = Device;
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device,
            PersonalCloudWorkspaceTests.Page(await store.GetPersonalCloudCheckpointAsync(Instance, Device) + 1, tasks: [remote]));
        Assert.Equal(1, await store.ApplyPendingMicrosoftCategoriesAfterPullAsync(CategoryCloud()));
        var updated = (await store.GetTaskAsync(task.Id))!;
        Assert.True(Applied(updated));
        Assert.Equal("Remote notes", updated.Notes);
        Assert.Equal("Release", Assert.Single(updated.Labels).Name);
        var intent = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal("task.addLabel", intent.Operation);
        Assert.Equal(2, BaseVersion(intent));
        Assert.Equal(0, await store.ApplyPendingMicrosoftCategoriesAfterPullAsync(CategoryCloud()));
    }

    [Fact]
    public async Task Published_category_backfill_at_the_label_limit_queues_no_extra_attachment()
    {
        var store = await StoreAsync();
        var task = await AdoptAsync(store, Source() with { SnapshotJson = "{}" });
        await store.SetTaskLabelsAsync(task.Id, Enumerable.Range(0, 32).Select(index => new LabelItem { Id = $"label_{index}", Name = $"Label {index}" }).ToArray());
        await CloudAsync(store, true);
        await store.StageUnpublishedTasksForPersonalCloudAsync(Instance, Device);
        await AcknowledgeCategoryOutboxAsync(store);
        await store.UpsertProviderSourceItemAsync(Source("Release"));
        await store.ApplyPendingMicrosoftCategoriesAfterPullAsync(CategoryCloud());
        Assert.Equal(32, (await store.GetTaskAsync(task.Id))!.Labels.Count);
        Assert.True(Applied((await store.GetTaskAsync(task.Id))!));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("disconnected")]
    [InlineData("missing")]
    public async Task Post_pull_category_backfill_defers_pending_edits_missing_data_and_changed_connections(string mode)
    {
        var store = await StoreAsync();
        var task = await AdoptAsync(store, Source() with { SnapshotJson = "{}" });
        await CloudAsync(store, true);
        await store.StageUnpublishedTasksForPersonalCloudAsync(Instance, Device);
        await AcknowledgeCategoryOutboxAsync(store);
        if (mode != "missing") await store.UpsertProviderSourceItemAsync(Source("Release"));
        if (mode == "disconnected") store.PersonalCloudConnection = () => null;
        if (mode == "pending")
            await new Openza.Tasks.Application.Tasks.TaskApplicationService(store, store.PersonalCloudConnection).SetCompletedAsync(task.Id, true);
        var before = (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Count;
        Assert.Equal(0, await store.ApplyPendingMicrosoftCategoriesAfterPullAsync(CategoryCloud()));
        Assert.Empty((await store.GetTaskAsync(task.Id))!.Labels);
        Assert.DoesNotContain("msToDoCategoriesApplied", (await store.GetTaskAsync(task.Id))!.SourceMetadataJson!);
        Assert.Equal(before, (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Count);
        using var replacement = PersonalCloudConnectionLease.AcquireReplacement(store.DatabasePath);
    }
}
