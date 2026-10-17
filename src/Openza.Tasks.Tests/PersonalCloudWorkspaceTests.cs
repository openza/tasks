using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudWorkspaceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-workspace-tests", Guid.NewGuid().ToString("N"));
    private const string Instance = "instance_workspace_test";
    private const string Device = "device_workspace_test";
    private const string Stamp = "2026-10-13T00:00:00.000Z";
    private SqliteTaskStore Store() { Directory.CreateDirectory(_directory); return new(Path.Combine(_directory, "tasks.db")); }

    internal static object Space(string id = "space_test", long version = 1, string name = "Example") => new
    {
        id, name, color = (string?)null, icon = "folder", sortOrder = 2, isArchived = false,
        createdAt = Stamp, updatedAt = Stamp, deletedAt = (string?)null, entityVersion = version,
    };
    internal static object Project(long version = 1, string? deletedAt = null) => new
    {
        id = "project_test", spaceId = "space_test", name = "Project", description = "Description", color = "#123456", icon = "folder",
        parentId = (string?)null, sortOrder = 4, status = "active", isFavorite = true, createdAt = Stamp, updatedAt = Stamp, deletedAt, entityVersion = version,
    };
    internal static object TaskRecord(string id = "task_test", long version = 1, string? projectId = "project_test") => new
    {
        id, spaceId = "space_test", projectId, parentId = (string?)null, title = "Example", completionState = "open", workflowStatus = "none", priority = 3,
        plannedOn = (string?)null, plannedAt = Stamp, deadlineOn = (string?)null, deadlineAt = Stamp, scheduledStart = Stamp, scheduledEnd = Stamp,
        durationMinutes = 30, recurrenceRule = "weekly", notes = "Notes", completedAt = (string?)null, createdAt = Stamp, entityVersion = version,
        deletedAt = (string?)null, labels = new[] { new { id = "label_test", name = "Example label" } }, source = (object?)null, sourceOwnerDeviceId = (string?)null,
    };
    internal static string Page(long seq, object[]? spaces = null, object[]? projects = null, object[]? tasks = null, object[]? catalog = null, object[]? intents = null, object[]? routes = null, object[]? scopes = null)
    {
        var payload = new Dictionary<string, object?> { ["spaces"] = spaces ?? [], ["projects"] = projects ?? [], ["tasks"] = tasks ?? [], ["catalog"] = catalog ?? [], ["intents"] = intents ?? [], ["routes"] = routes ?? [] };
        if (scopes is not null) payload["scopes"] = scopes;
        return PersonalCloudK0Codec.Serialize(new {
        protocolVersion = 1, product = "tasks", nextCheckpoint = seq,
        changes = new[] { new { protocolVersion = 1, product = "tasks", changeSeq = seq, mutationId = $"mutation_workspace_{seq}", operation = "workspace.changed", entityKind = "workspace",
            entityId = "entity_workspace_test", entityVersion = seq, serverAcceptedAt = Stamp,
            payload } },
        });
    }

    [Fact]
    public async Task Complete_workspace_applies_metadata_and_checkpoint_in_one_transaction()
    {
        var store = Store(); await store.InitializeAsync();
        var page = Page(1, [Space()], [Project()], [TaskRecord()]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyPersonalCloudChangePageAsync(Instance, Device, page, failAfterTaskWrite: true));
        Assert.Null(await store.GetTaskAsync("task_test"));
        Assert.Equal(0, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
        Assert.Empty(await store.GetPersonalCloudWorkspaceRecordsAsync(Instance, "spaces"));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, page);
        var task = (await store.GetTaskAsync("task_test"))!;
        Assert.Equal(DateTimeOffset.Parse(Stamp), task.DeadlineAt); Assert.Equal(DateTimeOffset.Parse(Stamp), task.ScheduledStart);
        Assert.Equal(30, task.DurationMinutes); Assert.Equal("weekly", task.RecurrenceRule); Assert.Single(task.Labels);
        var project = Assert.Single(await store.GetProjectsAsync("space_test"));
        Assert.Equal("Description", project.Description); Assert.True(project.IsFavorite); Assert.Equal("folder", project.Icon);
        Assert.Equal(1, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, page);
        Assert.Single(await store.GetPersonalCloudWorkspaceRecordsAsync(Instance, "tasks"));
    }

    [Fact]
    public async Task Invalid_complete_task_cannot_commit_earlier_reference_arrays()
    {
        var store = Store(); await store.InitializeAsync();
        var json = Page(1, [Space()], [Project()], [TaskRecord()]).Replace("\"durationMinutes\": 30", "\"durationMinutes\": -1", StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.ApplyPersonalCloudChangePageAsync(Instance, Device, json));
        Assert.Equal("invalid_request", error.Code); Assert.Empty(await store.GetPersonalCloudWorkspaceRecordsAsync(Instance, "spaces"));
        Assert.Equal(0, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
    }

    [Fact]
    public async Task Reference_tombstone_survives_stale_workspace_record_and_new_seed()
    {
        var store = Store(); await store.InitializeAsync(); await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(1, [Space()], [Project()], [TaskRecord()]));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(2, projects: [Project(2, Stamp)], tasks: [TaskRecord(version: 2, projectId: null)]));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(3, projects: [Project()]));
        Assert.Empty(await store.GetProjectsAsync("space_test", includeArchived: true));
        Assert.Null((await store.GetTaskAsync("task_test"))!.ProjectId);
        await store.StageUnpublishedTasksForPersonalCloudAsync(Instance, Device);
        Assert.DoesNotContain(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device), e => e.EntityId == "project_test");
    }

    [Fact]
    public async Task Empty_reference_and_rich_seed_are_immutable_and_idempotent()
    {
        var store = Store(); await store.InitializeAsync(); await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        await store.UpsertSpaceAsync(new() { Id = "space_empty", Name = "Empty", Icon = "folder", SortOrder = 8 });
        await store.UpsertProjectAsync(new() { Id = "project_empty", SpaceId = "space_empty", Name = "Empty project", Description = "Description", Icon = "folder" });
        await store.UpsertTaskAsync(new() { Id = "task_seed", Title = "Seed", SpaceId = "space_empty", ProjectId = "project_empty", DeadlineAt = DateTimeOffset.Parse(Stamp), ScheduledStart = DateTimeOffset.Parse(Stamp), RecurrenceRule = "weekly" });
        Assert.True(await store.StageUnpublishedTasksForPersonalCloudAsync(Instance, Device) > 0);
        var before = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        Assert.Equal(0, await store.StageUnpublishedTasksForPersonalCloudAsync(Instance, Device));
        Assert.Equal(before.Select(e => e.CanonicalJson), (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Select(e => e.CanonicalJson));
        var rich = Assert.Single(before, e => e.Operation == "task.createRich");
        using var document = JsonDocument.Parse(rich.CanonicalJson); var task = document.RootElement.GetProperty("payload").GetProperty("task");
        Assert.Equal(Stamp, task.GetProperty("deadlineAt").GetString()); Assert.Equal(Stamp, task.GetProperty("scheduledStart").GetString());
        Assert.Equal("weekly", task.GetProperty("recurrenceRule").GetString());
        Assert.Contains(before, e => e.Operation == "project.put" && e.EntityId == "project_empty");
        Assert.Contains(before, e => e.Operation == "space.put" && e.EntityId == "space_empty");
    }

    [Fact]
    public async Task Upgrade_separates_provider_recurrence_once_and_preserves_later_local_overlay()
    {
        var store = Store(); await store.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync(); var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM personal_cloud_workspace_local_migration; PRAGMA user_version=10;"; await command.ExecuteNonQueryAsync();
        }
        await store.UpsertTaskAsync(new() { Id = "task_source", Title = "Source", SourceIntegrationId = IntegrationIds.Todoist, SourceExternalId = "private-example", RecurrenceRule = "daily" });
        await store.InitializeAsync();
        var task = (await store.GetTaskAsync("task_source"))!;
        Assert.Null(task.RecurrenceRule); Assert.Equal("daily", task.SourceRecurrenceRule);
        await store.UpsertTaskAsync(task with { RecurrenceRule = "weekly" }); await store.InitializeAsync();
        Assert.Equal("weekly", (await store.GetTaskAsync(task.Id))!.RecurrenceRule);
    }

    [Theory]
    [InlineData("[]")] [InlineData("null")] [InlineData("legacy text")]
    public async Task Upgrade_preserves_non_object_legacy_metadata_while_moving_provider_recurrence(string legacyMetadata)
    {
        var store = Store(); await store.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync(); var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM personal_cloud_workspace_local_migration; PRAGMA user_version=10;"; await command.ExecuteNonQueryAsync();
        }
        await store.UpsertTaskAsync(new() { Id = "task_legacy_metadata", Title = "Example", SourceIntegrationId = IntegrationIds.Todoist, RecurrenceRule = "daily", SourceMetadataJson = legacyMetadata });
        await store.InitializeAsync(); var task = (await store.GetTaskAsync("task_legacy_metadata"))!;
        Assert.Null(task.RecurrenceRule); Assert.Equal("daily", task.SourceRecurrenceRule);
        using var metadata = JsonDocument.Parse(task.SourceMetadataJson!); var preserved = metadata.RootElement.GetProperty("legacySourceMetadata");
        Assert.Equal(legacyMetadata, preserved.ValueKind == JsonValueKind.String ? preserved.GetString() : preserved.GetRawText());
    }

    [Fact]
    public async Task Patch_metadata_is_atomic_and_date_alternatives_clear_without_provider_recurrence_changes()
    {
        var store = Store(); await store.InitializeAsync(); await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(1, [Space()], [Project()], [TaskRecord()]));
        var before = (await store.GetTaskAsync("task_test"))!;
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.PatchTaskWithPersonalCloudIntentAsync(before.Id,
            new Dictionary<string, object?> { ["deadlineOn"] = "2026-10-20", ["durationMinutes"] = -1 }, before.Revision, Instance, Device));
        Assert.Equal(before.DeadlineAt, (await store.GetTaskAsync(before.Id))!.DeadlineAt); Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await store.PatchTaskWithPersonalCloudIntentAsync(before.Id, new Dictionary<string, object?> { ["deadlineOn"] = "2026-10-20", ["scheduledEnd"] = "2026-10-13T01:00:00.000Z", ["durationMinutes"] = 60, ["recurrenceRule"] = "monthly" }, before.Revision, Instance, Device);
        var after = (await store.GetTaskAsync(before.Id))!;
        Assert.Null(after.DeadlineAt); Assert.Equal(new DateOnly(2026, 10, 20), after.DeadlineOn); Assert.Equal(60, after.DurationMinutes); Assert.Equal("monthly", after.RecurrenceRule);
        var entry = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)); Assert.Equal("task.patch", entry.Operation);
        using var document = JsonDocument.Parse(entry.CanonicalJson); Assert.Equal(4, document.RootElement.GetProperty("payload").GetProperty("fields").EnumerateObject().Count());
    }
    [Fact]
    public async Task Reference_edits_preserve_canonical_creation_time_and_nullable_color_and_reject_deleted_identity()
    {
        var store = Store(); await store.InitializeAsync(); await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(1, [Space()], [Project()]).Replace(".000Z", ".789Z", StringComparison.Ordinal));
        var space = (await store.GetSpacesAsync()).Single(s => s.Id == "space_test");
        await store.UpsertSpaceWithPersonalCloudIntentAsync(space with { Name = "Renamed", CreatedAt = DateTimeOffset.UtcNow }, Instance, Device);
        var entry = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        using var json = JsonDocument.Parse(entry.CanonicalJson); var facts = json.RootElement.GetProperty("payload").GetProperty("space");
        Assert.Equal(Stamp.Replace(".000Z", ".789Z", StringComparison.Ordinal), facts.GetProperty("createdAt").GetString()); Assert.Equal(JsonValueKind.Null, facts.GetProperty("color").ValueKind);
        var project = (await store.GetProjectsAsync()).Single(p => p.Id == "project_test");
        await store.UpsertProjectWithPersonalCloudIntentAsync(project with { Name = "Renamed project", CreatedAt = DateTimeOffset.UtcNow }, Instance, Device);
        using var projectJson = JsonDocument.Parse((await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Single(e => e.Operation == "project.put").CanonicalJson);
        Assert.Equal(Stamp.Replace(".000Z", ".789Z", StringComparison.Ordinal), projectJson.RootElement.GetProperty("payload").GetProperty("project").GetProperty("createdAt").GetString());
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(2, projects: [Project(2, Stamp)]));
        var rejected = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.UpsertProjectWithPersonalCloudIntentAsync(new() { Id = "project_test", SpaceId = "space_test", Name = "Stale" }, Instance, Device));
        Assert.Equal("invalid_transition", rejected.Code); Assert.DoesNotContain(await store.GetProjectsAsync(includeArchived: true), p => p.Id == "project_test");
    }
    [Fact]
    public async Task Rich_creation_rejects_invalid_metadata_and_existing_identity_without_overwriting_task()
    {
        var store = Store(); await store.InitializeAsync(); await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        var invalid = new TaskItem { Id = "task_invalid", Title = "Example", DurationMinutes = -1 };
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.CreateRichTaskWithPersonalCloudIntentAsync(invalid, Instance, Device));
        Assert.Null(await store.GetTaskAsync(invalid.Id)); Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await store.UpsertTaskAsync(invalid with { DurationMinutes = null, Title = "Preserved" });
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.CreateRichTaskWithPersonalCloudIntentAsync(invalid with { DurationMinutes = null }, Instance, Device));
        Assert.Equal("entity_exists", error.Code); Assert.Equal("Preserved", (await store.GetTaskAsync(invalid.Id))!.Title);
    }

    private static object With(object value, params (string Name, object? Value)[] fields)
    {
        using var document = JsonDocument.Parse(PersonalCloudK0Codec.Serialize(value));
        var result = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        foreach (var field in fields) result[field.Name] = field.Value;
        return result;
    }

    private static async Task AckAsync(SqliteTaskStore store, PersonalCloudK1OutboxEntry entry)
    {
        using var document = JsonDocument.Parse(entry.CanonicalJson);
        var basis = document.RootElement.GetProperty("baseEntityVersion");
        await store.AcknowledgePersonalCloudMutationAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new K0MutationReceipt {
            ProtocolVersion = 1, Product = "tasks", MutationId = entry.MutationId, Outcome = "accepted", EntityId = entry.EntityId,
            EntityVersion = basis.ValueKind == JsonValueKind.Null ? 1 : basis.GetInt64() + 1, ChangeSeq = entry.DeviceSequence, ServerAcceptedAt = Stamp }));
    }

    [Fact]
    public async Task Project_delete_predicts_task_and_detached_child_versions_across_restart_and_ack_without_echo()
    {
        var store = Store(); await store.InitializeAsync(); await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        var child = With(Project(), ("id", "project_child"), ("parentId", "project_test"));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(1, [Space()], [Project(), child], [TaskRecord()]));
        await store.DeleteProjectWithPersonalCloudIntentAsync("project_test", Instance, Device);
        var deletion = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        // An older complete echo must not overwrite indirect optimistic changes.
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(2, tasks: [TaskRecord(version: 2)]));
        Assert.Null((await store.GetTaskAsync("task_test"))!.ProjectId);
        await AckAsync(store, deletion);
        store = Store(); await store.InitializeAsync();
        Assert.Equal(2, await store.GetPersonalCloudEntityVersionAsync(Instance, "task_test"));
        var task = (await store.GetTaskAsync("task_test"))!;
        var patch = await store.PatchTaskWithPersonalCloudIntentAsync(task.Id, new Dictionary<string, object?> { ["notes"] = "After delete" }, task.Revision, Instance, Device);
        using var taskEnvelope = JsonDocument.Parse(patch.CanonicalJson);
        Assert.Equal(2, taskEnvelope.RootElement.GetProperty("baseEntityVersion").GetInt64());
        var project = (await store.GetProjectsAsync()).Single(p => p.Id == "project_child");
        Assert.Null(project.ParentId);
        await store.UpsertProjectWithPersonalCloudIntentAsync(project with { Name = "Detached child" }, Instance, Device);
        var update = (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Single(e => e.EntityId == project.Id);
        using var projectEnvelope = JsonDocument.Parse(update.CanonicalJson);
        Assert.Equal(2, projectEnvelope.RootElement.GetProperty("baseEntityVersion").GetInt64());
    }

    [Fact]
    public async Task Tree_move_predicts_all_descendants_and_preserves_their_overlay_until_ack()
    {
        var store = Store(); await store.InitializeAsync(); await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        var child = With(TaskRecord("task_child"), ("parentId", "task_test"));
        var leaf = With(TaskRecord("task_leaf"), ("parentId", "task_child"));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(1, [Space(), Space("space_destination", name: "Destination")], [Project()], [TaskRecord(), child, leaf]));
        var move = Assert.Single(await store.MoveTaskTreeToSpaceWithPersonalCloudIntentAsync("task_test", "space_destination", "Destination", new Dictionary<string, long> { ["task_test"] = 1 }, Instance, Device));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(2, tasks: [With(TaskRecord("task_leaf", 2), ("parentId", "task_child"))]));
        Assert.Equal("space_destination", (await store.GetTaskAsync("task_leaf"))!.SpaceId);
        await AckAsync(store, move);
        store = Store(); await store.InitializeAsync();
        foreach (var id in new[] { "task_child", "task_leaf" })
        {
            Assert.Equal(2, await store.GetPersonalCloudEntityVersionAsync(Instance, id));
            var task = (await store.GetTaskAsync(id))!;
            var patch = await store.PatchTaskWithPersonalCloudIntentAsync(id, new Dictionary<string, object?> { ["notes"] = "After move" }, task.Revision, Instance, Device);
            using var document = JsonDocument.Parse(patch.CanonicalJson);
            Assert.Equal(2, document.RootElement.GetProperty("baseEntityVersion").GetInt64());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootstrap_retains_local_metadata_and_links_but_keeps_raw_source_identity_only_for_matching_binding(bool changedBinding)
    {
        var store = Store(); await store.InitializeAsync(); await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        var sourceTask = new TaskItem { Id = "task_test", Title = "Example", SourceIntegrationId = IntegrationIds.Todoist,
            SourceConnectionId = "todoist_default", SourceExternalId = "source_private_example", SourceProviderTaskId = "task_private_example" };
        var source = PersonalCloudK0Mapper.CreateSafeSourceDescriptor(sourceTask)!;
        var remote = With(TaskRecord(), ("source", source), ("sourceOwnerDeviceId", Device));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(1, [Space()], [Project()], [remote, TaskRecord("task_removed")]));
        var task = (await store.GetTaskAsync("task_test"))!;
        await store.UpsertTaskAsync(task with { SourceExternalId = sourceTask.SourceExternalId, SourceConnectionId = sourceTask.SourceConnectionId,
            SourceProviderTaskId = sourceTask.SourceProviderTaskId, LocalMetadataJson = "{\"example\":true}" });
        await store.UpsertTaskExternalLinkAsync(new() { Id = "link_local_example", TaskId = task.Id, IntegrationId = IntegrationIds.Todoist, ExternalId = "linked_private_example" });
        await store.UpsertTaskAsync(new() { Id = "task_unpublished", Title = "Unpublished" });
        var bootstrapSource = changedBinding ? PersonalCloudK0Mapper.CreateSafeSourceDescriptor(sourceTask with { SourceExternalId = "other_private_example" })! : source;
        var bootstrap = PersonalCloudK0Codec.Serialize(new { protocolVersion = 1, product = "tasks", checkpoint = 4, snapshot = new {
            spaces = new[] { Space() }, projects = new[] { Project() }, tasks = new[] { With(remote, ("source", bootstrapSource), ("entityVersion", 2)) },
            labels = Array.Empty<object>(), taskLabels = Array.Empty<object>(), tombstones = Array.Empty<object>(), sourceDescriptors = Array.Empty<object>(),
            externalCommands = Array.Empty<object>(), scopes = Array.Empty<object>(), catalog = Array.Empty<object>(), intents = Array.Empty<object>(), routes = Array.Empty<object>() } });
        await store.ReplacePersonalCloudWorkspaceFromBootstrapAsync(Instance, Device, bootstrap);
        store = Store(); await store.InitializeAsync(); task = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal("{\"example\":true}", task.LocalMetadataJson);
        Assert.Single(await store.GetTaskExternalLinksAsync(task.Id));
        Assert.Equal(changedBinding ? null : sourceTask.SourceExternalId, task.SourceExternalId);
        Assert.Equal(changedBinding ? null : sourceTask.SourceConnectionId, task.SourceConnectionId);
        Assert.Equal(changedBinding ? null : sourceTask.SourceProviderTaskId, task.SourceProviderTaskId);
        Assert.Null(await store.GetTaskAsync("task_removed")); Assert.NotNull(await store.GetTaskAsync("task_unpublished"));
        Assert.Equal(4, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
    }

    [Theory]
    [InlineData("planned", false, false)]
    [InlineData("planned", false, true)]
    [InlineData("planned", true, false)]
    [InlineData("planned", true, true)]
    [InlineData("deadline", false, false)]
    [InlineData("deadline", false, true)]
    [InlineData("deadline", true, false)]
    [InlineData("deadline", true, true)]
    public async Task Paired_date_patch_is_order_independent(string prefix, bool moment, bool reverse)
    {
        var store = Store(); await store.InitializeAsync(); await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(1, [Space()], [Project()], [TaskRecord()]));
        var fields = new Dictionary<string, object?>();
        var pairs = new[] { (prefix + "On", moment ? null : "2026-10-20"), (prefix + "At", moment ? Stamp : null) };
        foreach (var (key, value) in reverse ? pairs.Reverse() : pairs) fields[key] = value;
        var task = (await store.GetTaskAsync("task_test"))!;
        await store.PatchTaskWithPersonalCloudIntentAsync(task.Id, fields, task.Revision, Instance, Device);
        task = (await store.GetTaskAsync(task.Id))!;
        // SQLite also stores the display day derived from an exact moment.
        Assert.Equal(moment ? TaskDateValues.FromDateTimeOffset(DateTimeOffset.Parse(Stamp)) : new DateOnly(2026, 10, 20), prefix == "planned" ? task.PlannedOn : task.DeadlineOn);
        Assert.Equal(moment ? DateTimeOffset.Parse(Stamp) : null, prefix == "planned" ? task.PlannedAt : task.DeadlineAt);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
