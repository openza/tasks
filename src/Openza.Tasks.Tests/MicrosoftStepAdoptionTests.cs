using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class MicrosoftStepAdoptionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "openza-microsoft-steps", Guid.NewGuid().ToString("N"));
    private const string Instance = "instance_steps_test";
    private const string Device = "device_steps_test";
    private const string Checklist = """{"msToDo":{"checklistItems":[{"displayName":"Zulu first","isChecked":false},{"displayName":"Alpha second","isChecked":false},{"displayName":"Review complete","isChecked":true,"checkedDateTime":"2026-10-12T09:00:00Z"}]}}""";
    private static ProviderSourceItem Source(string? snapshot = Checklist) => new()
    {
        Id = "source_steps", IntegrationId = IntegrationIds.MicrosoftToDo, ProviderConnectionId = "mstodo_default",
        ExternalId = "external_steps", ProviderTaskId = "list|external_steps", Title = "Prepare release", SnapshotJson = snapshot,
    };
    private async Task<SqliteTaskStore> StoreAsync()
    {
        var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
        await store.InitializeAsync();
        return store;
    }
    private static Task<IReadOnlyList<TaskItem>> Children(SqliteTaskStore store, string id) =>
        store.GetTasksAsync(new() { ParentId = id, Kind = TaskListKind.All, IncludeSubtasks = true,
            SortMode = TaskSortMode.CreatedNewest, SortDirection = TaskSortDirection.Descending });
    private static async Task<TaskItem> Adopt(SqliteTaskStore store, ProviderSourceItem source)
    {
        await store.UpsertProviderSourceItemAsync(source);
        return (await store.AdoptProviderSourceItemAsync(source.Id))!;
    }

    [Fact]
    public async Task Adoption_creates_local_children_in_inspector_order_with_completion_and_no_provider_identity()
    {
        var store = await StoreAsync();
        var task = await Adopt(store, Source());
        var children = await Children(store, task.Id);
        Assert.Equal(new[] { "Zulu first", "Alpha second", "Review complete" }, children.Select(child => child.Title));
        Assert.True(children[0].CreatedAt < children[1].CreatedAt && children[1].CreatedAt < children[2].CreatedAt);
        Assert.All(children, child => Assert.True(child.CreatedAt < DateTimeOffset.UtcNow));
        Assert.All(children, child =>
        {
            Assert.Equal(IntegrationIds.Local, child.IntegrationId);
            Assert.Equal(task.SpaceId, child.SpaceId);
            Assert.Null(child.ExternalId); Assert.Null(child.ProviderConnectionId); Assert.Null(child.SourceIntegrationId);
        });
        Assert.False(children[0].IsCompleted);
        Assert.Null(children[0].CompletedAt);
        Assert.True(children[2].IsCompleted);
        Assert.Equal(DateTimeOffset.Parse("2026-10-12T09:00:00Z"), children[2].CompletedAt);
        Assert.Contains("\"msToDoStepsApplied\":true", task.SourceMetadataJson);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"msToDo\":{\"id\":\"cached\"}}")]
    public async Task Cached_snapshot_waits_for_checklist_then_backfills_once_without_restoring_user_edits(string? snapshot)
    {
        var store = await StoreAsync();
        var task = await Adopt(store, Source(snapshot));
        Assert.DoesNotContain("msToDoStepsApplied", task.SourceMetadataJson!);
        await store.UpsertTaskAsync(new() { Id = "existing_child", ParentId = task.Id, Title = "Existing local child" });
        await store.UpsertProviderSourceItemAsync(Source());
        var children = await Children(store, task.Id);
        Assert.Equal(4, children.Count);
        var first = children.Single(child => child.Title == "Zulu first");
        await store.DeleteTaskAsync(first.Id);
        var second = children.Single(child => child.Title == "Alpha second");
        await store.UpsertTaskAsync(second with { Title = "User renamed", CompletionState = TaskCompletionState.Completed });
        await store.UpsertProviderSourceItemAsync(Source());
        Assert.Equal(3, (await Children(store, task.Id)).Count);
        Assert.DoesNotContain(await Children(store, task.Id), child => child.Title is "Zulu first" or "Alpha second");
        Assert.True((await store.GetTaskAsync(second.Id))!.IsCompleted);
        Assert.Contains("\"msToDoStepsApplied\":true", (await store.GetTaskAsync(task.Id))!.SourceMetadataJson);
    }

    [Fact]
    public async Task Explicit_empty_checklist_is_marked_and_malformed_checklist_rolls_back()
    {
        var store = await StoreAsync();
        var task = await Adopt(store, Source("""{"msToDo":{"checklistItems":[]}}"""));
        await store.UpsertProviderSourceItemAsync(Source());
        Assert.Empty(await Children(store, task.Id));
        var bad = Source("""{"msToDo":{"checklistItems":[{"displayName":"Valid"},7]}}""") with { Id = "bad", ExternalId = "bad" };
        await store.UpsertProviderSourceItemAsync(bad);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdoptProviderSourceItemAsync(bad.Id));
        Assert.Single(await store.GetTasksAsync(new() { Kind = TaskListKind.All, IncludeSubtasks = true }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cloud_children_use_existing_create_parent_completion_and_consecutive_versions(bool workspace)
    {
        var store = await StoreAsync();
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, workspace, workspace);
        store.PersonalCloudConnection = () => new() { InstanceId = Instance, DeviceId = Device,
            ServiceOrigin = new("https://example.test"), ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow };
        var task = await Adopt(store, Source());
        var child = (await Children(store, task.Id)).Single(item => item.IsCompleted);
        var entries = (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Where(entry => entry.EntityId == child.Id).ToArray();
        Assert.Equal(workspace ? "task.createRich" : "task.create", entries[0].Operation);
        Assert.Contains(entries, entry => entry.Operation == "task.setParent");
        Assert.Contains(entries, entry => entry.Operation == "task.complete");
        Assert.All(entries.Skip(1).Select((entry, index) => (entry, index)), pair =>
        {
            using var json = JsonDocument.Parse(pair.entry.CanonicalJson);
            Assert.Equal(pair.index + 1L, json.RootElement.GetProperty("baseEntityVersion").GetInt64());
        });
        var version = await store.GetPersonalCloudEntityVersionAsync(Instance, child.Id);
        Assert.Equal(entries.Length, version);
        var edit = await store.UpdateTaskTitleWithPersonalCloudIntentAsync(child.Id, "User edit", version!.Value, Instance, Device);
        using var mutation = JsonDocument.Parse(edit.CanonicalJson);
        Assert.Equal(entries.Length, mutation.RootElement.GetProperty("baseEntityVersion").GetInt64());
        Assert.Empty(await store.GetPersonalCloudTaskConflictsAsync(Instance));
    }

    [Fact]
    public async Task Incomplete_checklist_fields_skip_missing_names_and_treat_null_or_missing_checks_as_open()
    {
        var store = await StoreAsync();
        var task = await Adopt(store, Source("""{"msToDo":{"checklistItems":[{"isChecked":true},{"displayName":null,"isChecked":true},{"displayName":"Null check","isChecked":null},{"displayName":"Missing check"},{"displayName":"Checked","isChecked":true}]}}"""));
        var children = await Children(store, task.Id);
        Assert.Equal(new[] { "Null check", "Missing check", "Checked" }, children.Select(child => child.Title));
        Assert.False(children[0].IsCompleted);
        Assert.False(children[1].IsCompleted);
        Assert.True(children[2].IsCompleted);
    }

    [Fact]
    public async Task Marked_refresh_does_not_hydrate_unrelated_tasks_stage_or_acquire_a_cloud_lease()
    {
        var store = await StoreAsync();
        var task = await Adopt(store, Source());
        await store.UpsertTaskAsync(new() { Id = "unrelated", Title = "Unrelated" });
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE tasks SET priority='invalid' WHERE id='unrelated'";
            await corrupt.ExecuteNonQueryAsync();
        }
        store.PersonalCloudConnection = () => throw new InvalidOperationException("Unchanged steps must not stage cloud tasks.");
        await store.UpsertProviderSourceItemAsync(Source());
        using (PersonalCloudConnectionLease.AcquireReplacement(store.DatabasePath))
        {
            await store.UpsertProviderSourceItemAsync(Source());
            Assert.Null(await store.AdoptProviderSourceItemAsync("other"));
            var result = await new TaskSyncEngine(store).SyncAsync(new SnapshotProvider([new() { Id = "remote", ExternalId = Source().ExternalId,
                IntegrationId = IntegrationIds.MicrosoftToDo, Title = Source().Title, ProviderMetadataJson = Checklist }]));
            Assert.True(result.Success, result.Error);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Todoist_adoption_does_not_acquire_a_Microsoft_step_lease(bool connected)
    {
        var store = await StoreAsync();
        var source = Source() with { IntegrationId = IntegrationIds.Todoist, ProviderConnectionId = "todoist_default" };
        await store.UpsertProviderSourceItemAsync(source);
        store.PersonalCloudConnection = () => connected ? Cloud() : null;
        using (PersonalCloudConnectionLease.AcquireReplacement(store.DatabasePath))
        {
            var task = (await store.AdoptProviderSourceItemAsync(source.Id))!;
            Assert.Empty(await Children(store, task.Id));
        }
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public async Task Unpaired_Microsoft_step_backfill_does_not_block_a_cloud_connection_change()
    {
        var store = await StoreAsync();
        var parent = await Adopt(store, Source("{}"));
        store.PersonalCloudConnection = () => null;
        using (PersonalCloudConnectionLease.AcquireReplacement(store.DatabasePath))
        {
            var result = await new TaskSyncEngine(store).SyncAsync(new SnapshotProvider([new() { Id = "remote", ExternalId = Source().ExternalId,
                IntegrationId = IntegrationIds.MicrosoftToDo, Title = Source().Title, ProviderMetadataJson = Checklist }]));
            Assert.True(result.Success, result.Error);
            Assert.Equal(3, (await Children(store, parent.Id)).Count);
        }
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"msToDo\":{\"checklistItems\":[]}}")]
    public async Task Connected_adoption_without_step_children_does_not_acquire_cloud_lease(string snapshot)
    {
        var store = await StoreAsync();
        await store.UpsertProviderSourceItemAsync(Source(snapshot));
        store.PersonalCloudConnection = Cloud;
        using (PersonalCloudConnectionLease.AcquireReplacement(store.DatabasePath))
        {
            var task = (await store.AdoptProviderSourceItemAsync(Source().Id))!;
            Assert.Empty(await Children(store, task.Id));
        }
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connected_step_adoption_and_backfill_block_disconnect_before_creating_children(bool backfill)
    {
        var store = await StoreAsync();
        var source = Source();
        var parent = backfill ? await Adopt(store, Source("{}")) : null;
        if (!backfill) await store.UpsertProviderSourceItemAsync(source);
        store.PersonalCloudConnection = Cloud;
        using (PersonalCloudConnectionLease.AcquireReplacement(store.DatabasePath))
        {
            var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(async () =>
            {
                if (backfill) await store.UpsertProviderSourceItemAsync(source);
                else await store.AdoptProviderSourceItemAsync(source.Id);
            });
            Assert.Equal("connection_busy", error.Code);
            if (parent is not null) Assert.Empty(await Children(store, parent.Id));
            Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        }
        if (backfill) await store.UpsertProviderSourceItemAsync(source);
        else parent = await store.AdoptProviderSourceItemAsync(source.Id);
        Assert.Equal(3, (await Children(store, parent!.Id)).Count);
        Assert.NotEmpty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public async Task Cloud_connection_is_rechecked_under_the_lease_before_staging_steps()
    {
        var store = await StoreAsync();
        var reads = 0;
        store.PersonalCloudConnection = () => ++reads == 1 ? Cloud() : null;
        var parent = await Adopt(store, Source());
        Assert.True(reads >= 2);
        Assert.Equal(3, (await Children(store, parent.Id)).Count);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        using var replacement = PersonalCloudConnectionLease.AcquireReplacement(store.DatabasePath);
    }

    private static PersonalCloudConnectionProfile Cloud() => new() { InstanceId = Instance, DeviceId = Device,
        ServiceOrigin = new("https://example.test"), ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow };

    [Fact]
    public async Task Todoist_snapshots_never_promote_checklists()
    {
        var store = await StoreAsync();
        var task = await Adopt(store, Source() with { IntegrationId = IntegrationIds.Todoist, ProviderConnectionId = "todoist_default" });
        Assert.Empty(await Children(store, task.Id));
        Assert.DoesNotContain("msToDoStepsApplied", task.SourceMetadataJson!);
    }

    [Fact]
    public async Task Backfill_stages_once_per_sync_batch_and_never_stages_an_unchanged_sync()
    {
        var store = await StoreAsync();
        var tasks = new List<TaskItem>();
        for (var index = 0; index < 4; index++)
        {
            var source = Source("{}") with { Id = $"source_{index}", ExternalId = $"external_{index}" };
            await Adopt(store, source);
            tasks.Add(new() { Id = source.Id, ExternalId = source.ExternalId, IntegrationId = IntegrationIds.MicrosoftToDo,
                Title = source.Title, ProviderMetadataJson = Checklist });
        }
        var targetReads = 0;
        store.PersonalCloudConnection = () => { targetReads++; return Cloud(); };
        var engine = new TaskSyncEngine(store);
        var provider = new SnapshotProvider(tasks);
        Assert.True((await engine.SyncAsync(provider)).Success);
        Assert.Equal(2, targetReads);
        Assert.True((await engine.SyncAsync(provider)).Success);
        Assert.Equal(2, targetReads);
        Assert.Equal(16, (await store.GetTasksAsync(new() { Kind = TaskListKind.All, IncludeSubtasks = true })).Count);
    }

    [Fact]
    public async Task Legacy_bootstrap_replacement_preserves_marker_and_user_child_deletions()
    {
        var store = await StoreAsync();
        var parent = await Adopt(store, Source());
        foreach (var child in await Children(store, parent.Id)) await store.DeleteTaskAsync(child.Id);
        var bootstrap = new PersonalCloudBootstrap
        {
            ProtocolVersion = 1, Product = "tasks", Checkpoint = 1,
            Snapshot = new()
            {
                Spaces = [new(SpaceIds.Default, "Default", null)], Projects = [], Labels = [], TaskLabels = [], Tombstones = [],
                Tasks = [new(parent.Id, parent.SpaceId, null, null, parent.Title, "open", "inbox", 2, null, null, null, null, null,
                    "2026-10-12T00:00:00.000Z", 1, null)],
            },
        };
        for (var index = 0; index < 2; index++)
        {
            await store.ReplacePersonalCloudReplicaFromBootstrapAsync(Instance, Device, PersonalCloudK0Codec.Serialize(bootstrap));
            await store.UpsertProviderSourceItemAsync(Source());
            Assert.Empty(await Children(store, parent.Id));
            Assert.Contains("\"msToDoStepsApplied\":true", (await store.GetTaskAsync(parent.Id))!.SourceMetadataJson);
        }
    }

    private sealed class SnapshotProvider(IReadOnlyList<TaskItem> tasks) : ISyncProvider
    {
        public string IntegrationId => IntegrationIds.MicrosoftToDo;
        public string ProviderConnectionId => "mstodo_default";
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderSnapshot(tasks, [], []));
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public void Dispose() => TestDirectory.Delete(directory);
}
