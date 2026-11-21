using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudBatchSyncTests
{
    [Fact]
    public async Task Child_edit_before_parked_move_uploads_and_survives_discarding_the_move()
    {
        var (store, handler) = await ConflictStoreAsync();
        var child = JsonSerializer.SerializeToNode(PersonalCloudWorkspaceTests.TaskRecord("task_child"))!;
        child["parentId"] = "task_test";
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(2, tasks: [child]));
        var rejected = await PatchAsync(store, "task_test", "title", "Desktop parent");
        await PatchAsync(store, "task_child", "notes", "Independent child notes");
        await store.MoveTaskTreeToSpaceWithPersonalCloudIntentAsync("task_test", "space_destination", "Destination",
            new Dictionary<string, long> { ["task_test"] = 2, ["task_child"] = 2 }, Instance, Device);
        await store.ParkPersonalCloudTaskConflictAsync(rejected);
        Assert.Equal("task_child", Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).EntityId);
        child["entityVersion"] = 2; child["notes"] = "Independent child notes";
        handler.CurrentCheckpoint = 3;
        handler.ChangePageJson = PersonalCloudWorkspaceTests.Page(3, tasks: [CloudTask(), child]);
        var result = await (await CreateClientAsync(store, handler)).SyncAsync();
        Assert.Null(result.BlockedCode);
        Assert.Equal(1, result.PushedMutations);
        var card = Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        await store.ResolvePersonalCloudTaskConflictAsync(Instance, card.TaskId, false, expectedRevision: card.RevisionToken);
        var restored = (await store.GetTaskAsync("task_child"))!;
        Assert.Equal("Independent child notes", restored.Notes);
        Assert.Equal("space_test", restored.SpaceId);
        Assert.Empty(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        await store.EnsureNoPendingPersonalCloudIntentsAsync();
    }

    [Fact]
    public async Task Project_deletion_stays_uploadable_and_task_conflict_can_resolve_after_its_echo()
    {
        var (store, handler) = await ConflictStoreAsync();
        var rejected = await PatchAsync(store, "task_test", "title", "Desktop title");
        await store.DeleteProjectWithPersonalCloudIntentAsync("project_test", Instance, Device);
        await store.ParkPersonalCloudTaskConflictAsync(rejected);
        Assert.Equal("project.delete", Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Operation);
        var card = Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        var pending = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            store.ResolvePersonalCloudTaskConflictAsync(Instance, card.TaskId, false, expectedRevision: card.RevisionToken));
        Assert.Equal("pending_intents", pending.Code);
        var cloud = JsonSerializer.SerializeToNode(CloudTask(version: 3))!;
        cloud["projectId"] = null;
        handler.CurrentCheckpoint = 2;
        handler.ChangePageJson = PersonalCloudWorkspaceTests.Page(2, projects: [PersonalCloudWorkspaceTests.Project(2, "2026-10-14T00:00:00.000Z")], tasks: [cloud]);
        var result = await (await CreateClientAsync(store, handler)).SyncAsync();
        Assert.Null(result.BlockedCode);
        Assert.Equal(1, result.PushedMutations);
        card = Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        await store.ResolvePersonalCloudTaskConflictAsync(Instance, card.TaskId, false, expectedRevision: card.RevisionToken);
        Assert.Null((await store.GetTaskAsync("task_test"))!.ProjectId);
        Assert.Empty(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        await store.EnsureNoPendingPersonalCloudIntentsAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Dependent_child_edits_park_and_resolve_with_parent_move(bool editAfterParking, bool completeChild)
    {
        var (store, handler) = await ConflictStoreAsync();
        var child = JsonSerializer.SerializeToNode(PersonalCloudWorkspaceTests.TaskRecord("task_child"))!;
        child["parentId"] = "task_test";
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(2, tasks: [child]));
        var rejected = await PatchAsync(store, "task_test", "title", "Desktop parent");
        await store.MoveTaskTreeToSpaceWithPersonalCloudIntentAsync("task_test", "space_destination", "Destination",
            new Dictionary<string, long> { ["task_test"] = 2, ["task_child"] = 1 }, Instance, Device);
        if (!editAfterParking) await PatchAsync(store, "task_child", "notes", "Desktop child notes");
        await store.ParkPersonalCloudTaskConflictAsync(rejected);
        if (editAfterParking) await PatchAsync(store, "task_child", "notes", "Desktop child notes");
        if (completeChild)
            await store.CompleteTaskWithPersonalCloudIntentAsync("task_child", DateTimeOffset.UtcNow,
                (await store.GetPersonalCloudEntityVersionAsync(Instance, "task_child"))!.Value, Instance, Device);
        await PatchAsync(store, "task_second", "notes", "Unrelated");
        Assert.Equal("task_second", Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).EntityId);
        var card = Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        Assert.False(card.CanKeepDesktop);
        Assert.Contains(card.Fields, f => f.DesktopValue == "Desktop child notes");
        handler.CurrentCheckpoint = 3;
        handler.ChangePageJson = PersonalCloudWorkspaceTests.Page(3, tasks: [CloudTask(), child]);
        var result = await (await CreateClientAsync(store, handler)).SyncAsync();
        Assert.Null(result.BlockedCode);
        Assert.Equal(1, result.PushedMutations);
        await store.ResolvePersonalCloudTaskConflictAsync(Instance, "task_test", false);
        Assert.Equal("space_test", (await store.GetTaskAsync("task_child"))!.SpaceId);
        Assert.Equal("Notes", (await store.GetTaskAsync("task_child"))!.Notes);
        Assert.False((await store.GetTaskAsync("task_child"))!.IsCompleted);
        Assert.Equal(1, await store.GetPersonalCloudEntityVersionAsync(Instance, "task_child"));
        Assert.Empty(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        await store.EnsureNoPendingPersonalCloudIntentsAsync();
        await store.CompleteTaskWithPersonalCloudIntentAsync("task_child", DateTimeOffset.UtcNow, 1, Instance, Device);
        Assert.Equal(2, Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).DeviceSequence);
        result = await (await CreateClientAsync(store, handler)).SyncAsync();
        Assert.Null(result.BlockedCode);
        Assert.Equal(1, result.PushedMutations);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await store.EnsureNoPendingPersonalCloudIntentsAsync();
    }

    [Fact]
    public async Task First_workspace_sync_pulls_comparison_records_without_replacing_parked_edits()
    {
        var (store, handler) = await ConflictStoreAsync();
        var rejected = await PatchAsync(store, "task_test", "title", "Desktop title");
        await store.ParkPersonalCloudTaskConflictAsync(rejected);
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE personal_cloud_workspace_capabilities SET hydrated=0";
        await command.ExecuteNonQueryAsync();
        handler.WorkspaceBootstrapJson = PersonalCloudK0Codec.Serialize(new
        {
            protocolVersion = 1, product = "tasks", checkpoint = 2,
            snapshot = new { spaces = new[] { PersonalCloudWorkspaceTests.Space() }, projects = new[] { PersonalCloudWorkspaceTests.Project() },
                tasks = new[] { CloudTask() }, tombstones = Array.Empty<object>(), labels = Array.Empty<object>(),
                taskLabels = Array.Empty<object>(), sourceDescriptors = Array.Empty<object>(), externalCommands = Array.Empty<object>(),
                catalog = Array.Empty<object>(), intents = Array.Empty<object>(), routes = Array.Empty<object>() },
        });
        handler.CurrentCheckpoint = 2;
        handler.ChangePageJson = PersonalCloudWorkspaceTests.Page(2, tasks: [CloudTask()]);
        var result = await (await CreateClientAsync(store, handler)).SyncAsync();
        Assert.Null(result.BlockedCode);
        Assert.True(handler.ChangeRequests > 0);
        Assert.False(await store.HasPersonalCloudWorkspaceBaselineAsync(Instance));
        Assert.Equal("Desktop title", (await store.GetTaskAsync("task_test"))!.Title);
        Assert.Equal("Cloud title", Assert.Single(Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance)).Fields).CloudValue);
        await store.ResolvePersonalCloudTaskConflictAsync(Instance, "task_test", false);
        await (await CreateClientAsync(store, handler)).SyncAsync();
        Assert.True(await store.HasPersonalCloudWorkspaceBaselineAsync(Instance));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Card_revision_rejects_choices_after_cloud_or_parked_rows_change(bool changeCloud, bool keepDesktop)
    {
        var (store, _) = await ConflictStoreAsync();
        var rejected = await PatchAsync(store, "task_test", "title", "Desktop title");
        await store.ParkPersonalCloudTaskConflictAsync(rejected);
        var card = Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        if (changeCloud)
            await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(2, tasks: [CloudTask()]));
        else
            await PatchAsync(store, "task_test", "title", "Later desktop title");
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            store.ResolvePersonalCloudTaskConflictAsync(Instance, card.TaskId, keepDesktop, expectedRevision: card.RevisionToken));
        Assert.Equal("conflict_changed", error.Code);
        var refreshed = Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        Assert.NotEqual(card.RevisionToken, refreshed.RevisionToken);
        Assert.Equal(changeCloud ? "Desktop title" : "Later desktop title", (await store.GetTaskAsync("task_test"))!.Title);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await store.ResolvePersonalCloudTaskConflictAsync(Instance, refreshed.TaskId, keepDesktop, expectedRevision: refreshed.RevisionToken);
        Assert.Empty(await store.GetPersonalCloudTaskConflictsAsync(Instance));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "provider_rejected")]
    [InlineData(true, "provider_rejected")]
    public async Task Unversioned_provider_rows_upload_and_settle_whether_authored_before_or_after_parking(bool parkFirst, string? error)
    {
        var (store, handler) = await ConflictStoreAsync();
        var rejected = await PatchAsync(store, "task_test", "title", "Desktop title");
        if (parkFirst) await store.ParkPersonalCloudTaskConflictAsync(rejected);
        var source = new K0SafeSourceDescriptor { IntegrationKind = "todoist", ExternalIdHash = new string('a', 64),
            Title = "Source title", RefreshedAt = "2026-10-13T00:00:00.000Z" };
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO personal_cloud_provider_sources(instance_id,task_id,source_json) VALUES(@i,'task_test',@j);
            INSERT INTO personal_cloud_provider_attempts(claim_id,instance_id,task_id,requested_seq,device_id,account_binding,command,source_json,state)
            VALUES('claim_conflict',@i,'task_test',42,@d,'synthetic_account','complete',@j,'dispatching');
            """;
        seed.Parameters.AddWithValue("@i", Instance); seed.Parameters.AddWithValue("@d", Device);
        seed.Parameters.AddWithValue("@j", PersonalCloudK0Codec.Serialize(source));
        await seed.ExecuteNonQueryAsync();
        await store.CompletePersonalCloudProviderAttemptAsync(Instance,
            new("claim_conflict", "task_test", 42, Device, "synthetic_account", "complete", "dispatching", source), error);
        await store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, "task_test",
            source with { Title = "Refreshed source title", RefreshedAt = "2026-10-13T01:00:00.000Z" });
        if (!parkFirst) await store.ParkPersonalCloudTaskConflictAsync(rejected);

        var pending = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        Assert.Equal(new long[] { 1, 2 }, pending.Select(r => r.DeviceSequence));
        Assert.Equal(new[] { error is null ? "externalCommand.confirm" : "externalCommand.reject", "source.refreshSnapshot" }, pending.Select(r => r.Operation));
        var attempt = connection.CreateCommand();
        attempt.CommandText = "SELECT result_mutation_id FROM personal_cloud_provider_attempts WHERE claim_id='claim_conflict'";
        Assert.Equal(pending[0].MutationId, await attempt.ExecuteScalarAsync());
        Assert.True(Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance)).CanKeepDesktop);
        handler.CurrentCheckpoint = 2;
        handler.ChangePageJson = PersonalCloudWorkspaceTests.Page(2, tasks: [CloudTask()]);
        var result = await (await CreateClientAsync(store, handler)).SyncAsync();
        Assert.Equal(2, result.PushedMutations);
        Assert.Null(result.BlockedCode);
        Assert.Equal(pending.Select(r => r.CanonicalJson), handler.Envelopes);
        attempt.CommandText = "SELECT state FROM personal_cloud_provider_attempts WHERE claim_id='claim_conflict'";
        Assert.Equal("settled", await attempt.ExecuteScalarAsync());
        await store.ResolvePersonalCloudTaskConflictAsync(Instance, "task_test", false);
        await store.EnsureNoPendingPersonalCloudIntentsAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_cached_cloud_can_discard_conflict_and_restore_base_or_keep_recovery_copy(bool unknownBase)
    {
        var (store, _) = await ConflictStoreAsync();
        var rejected = await PatchAsync(store, "task_test", "title", "Desktop title");
        await store.ParkPersonalCloudTaskConflictAsync(rejected);
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        var remove = connection.CreateCommand();
        remove.CommandText = "DELETE FROM personal_cloud_workspace_records WHERE kind='tasks' AND entity_id='task_test';" +
            (unknownBase ? "UPDATE personal_cloud_task_conflicts SET base_record_json=NULL;" : "");
        await remove.ExecuteNonQueryAsync();
        var conflict = Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        Assert.False(conflict.CanKeepDesktop);
        Assert.Contains("recovery copy", conflict.ResolutionNotice);
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.ResolvePersonalCloudTaskConflictAsync(Instance, "task_test", true));
        await store.ResolvePersonalCloudTaskConflictAsync(Instance, "task_test", false);
        Assert.Equal(unknownBase ? "Desktop title" : "Example", (await store.GetTaskAsync("task_test"))!.Title);
        Assert.Empty(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await store.EnsureNoPendingPersonalCloudIntentsAsync();
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(2, tasks: [CloudTask()]));
        Assert.Equal("Cloud title", (await store.GetTaskAsync("task_test"))!.Title);
    }

    [Fact]
    public async Task Project_conflict_values_show_names_including_deleted_projects()
    {
        var (store, _) = await ConflictStoreAsync();
        var project = JsonSerializer.SerializeToNode(PersonalCloudWorkspaceTests.Project())!;
        project["id"] = "project_destination"; project["name"] = "Destination project";
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(2, projects: [project]));
        var rejected = await PatchAsync(store, "task_test", "projectId", "project_destination");
        await store.ParkPersonalCloudTaskConflictAsync(rejected);
        var field = Assert.Single(Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance)).Fields);
        Assert.Equal("Project", field.DisplayName);
        Assert.Equal("Destination project", field.DesktopValue);
        Assert.Equal("Project", field.CloudValue);
        project["entityVersion"] = 2; project["deletedAt"] = "2026-10-13T00:00:00.000Z";
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(3, projects: [project]));
        Assert.Equal("Destination project", Assert.Single(Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance)).Fields).DesktopValue);
    }

    [Fact]
    public async Task Matching_values_explain_the_card_and_migration_index_survives_reopen()
    {
        var (store, _) = await ConflictStoreAsync();
        var rejected = await PatchAsync(store, "task_test", "title", "Example");
        await store.ParkPersonalCloudTaskConflictAsync(rejected);
        var reopened = new SqliteTaskStore(store.DatabasePath); await reopened.InitializeAsync();
        var conflict = Assert.Single(await reopened.GetPersonalCloudTaskConflictsAsync(Instance));
        Assert.Empty(conflict.Fields);
        Assert.Contains("already match the cloud", conflict.ResolutionNotice);
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}"); await connection.OpenAsync();
        var index = connection.CreateCommand();
        index.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_personal_cloud_task_conflicts_entity'";
        Assert.Equal(1L, await index.ExecuteScalarAsync());
        Assert.Equal(1, await reopened.AutoResolvePersonalCloudTaskConflictsAsync(Instance));
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("reopen")]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("label")]
    [InlineData("legacy")]
    public async Task New_operations_on_parked_tasks_never_upload_or_resequence_attempted_rows(string operation)
    {
        var (store, handler) = await ConflictStoreAsync();
        var rejected = await PatchAsync(store, "task_test", "title", "Desktop title");
        await store.ParkPersonalCloudTaskConflictAsync(rejected);
        if (operation == "reopen")
            await store.CompleteTaskWithPersonalCloudIntentAsync("task_test", DateTimeOffset.UtcNow, 2, Instance, Device);
        switch (operation)
        {
            case "complete": await store.CompleteTaskWithPersonalCloudIntentAsync("task_test", DateTimeOffset.UtcNow, 2, Instance, Device); break;
            case "reopen": await store.ReopenTaskWithPersonalCloudIntentAsync("task_test", DateTimeOffset.UtcNow, 3, Instance, Device); break;
            case "delete": await store.DeleteTaskWithPersonalCloudIntentAsync("task_test", DateTimeOffset.UtcNow, 2, Instance, Device); break;
            case "move": await store.MoveTaskTreeToSpaceWithPersonalCloudIntentAsync("task_test", "space_destination", "Destination", new Dictionary<string, long> { ["task_test"] = 2 }, Instance, Device); break;
            case "label": await store.MutateTaskRelationshipWithPersonalCloudIntentAsync("task_test", "task.addLabel", "label_new", "New label", 2, Instance, Device); break;
            case "legacy": await store.UpdateTaskPriorityWithPersonalCloudIntentAsync("task_test", 1, 2, Instance, Device); break;
        }
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        var conflict = Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        if (operation != "legacy")
        {
            Assert.False(conflict.CanKeepDesktop);
            Assert.Contains("discards all parked desktop changes", conflict.ResolutionNotice);
            Assert.NotEmpty(conflict.ResolutionNotice);
            var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.ResolvePersonalCloudTaskConflictAsync(Instance, "task_test", true));
            Assert.Equal("invalid_transition", error.Code);
        }
        await PatchAsync(store, "task_second", "notes", "Unrelated");
        await PatchAsync(store, "task_third", "notes", "More unrelated");
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        Assert.Equal(new long[] { 1, 2 }, original.Select(r => r.DeviceSequence));
        var client = await CreateClientAsync(store, handler);
        handler.CurrentCheckpoint = 2;
        handler.ChangePageJson = PersonalCloudWorkspaceTests.Page(2, tasks: [CloudTask()]);
        handler.LoseResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SyncAsync());
        Assert.Equal(original, await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        handler.LoseResponse = false;
        await client.SyncAsync();
        Assert.Equal(2, handler.DuplicateReceipts);
        Assert.Equal(original.Select(r => r.CanonicalJson), handler.Envelopes.Skip(2));
        await store.ResolvePersonalCloudTaskConflictAsync(Instance, "task_test", false);
        Assert.Empty(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        Assert.False((await store.GetTaskAsync("task_test"))!.IsCompleted);
    }

    [Fact]
    public async Task Auto_resolution_skips_pending_uploads_and_commits_other_safe_tasks()
    {
        var (store, _) = await ConflictStoreAsync();
        var first = await PatchAsync(store, "task_test", "title", "Desktop title");
        await store.ParkPersonalCloudTaskConflictAsync(first);
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO personal_cloud_outbox(mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at)
            VALUES(@m,'tasks',@i,@d,1,'task.patch','task_test',@j,@h,1);
            UPDATE personal_cloud_device_state SET next_device_sequence=2 WHERE instance_id=@i AND device_id=@d;
            """;
        insert.Parameters.AddWithValue("@m", first.MutationId); insert.Parameters.AddWithValue("@i", Instance); insert.Parameters.AddWithValue("@d", Device);
        insert.Parameters.AddWithValue("@j", first.CanonicalJson); insert.Parameters.AddWithValue("@h", first.CanonicalHash);
        await insert.ExecuteNonQueryAsync();
        var second = await PatchAsync(store, "task_second", "title", "Second desktop title");
        await store.ParkPersonalCloudTaskConflictAsync(second);
        Assert.Equal(1, await store.AutoResolvePersonalCloudTaskConflictsAsync(Instance));
        Assert.Equal("task_test", Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance)).TaskId);
        Assert.Contains(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device), r => r.MutationId == first.MutationId && r.CanonicalJson == first.CanonicalJson);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("deleted")]
    [InlineData("archived")]
    [InlineData("missing")]
    [InlineData("other-space")]
    public async Task Unsafe_replay_is_flagged_and_manual_keep_preserves_conflict_without_enqueuing(string reason)
    {
        var (store, _) = await ConflictStoreAsync();
        var rejected = await PatchAsync(store, "task_test", reason == "provider" ? "title" : "projectId", reason == "provider" ? "Desktop title" : "project_test");
        await store.ParkPersonalCloudTaskConflictAsync(rejected);
        var cloud = JsonSerializer.SerializeToNode(CloudTask())!;
        cloud["projectId"] = null;
        var project = JsonSerializer.SerializeToNode(PersonalCloudWorkspaceTests.Project(2))!;
        if (reason == "provider")
        {
            cloud["source"] = JsonNode.Parse(PersonalCloudK0Codec.Serialize(PersonalCloudK0Mapper.CreateSafeSourceDescriptor(new Openza.Tasks.Core.Models.TaskItem {
                Id = "task_test", Title = "Cloud title", SourceIntegrationId = Openza.Tasks.Core.Models.IntegrationIds.Todoist, SourceExternalId = "synthetic_source" })));
            cloud["sourceOwnerDeviceId"] = Device;
        }
        else if (reason == "deleted") project["deletedAt"] = "2026-10-13T00:00:00.000Z";
        else if (reason == "archived") project["status"] = "archived";
        else if (reason == "other-space") project["spaceId"] = "space_elsewhere";
        else
        {
            await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}"); await connection.OpenAsync();
            var remove = connection.CreateCommand(); remove.CommandText = "DELETE FROM personal_cloud_workspace_records WHERE kind='projects'"; await remove.ExecuteNonQueryAsync();
        }
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(2,
            spaces: reason == "other-space" ? [PersonalCloudWorkspaceTests.Space("space_elsewhere")] : [],
            projects: reason is "provider" or "missing" ? [] : [project], tasks: [cloud]));
        var conflict = Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        Assert.False(conflict.CanKeepDesktop);
        Assert.NotEmpty(conflict.ResolutionNotice);
        Assert.Equal(0, await store.AutoResolvePersonalCloudTaskConflictsAsync(Instance));
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.ResolvePersonalCloudTaskConflictAsync(Instance, "task_test", true));
        Assert.Equal("invalid_transition", error.Code);
        Assert.Single(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await store.ResolvePersonalCloudTaskConflictAsync(Instance, "task_test", false);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

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
