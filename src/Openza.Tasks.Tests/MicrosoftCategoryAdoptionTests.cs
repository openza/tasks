using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class MicrosoftCategoryAdoptionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-microsoft-categories", Guid.NewGuid().ToString("N"));
    private const string Instance = "instance_00000000000000000000000000000001";
    private const string Device = "device_categories";

    [Fact]
    public async Task Adoption_creates_colored_local_labels_and_deduplicates_names()
    {
        var store = await StoreAsync();
        await store.UpsertLabelAsync(new LabelItem { Id = "provider_release", IntegrationId = IntegrationIds.MicrosoftToDo, Name = "Release", Color = "#000000" });
        var task = await AdoptAsync(store, Source(" Release ", "release", "Planning"));
        Assert.Equal(2, task.Labels.Count);
        Assert.All(task.Labels, label => Assert.Equal(IntegrationIds.Local, label.IntegrationId));
        Assert.All(task.Labels, label => Assert.Equal("#32CD32", label.Color));
        Assert.DoesNotContain(task.Labels, label => label.Id == "provider_release");
        Assert.Equal("Release", task.Labels.Single(label => label.Name == "Release").Name);
        Assert.True(Applied(task));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public async Task Adoption_reuses_local_name_case_insensitively_without_changing_color()
    {
        var store = await StoreAsync();
        await store.UpsertLabelAsync(new LabelItem { Id = "local_release", Name = " release ", Color = "#123456" });
        var task = await AdoptAsync(store, Source("RELEASE"));
        var label = Assert.Single(task.Labels);
        Assert.Equal("local_release", label.Id);
        Assert.Equal("#123456", label.Color);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Adoption_publishes_labels_to_workspace_and_legacy_cloud(bool workspace)
    {
        var store = await StoreAsync();
        await CloudAsync(store, workspace);
        Assert.Equal(workspace, await store.SupportsPersonalCloudWorkspaceAsync(Instance));
        var task = await AdoptAsync(store, Source("Release", "Planning"));
        var outbox = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        if (workspace)
        {
            using var json = JsonDocument.Parse(Assert.Single(outbox, item => item.EntityId == task.Id && item.Operation == "task.createRich").CanonicalJson);
            Assert.Equal(2, json.RootElement.GetProperty("payload").GetProperty("task").GetProperty("labels").GetArrayLength());
        }
        else
        {
            Assert.Contains(outbox, item => item.EntityId == task.Id && item.Operation == "task.create");
            Assert.DoesNotContain(outbox, item => item.Operation == "task.createRich");
            var adds = outbox.Where(item => item.EntityId == task.Id && item.Operation == "task.addLabel").ToList();
            Assert.Equal(2, adds.Count);
            Assert.Equal(new long[] { 1, 2 }, adds.Select(item => JsonDocument.Parse(item.CanonicalJson).RootElement.GetProperty("baseEntityVersion").GetInt64()));
        }
        var version = (await store.GetPersonalCloudEntityVersionAsync(Instance, task.Id))!.Value;
        Assert.Equal(workspace ? 1L : 3L, version);
        var service = new TaskApplicationService(store, store.PersonalCloudConnection);
        var edited = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = task.Id, ExpectedRevision = task.Revision,
            Labels = OptionalValue<IReadOnlyList<string>>.Set(task.Labels.Select(label => label.Id).Append("After categories").ToArray()),
        });
        var userIntent = (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Last();
        Assert.Equal("task.addLabel", userIntent.Operation);
        Assert.Equal(version, BaseVersion(userIntent));
        Assert.Equal(version + 1, await store.GetPersonalCloudEntityVersionAsync(Instance, task.Id));
        Assert.Equal(3, edited.Labels.Count);
        Assert.Empty(await store.GetPersonalCloudTaskConflictsAsync(Instance));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Backfill_unions_once_and_emits_per_label_intents(bool workspace)
    {
        var store = await StoreAsync();
        var source = Source("Release", "Planning");
        var task = await AdoptAsync(store, source);
        await store.SetTaskLabelsAsync(task.Id, [new LabelItem { Id = "user_label", Name = "Mine" }]);
        await RemoveMarkerAsync(store, task.Id);
        await CloudAsync(store, workspace);
        Assert.Equal(workspace, await store.SupportsPersonalCloudWorkspaceAsync(Instance));
        await store.StageUnpublishedTasksForPersonalCloudAsync(Instance, Device);
        var before = (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Count;
        var version = (await store.GetPersonalCloudEntityVersionAsync(Instance, task.Id))!.Value;

        await store.UpsertProviderSourceItemAsync(source);
        var backfilled = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(new[] { "Mine", "Planning", "Release" }, backfilled.Labels.Select(label => label.Name).Order().ToArray());
        Assert.True(Applied(backfilled));
        var added = (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Skip(before).ToArray();
        Assert.Equal(2, added.Length);
        Assert.All(added, entry => Assert.Equal("task.addLabel", entry.Operation));
        Assert.Equal(new[] { version, version + 1 }, added.Select(BaseVersion));
        Assert.Equal(version + 2, await store.GetPersonalCloudEntityVersionAsync(Instance, task.Id));

        var service = new TaskApplicationService(store, store.PersonalCloudConnection);
        await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = task.Id, ExpectedRevision = backfilled.Revision,
            Labels = OptionalValue<IReadOnlyList<string>>.Set(["user_label"]),
        });
        var userIntents = (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Skip(before + 2).ToArray();
        Assert.Equal(2, userIntents.Length);
        Assert.All(userIntents, entry => Assert.Equal("task.removeLabel", entry.Operation));
        Assert.Equal(new[] { version + 2, version + 3 }, userIntents.Select(BaseVersion));
        Assert.Equal(version + 4, await store.GetPersonalCloudEntityVersionAsync(Instance, task.Id));
        Assert.Empty(await store.GetPersonalCloudTaskConflictsAsync(Instance));
        await store.UpsertProviderSourceItemAsync(Source("Release", "New category"));
        var refreshed = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal("Mine", Assert.Single(refreshed.Labels).Name);
        Assert.True(Applied(refreshed));
        Assert.Equal(before + 4, (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refresh_reads_only_an_unmarked_task_and_skips_hydration_after_marking(bool applied)
    {
        var store = await StoreAsync();
        var task = await AdoptAsync(store, Source("Release"));
        await store.UpsertTaskAsync(new TaskItem { Id = "unrelated", Title = "Unrelated" });
        if (!applied) await RemoveMarkerAsync(store, task.Id);
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        var corrupt = connection.CreateCommand();
        corrupt.CommandText = "UPDATE tasks SET created_at=9223372036854775807 WHERE id='unrelated' OR (id=@id AND @applied=1)";
        corrupt.Parameters.AddWithValue("@id", task.Id);
        corrupt.Parameters.AddWithValue("@applied", applied ? 1 : 0);
        await corrupt.ExecuteNonQueryAsync();

        await store.UpsertProviderSourceItemAsync(Source("Release", "Planning"));

        var read = connection.CreateCommand();
        read.CommandText = "SELECT json_extract(source_metadata, '$.msToDoCategoriesApplied'), (SELECT COUNT(*) FROM task_labels WHERE task_id=@id) FROM tasks WHERE id=@id";
        read.Parameters.AddWithValue("@id", task.Id);
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(applied ? 1 : 2, reader.GetInt32(1));
    }

    [Fact]
    public async Task Empty_categories_are_marked_and_not_applied_on_later_sync()
    {
        var store = await StoreAsync();
        var task = await AdoptAsync(store, Source());
        Assert.Empty(task.Labels);
        Assert.True(Applied(task));
        await store.UpsertProviderSourceItemAsync(Source("Later"));
        Assert.Empty((await store.GetTaskAsync(task.Id))!.Labels);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"msToDo\":{\"id\":\"old-task\",\"listId\":\"old-list\"}}")]
    public async Task Cached_snapshot_without_categories_waits_for_fresh_categories_before_marking(string? snapshot)
    {
        var store = await StoreAsync();
        var task = await AdoptAsync(store, Source() with { SnapshotJson = snapshot });
        Assert.Empty(task.Labels);
        using (var metadata = JsonDocument.Parse(task.SourceMetadataJson!))
            Assert.False(metadata.RootElement.TryGetProperty("msToDoCategoriesApplied", out _));

        await store.UpsertProviderSourceItemAsync(Source("Release"));
        var refreshed = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal("Release", Assert.Single(refreshed.Labels).Name);
        Assert.True(Applied(refreshed));
        await store.SetTaskLabelsAsync(task.Id, []);
        await store.UpsertProviderSourceItemAsync(Source("Release"));
        Assert.Empty((await store.GetTaskAsync(task.Id))!.Labels);
    }

    [Fact]
    public async Task Malformed_categories_roll_back_adoption()
    {
        var store = await StoreAsync();
        var source = Source() with { SnapshotJson = "{" };
        await store.UpsertProviderSourceItemAsync(source);
        await Assert.ThrowsAnyAsync<JsonException>(() => store.AdoptProviderSourceItemAsync("source_categories"));
        Assert.Empty(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
        Assert.Equal(ProviderSourceAdoptionStates.NotAdopted, Assert.Single(await store.GetProviderSourceItemsAsync()).AdoptionState);
    }

    private async Task<SqliteTaskStore> StoreAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        return store;
    }

    private static async Task CloudAsync(SqliteTaskStore store, bool workspace)
    {
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, workspace, workspace);
        store.PersonalCloudConnection = () => new PersonalCloudConnectionProfile
        {
            InstanceId = Instance, DeviceId = Device, ServiceOrigin = new Uri("https://categories.example.test"),
            ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow,
        };
    }

    private static ProviderSourceItem Source(params string[] names) => new()
    {
        Id = "source_categories", IntegrationId = IntegrationIds.MicrosoftToDo, ProviderConnectionId = "mstodo_default",
        ExternalId = "task_categories", ProviderTaskId = "list|task_categories", Title = "Review Microsoft category labels",
        SnapshotJson = JsonSerializer.Serialize(new { msToDo = new { categories = names.Select(name => new { name, color = "#32CD32" }) } }),
    };

    private static async Task<TaskItem> AdoptAsync(SqliteTaskStore store, ProviderSourceItem source)
    {
        await store.UpsertProviderSourceItemAsync(source);
        await store.AdoptProviderSourceItemAsync(source.Id);
        return Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
    }

    private static bool Applied(TaskItem task) => JsonDocument.Parse(task.SourceMetadataJson!).RootElement.GetProperty("msToDoCategoriesApplied").GetBoolean();

    private static long BaseVersion(PersonalCloudK1OutboxEntry entry)
    {
        using var json = JsonDocument.Parse(entry.CanonicalJson);
        return json.RootElement.GetProperty("baseEntityVersion").GetInt64();
    }

    private static async Task RemoveMarkerAsync(SqliteTaskStore store, string id)
    {
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE tasks SET source_metadata=json_remove(source_metadata,'$.msToDoCategoriesApplied') WHERE id=@id";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
