using System.Text.Json;
using Microsoft.Data.Sqlite;
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
        var task = await AdoptAsync(store, Source("Release", "Planning"));
        var outbox = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        if (workspace)
        {
            using var json = JsonDocument.Parse(Assert.Single(outbox, item => item.EntityId == task.Id && item.Operation == "task.createRich").CanonicalJson);
            Assert.Equal(2, json.RootElement.GetProperty("payload").GetProperty("task").GetProperty("labels").GetArrayLength());
        }
        else
        {
            var adds = outbox.Where(item => item.EntityId == task.Id && item.Operation == "task.addLabel").ToList();
            Assert.Equal(2, adds.Count);
            Assert.Equal(new long[] { 1, 2 }, adds.Select(item => JsonDocument.Parse(item.CanonicalJson).RootElement.GetProperty("baseEntityVersion").GetInt64()));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Backfill_unions_once_and_emits_per_label_intents(bool workspace)
    {
        var store = await StoreAsync();
        var source = Source("Release");
        var task = await AdoptAsync(store, source);
        await store.SetTaskLabelsAsync(task.Id, [new LabelItem { Id = "user_label", Name = "Mine" }]);
        await RemoveMarkerAsync(store, task.Id);
        await CloudAsync(store, workspace);
        await store.StageUnpublishedTasksForPersonalCloudAsync(Instance, Device);
        var before = (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Count;

        await store.UpsertProviderSourceItemAsync(source);
        var backfilled = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(new[] { "Mine", "Release" }, backfilled.Labels.Select(label => label.Name).Order().ToArray());
        Assert.True(Applied(backfilled));
        var added = Assert.Single((await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Skip(before));
        Assert.Equal("task.addLabel", added.Operation);

        await store.SetTaskLabelsAsync(task.Id, backfilled.Labels.Where(label => label.Name == "Mine").ToArray());
        await store.UpsertProviderSourceItemAsync(Source("Release", "New category"));
        var refreshed = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal("Mine", Assert.Single(refreshed.Labels).Name);
        Assert.True(Applied(refreshed));
        Assert.Equal(before + 1, (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Count);
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
