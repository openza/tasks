using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed partial class MicrosoftCategoryAdoptionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sync_batch_stages_changed_Microsoft_categories_once_and_skips_unchanged_tasks(bool workspace)
    {
        var store = await StoreAsync();
        var sources = Enumerable.Range(0, 4).Select(index => Source("Release") with
        {
            Id = $"source_{index}", ExternalId = $"task_{index}", ProviderTaskId = $"list|task_{index}",
            SnapshotJson = """{"msToDo":{"id":"cached-task"}}""",
        }).ToArray();
        foreach (var source in sources)
        {
            await store.UpsertProviderSourceItemAsync(source);
            await store.AdoptProviderSourceItemAsync(source.Id);
        }
        await CloudAsync(store, workspace);
        var target = store.PersonalCloudConnection!();
        var targetReads = 0;
        store.PersonalCloudConnection = () => { targetReads++; return target; };
        var tasks = sources.Select(source => new TaskItem
        {
            Id = $"mstodo_{source.ExternalId}", IntegrationId = IntegrationIds.MicrosoftToDo, ExternalId = source.ExternalId,
            Title = source.Title, ProviderMetadataJson = Source("Release").SnapshotJson,
        }).ToArray();
        var engine = new TaskSyncEngine(store);
        var provider = new CategoryBatchProvider(tasks);

        var result = await engine.SyncAsync(provider);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, targetReads);
        Assert.Equal(tasks.Length, (await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All })).Count(task => task.Labels.Count == 1));
        var before = (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Count;
        targetReads = 0;
        result = await engine.SyncAsync(provider);
        Assert.True(result.Success, result.Error);
        Assert.Equal(0, targetReads);
        Assert.Equal(before, (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Count);
    }

    [Fact]
    public async Task Sync_batch_stages_committed_category_changes_when_a_later_source_fails()
    {
        var store = await StoreAsync();
        var sources = Enumerable.Range(0, 2).Select(index => Source("Release") with
        {
            Id = $"source_{index}", ExternalId = $"task_{index}", ProviderTaskId = $"list|task_{index}", SnapshotJson = "{}",
        }).ToArray();
        foreach (var source in sources)
        {
            await store.UpsertProviderSourceItemAsync(source);
            await store.AdoptProviderSourceItemAsync(source.Id);
        }
        await CloudAsync(store, true);
        var target = store.PersonalCloudConnection!();
        var targetReads = 0;
        store.PersonalCloudConnection = () => { targetReads++; return target; };
        var tasks = sources.Select((source, index) => new TaskItem
        {
            IntegrationId = IntegrationIds.MicrosoftToDo, ExternalId = source.ExternalId, Title = source.Title,
            ProviderMetadataJson = index == 0 ? Source("Release").SnapshotJson : "{",
        }).ToArray();

        var result = await new TaskSyncEngine(store).SyncAsync(new CategoryBatchProvider(tasks));

        Assert.False(result.Success);
        Assert.Equal(2, targetReads);
        var first = (await store.GetProviderSourceItemsAsync(IntegrationIds.MicrosoftToDo, includeAdopted: true)).Single(source => source.ExternalId == sources[0].ExternalId);
        var create = (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Single(entry => entry.EntityId == first.AdoptedTaskId);
        Assert.Equal("task.createRich", create.Operation);
        Assert.Contains("Release", create.CanonicalJson);
    }

    [Fact]
    public async Task Sync_batch_with_no_categories_does_not_stage_unpublished_tasks()
    {
        var store = await StoreAsync();
        var source = Source() with { SnapshotJson = "{}" };
        var task = await AdoptAsync(store, source);
        await CloudAsync(store, true);
        var target = store.PersonalCloudConnection!();
        var targetReads = 0;
        store.PersonalCloudConnection = () => { targetReads++; return target; };
        var remote = new TaskItem
        {
            IntegrationId = IntegrationIds.MicrosoftToDo, ExternalId = source.ExternalId, Title = source.Title,
            ProviderMetadataJson = Source().SnapshotJson,
        };

        var result = await new TaskSyncEngine(store).SyncAsync(new CategoryBatchProvider([remote]));

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, targetReads);
        Assert.True(Applied((await store.GetTaskAsync(task.Id))!));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    private sealed class CategoryBatchProvider(IReadOnlyList<TaskItem> tasks) : ISyncProvider
    {
        public string IntegrationId => IntegrationIds.MicrosoftToDo;
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderSnapshot(tasks, [], []));
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
