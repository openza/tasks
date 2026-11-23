using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.ViewModels.Editing;

namespace Openza.Tasks.Tests;

public sealed class CloudLabelEditingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "openza-cloud-label-editing", Guid.NewGuid().ToString("N"));
    private const string Instance = "instance_label_editing";
    private const string Device = "device_label_editing";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adding_a_chip_preserves_an_attached_catalog_label_id(bool existingLocalName)
    {
        var store = await StoreAsync();
        await SeedLabelAsync(store, "label_test", IntegrationIds.Todoist);
        if (existingLocalName) await SeedLabelAsync(store, "local_release", IntegrationIds.Local);
        var task = await HydrateAsync(store);
        Assert.Equal("label_test", Assert.Single(task.Labels).Id);
        var updated = await AddChipAsync(store, task);
        Assert.Contains(updated.Labels, label => label.Id == "label_test");
        var intent = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal("task.addLabel", intent.Operation);
        using var json = JsonDocument.Parse(intent.CanonicalJson);
        Assert.Equal("New", json.RootElement.GetProperty("payload").GetProperty("label").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Unknown_cloud_label_ids_reuse_local_names_without_duplicate_edit_choices()
    {
        var store = await StoreAsync();
        await SeedLabelAsync(store, "local_release", IntegrationIds.Local);
        var task = await HydrateAsync(store);
        var updated = await AddChipAsync(store, task);
        Assert.Equal("local_release", Assert.Single(updated.Labels, label => label.Name == "Example label").Id);
        Assert.Single(await store.GetLabelsAsync(), label => label.Name == "Example label");
        Assert.Equal("task.addLabel", Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Operation);
    }

    [Fact]
    public async Task Existing_same_name_local_labels_do_not_make_retained_chips_ambiguous()
    {
        var store = await StoreAsync();
        await SeedLabelAsync(store, "label_test", IntegrationIds.Local);
        await SeedLabelAsync(store, "other_release", IntegrationIds.Local);
        var task = await HydrateAsync(store);
        var updated = await AddChipAsync(store, task);
        Assert.Contains(updated.Labels, label => label.Id == "label_test");
        Assert.Equal("task.addLabel", Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Operation);
    }

    private async Task<SqliteTaskStore> StoreAsync()
    {
        Directory.CreateDirectory(directory);
        var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
        await store.InitializeAsync();
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, false);
        store.PersonalCloudConnection = () => new() { InstanceId = Instance, DeviceId = Device,
            ServiceOrigin = new("https://labels.example.test"), ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow };
        return store;
    }

    private static async Task SeedLabelAsync(SqliteTaskStore store, string id, string integration)
    {
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO labels(id,integration_id,name,color,created_at) VALUES(@id,@integration,'Example label','#123456',0)";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@integration", integration);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<TaskItem> HydrateAsync(SqliteTaskStore store)
    {
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device,
            PersonalCloudWorkspaceTests.Page(1, [PersonalCloudWorkspaceTests.Space()], [PersonalCloudWorkspaceTests.Project()], [PersonalCloudWorkspaceTests.TaskRecord()]));
        return (await store.GetTaskAsync("task_test"))!;
    }

    private static Task<TaskItem> AddChipAsync(SqliteTaskStore store, TaskItem task)
    {
        var draft = new TaskDetailDraft(task.Id, task.Title, task.Notes ?? "", task.Status, task.Priority, task.ProjectId,
            TaskDetailEditPlanner.DisplayPlannedOn(task), TaskDetailEditPlanner.DisplayDeadlineOn(task),
            [.. task.Labels, new() { Id = "new_label", Name = "New" }], TaskSourceDateChoice.ReadAcknowledgement(task.LocalMetadataJson), 1);
        return new TaskApplicationService(store, store.PersonalCloudConnection).UpdateTaskAsync(TaskDetailEditPlanner.CreateRequest(task, task, draft)!);
    }

    public void Dispose() => TestDirectory.Delete(directory);
}
