using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudBatchSyncTests
{
    private async Task<(SqliteTaskStore Store, Handler Handler)> ConflictStoreAsync(bool batch = true)
    {
        var (store, handler) = await FanoutStoreAsync(false);
        if (batch) handler.CapabilityOverride = handler.CapabilityOverride![..^1] + ",\"mutationBatch\":{\"protocolVersion\":1,\"path\":\"/v1/tasks/mutations/batch\",\"maxMutations\":2,\"maxRequestUtf8Bytes\":16384}}";
        handler.StrictSequencing = true;
        return (store, handler);
    }

    private static async Task<PersonalCloudK1OutboxEntry> PatchAsync(SqliteTaskStore store, string taskId, string field, object? value)
    {
        var task = (await store.GetTaskAsync(taskId))!;
        return await store.PatchTaskWithPersonalCloudIntentAsync(taskId, new Dictionary<string, object?> { [field] = value }, task.Revision, Instance, Device);
    }

    private static object CloudTask(long version = 2, string title = "Cloud title", string? notes = "Notes")
    {
        var node = JsonSerializer.SerializeToNode(PersonalCloudWorkspaceTests.TaskRecord(version: version))!;
        node["title"] = title; node["notes"] = notes;
        return node;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conflict_parks_all_task_edits_and_unrelated_uploads_continue_contiguously_in_same_pass(bool batch)
    {
        var (store, handler) = await ConflictStoreAsync(batch);
        var first = await PatchAsync(store, "task_test", "title", "Desktop title");
        await PatchAsync(store, "task_test", "priority", 1);
        var unrelated = await PatchAsync(store, "task_second", "notes", "Unrelated");
        handler.ConflictEntity = "task_test";
        handler.CurrentCheckpoint = 2;
        handler.ChangePageJson = PersonalCloudWorkspaceTests.Page(2, tasks: [CloudTask()]);
        var result = await (await CreateClientAsync(store, handler)).SyncAsync();
        Assert.Null(result.BlockedCode);
        Assert.Equal(1, result.PushedMutations);
        Assert.Equal(1, result.TaskConflictCount);
        using var sent = JsonDocument.Parse(Assert.Single(handler.Envelopes));
        Assert.Equal("task_second", sent.RootElement.GetProperty("entityId").GetString());
        Assert.Equal(1, sent.RootElement.GetProperty("deviceSequence").GetInt64());
        Assert.NotEqual(unrelated.MutationId, sent.RootElement.GetProperty("mutationId").GetString());
        Assert.Equal("Desktop title", (await store.GetTaskAsync("task_test"))!.Title);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        var reopened = new SqliteTaskStore(store.DatabasePath); await reopened.InitializeAsync();
        var conflict = Assert.Single(await reopened.GetPersonalCloudTaskConflictsAsync(Instance));
        Assert.Equal(2, conflict.Fields.Count);
        Assert.Contains(conflict.Fields, f => f.Name == "title" && f.DesktopValue == "Desktop title" && f.CloudValue == "Cloud title");
        Assert.True(await reopened.GetPersonalCloudEntityVersionAsync(Instance, first.EntityId) >= 3);
        var guard = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => reopened.EnsureNoPendingPersonalCloudIntentsAsync());
        Assert.Equal("pending_intents", guard.Code);
        await PatchAsync(reopened, "task_third", "notes", "Next unrelated");
        Assert.Equal(2, Assert.Single(await reopened.GetPendingPersonalCloudOutboxAsync(Instance, Device)).DeviceSequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Safe_field_conflict_auto_resolves_without_overwriting_cloud_fields(bool alreadyEqual)
    {
        var (store, handler) = await ConflictStoreAsync();
        await PatchAsync(store, "task_test", "title", "Desktop title");
        handler.ConflictEntity = "task_test"; handler.CurrentCheckpoint = 2;
        handler.ChangePageJson = PersonalCloudWorkspaceTests.Page(2, tasks: [CloudTask(title: alreadyEqual ? "Desktop title" : "Example", notes: "Cloud notes")]);
        var client = await CreateClientAsync(store, handler);
        var result = await client.SyncAsync();
        Assert.Equal(0, result.TaskConflictCount);
        Assert.Empty(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        var pending = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        Assert.Equal(alreadyEqual ? 0 : 1, pending.Count);
        Assert.Equal("Cloud notes", (await store.GetTaskAsync("task_test"))!.Notes);
        Assert.Equal("Desktop title", (await store.GetTaskAsync("task_test"))!.Title);
        if (!alreadyEqual)
        {
            using var patch = JsonDocument.Parse(pending[0].CanonicalJson);
            Assert.Equal(2, patch.RootElement.GetProperty("baseEntityVersion").GetInt64());
            Assert.Single(patch.RootElement.GetProperty("payload").GetProperty("fields").EnumerateObject());
            handler.ConflictEntity = null;
            Assert.Equal(1, (await client.SyncAsync()).PushedMutations);
        }
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_field_conflict_requires_choice_and_both_choices_converge(bool keepDesktop)
    {
        var (store, handler) = await ConflictStoreAsync();
        await store.UpdateTaskTitleWithPersonalCloudIntentAsync("task_test", "Desktop title", 1, Instance, Device);
        handler.ConflictEntity = "task_test"; handler.CurrentCheckpoint = 2;
        handler.ChangePageJson = PersonalCloudWorkspaceTests.Page(2, tasks: [CloudTask(notes: "Cloud notes")]);
        var client = await CreateClientAsync(store, handler);
        Assert.Equal(1, (await client.SyncAsync()).TaskConflictCount);
        Assert.Equal(0, await store.AutoResolvePersonalCloudTaskConflictsAsync(Instance));
        await store.ResolvePersonalCloudTaskConflictAsync(Instance, "task_test", keepDesktop);
        Assert.Empty(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        Assert.Equal(keepDesktop ? "Desktop title" : "Cloud title", (await store.GetTaskAsync("task_test"))!.Title);
        Assert.Equal("Cloud notes", (await store.GetTaskAsync("task_test"))!.Notes);
        handler.ConflictEntity = null;
        Assert.Equal(keepDesktop ? 1 : 0, (await client.SyncAsync()).PushedMutations);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await store.EnsureNoPendingPersonalCloudIntentsAsync();
        var missing = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.ResolvePersonalCloudTaskConflictAsync(Instance, "task_test", keepDesktop));
        Assert.Equal("entity_not_found", missing.Code);
    }

    [Fact]
    public async Task Unknown_base_is_conservative_and_later_desktop_patch_is_preserved()
    {
        var (store, _) = await ConflictStoreAsync();
        var rejected = await PatchAsync(store, "task_test", "title", "Desktop title");
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(2, tasks: [CloudTask(title: "Example", notes: "Cloud notes")]));
        Assert.True(await store.ParkPersonalCloudTaskConflictAsync(rejected));
        Assert.Equal(0, await store.AutoResolvePersonalCloudTaskConflictsAsync(Instance));
        await PatchAsync(store, "task_test", "title", "Newest desktop title");
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal("Newest desktop title", Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance)).Title);
        await store.ResolvePersonalCloudTaskConflictAsync(Instance, "task_test", true);
        Assert.Equal("Newest desktop title", (await store.GetTaskAsync("task_test"))!.Title);
    }

    [Fact]
    public async Task Parked_tree_intent_retains_indirect_overlays_and_cloud_choice_restores_affected_children()
    {
        var (store, _) = await ConflictStoreAsync();
        var child = JsonSerializer.SerializeToNode(PersonalCloudWorkspaceTests.TaskRecord("task_child"))!;
        child["parentId"] = "task_test";
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(2, tasks: [child]));
        var rejected = await PatchAsync(store, "task_test", "title", "Desktop title");
        await store.MoveTaskTreeToSpaceWithPersonalCloudIntentAsync("task_test", "space_destination", "Destination", new Dictionary<string, long> { ["task_test"] = 2, ["task_child"] = 1 }, Instance, Device);
        await store.ParkPersonalCloudTaskConflictAsync(rejected);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(3, tasks: [CloudTask(), child]));
        Assert.Equal("space_destination", (await store.GetTaskAsync("task_child"))!.SpaceId);
        Assert.Equal(0, await store.AutoResolvePersonalCloudTaskConflictsAsync(Instance));
        await store.ResolvePersonalCloudTaskConflictAsync(Instance, "task_test", false);
        Assert.Equal("space_test", (await store.GetTaskAsync("task_test"))!.SpaceId);
        Assert.Equal("space_test", (await store.GetTaskAsync("task_child"))!.SpaceId);
        Assert.Empty(await store.GetPersonalCloudTaskConflictsAsync(Instance));
    }

    [Fact]
    public async Task Parking_failure_rolls_back_the_entire_queue_and_empty_resolution_is_a_noop()
    {
        var (store, _) = await ConflictStoreAsync();
        Assert.Empty(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        Assert.Equal(0, await store.AutoResolvePersonalCloudTaskConflictsAsync(Instance));
        var rejected = await PatchAsync(store, "task_test", "title", "Desktop title");
        await PatchAsync(store, "task_second", "notes", "Other");
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}"); await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_conflict BEFORE INSERT ON personal_cloud_task_conflicts BEGIN SELECT RAISE(ABORT,'injected'); END";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(() => store.ParkPersonalCloudTaskConflictAsync(rejected));
        Assert.Equal(original, await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Empty(await store.GetPersonalCloudTaskConflictsAsync(Instance));
    }
}
