using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.ViewModels.Editing;
using Openza.Tasks.Desktop.ViewModels;

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

    [Theory]
    [InlineData(IntegrationIds.Local)]
    [InlineData(IntegrationIds.Todoist)]
    public async Task Unknown_cloud_label_id_survives_same_name_local_collision_and_chip_removal(string existingIntegration)
    {
        var store = await StoreAsync();
        await SeedLabelAsync(store, "local_release", existingIntegration);
        var task = await HydrateAsync(store);
        var updated = await new TaskApplicationService(store, store.PersonalCloudConnection).UpdateTaskAsync(
            new() { TaskId = task.Id, ExpectedRevision = task.Revision, Labels = OptionalValue<IReadOnlyList<string>>.Set([]) });
        Assert.Empty(updated.Labels);
        var intent = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal("task.removeLabel", intent.Operation);
        using var json = JsonDocument.Parse(intent.CanonicalJson);
        Assert.Equal("label_test", json.RootElement.GetProperty("payload").GetProperty("labelId").GetString());
        Assert.Equal("label_test", Assert.Single(task.Labels).Id);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ambiguous_update_names_are_rejected_without_removing_other_labels(bool bothMatchesAttached)
    {
        var store = await StoreAsync();
        await SeedLabelAsync(store, "label_test", IntegrationIds.Local);
        await SeedLabelAsync(store, "other_release", IntegrationIds.Local);
        var task = await HydrateAsync(store, bothMatchesAttached
            ? [("other_label", "Other"), ("other_release", "Example label")]
            : [("other_label", "Other")]);
        var error = await Assert.ThrowsAsync<ReferenceResolutionException>(() =>
            new TaskApplicationService(store, store.PersonalCloudConnection).UpdateTaskAsync(new()
            {
                TaskId = task.Id, ExpectedRevision = task.Revision,
                Labels = OptionalValue<IReadOnlyList<string>>.Set(["example label"])
            }));
        Assert.Contains("Use its exact id", error.Message);
        Assert.Equal(task.Labels.Select(label => label.Id).Order(), (await store.GetTaskAsync(task.Id))!.Labels.Select(label => label.Id).Order());
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public async Task Exact_update_ids_select_between_same_name_labels_and_preserve_removal_intent_ids()
    {
        var store = await StoreAsync();
        await SeedLabelAsync(store, "label_test", IntegrationIds.Local);
        await SeedLabelAsync(store, "other_release", IntegrationIds.Local);
        var task = await HydrateAsync(store, ("other_release", "Example label"));
        var updated = await new TaskApplicationService(store, store.PersonalCloudConnection).UpdateTaskAsync(new()
        {
            TaskId = task.Id, ExpectedRevision = task.Revision,
            Labels = OptionalValue<IReadOnlyList<string>>.Set(["label_test"])
        });
        Assert.Equal("label_test", Assert.Single(updated.Labels).Id);
        var intent = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        using var json = JsonDocument.Parse(intent.CanonicalJson);
        Assert.Equal("other_release", json.RootElement.GetProperty("payload").GetProperty("labelId").GetString());
    }

    [Fact]
    public async Task Desktop_retains_both_same_name_chips_by_id()
    {
        var store = await StoreAsync();
        await SeedLabelAsync(store, "label_test", IntegrationIds.Local);
        await SeedLabelAsync(store, "other_release", IntegrationIds.Local);
        var task = await HydrateAsync(store, ("other_release", "Example label"));
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore());
        var resolve = typeof(MainWindowViewModel).GetMethod("ResolveLabels", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var labels = Assert.IsAssignableFrom<IReadOnlyList<LabelItem>>(resolve.Invoke(vm, [new[] { "Example label" }, task.Labels]));
        Assert.Equal(task.Labels.Select(label => label.Id).Order(), labels.Select(label => label.Id).Order());
        var updated = await AddChipAsync(store, task);
        Assert.Equal(3, updated.Labels.Count);
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

    private static async Task<TaskItem> HydrateAsync(SqliteTaskStore store, params (string Id, string Name)[] extraLabels)
    {
        var record = JsonSerializer.SerializeToNode(PersonalCloudWorkspaceTests.TaskRecord())!.AsObject();
        record["labels"] = JsonSerializer.SerializeToNode(new[] { new { id = "label_test", name = "Example label" } }
            .Concat(extraLabels.Select(label => new { id = label.Id, name = label.Name })));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device,
            PersonalCloudWorkspaceTests.Page(1, [PersonalCloudWorkspaceTests.Space()], [PersonalCloudWorkspaceTests.Project()], [record]));
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
