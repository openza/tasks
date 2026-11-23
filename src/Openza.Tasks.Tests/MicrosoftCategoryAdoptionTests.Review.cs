using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed partial class MicrosoftCategoryAdoptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Category_backfill_keeps_the_attached_label_when_another_id_has_the_same_name(bool providerAttachment)
    {
        var store = await StoreAsync();
        var task = await AdoptAsync(store, Source() with { SnapshotJson = "{}" });
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO labels(id,integration_id,name,color,created_at) VALUES
                  ('label_first','openza_tasks','Release','#123456',0),
                  ('label_second',@integration,'Release','#123456',0);
                """;
            command.Parameters.AddWithValue("@integration", providerAttachment ? IntegrationIds.Todoist : IntegrationIds.Local);
            await command.ExecuteNonQueryAsync();
        }
        await store.SetTaskLabelsAsync(task.Id, [new() { Id = "label_second", Name = "Release" }]);
        await store.UpsertProviderSourceItemAsync(Source(" release ", "RELEASE"));
        var updated = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal("label_second", Assert.Single(updated.Labels).Id);
        Assert.True(Applied(updated));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public async Task Workspace_catalog_label_collision_keeps_the_cloud_id_when_removing_the_chip()
    {
        var store = await StoreAsync();
        await store.UpsertLabelAsync(new() { Id = "label_test", Name = "Example label", IntegrationId = IntegrationIds.Todoist });
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device,
            PersonalCloudWorkspaceTests.Page(1, [PersonalCloudWorkspaceTests.Space()], [PersonalCloudWorkspaceTests.Project()], [PersonalCloudWorkspaceTests.TaskRecord()]));
        Assert.Equal("label_test", Assert.Single((await store.GetTaskAsync("task_test"))!.Labels).Id);
        await CloudAsync(store, true);
        await new TaskApplicationService(store, store.PersonalCloudConnection).UpdateTaskAsync(new()
        {
            TaskId = "task_test", Labels = OptionalValue<IReadOnlyList<string>>.Set([]),
        });
        var intent = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal("task.removeLabel", intent.Operation);
        using var json = JsonDocument.Parse(intent.CanonicalJson);
        Assert.Equal("label_test", json.RootElement.GetProperty("payload").GetProperty("labelId").GetString());
    }

    [Fact]
    public async Task Category_backfill_never_exceeds_the_workspace_label_limit()
    {
        var store = await StoreAsync();
        var task = await AdoptAsync(store, Source() with { SnapshotJson = "{}" });
        await store.SetTaskLabelsAsync(task.Id, Enumerable.Range(0, 32).Select(index => new LabelItem { Id = $"label_{index}", Name = $"Label {index}" }).ToArray());
        await store.UpsertProviderSourceItemAsync(Source("Release", "Planning"));
        var updated = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(32, updated.Labels.Count);
        Assert.True(Applied(updated));
        Assert.DoesNotContain(await store.GetLabelsAsync(), label => label.Name == "Release");
    }

    [Fact]
    public async Task Missing_source_relink_preserves_category_marker_and_user_label_removal()
    {
        var store = await StoreAsync();
        var source = Source("Release");
        var task = await AdoptAsync(store, source);
        await store.SetTaskLabelsAsync(task.Id, []);
        await store.DetachProviderSourceItemAsync(source.Id);
        Assert.True(Applied((await store.GetTaskAsync(task.Id))!));
        await store.UpsertProviderSourceItemAsync(source);
        Assert.Empty((await store.GetTaskAsync(task.Id))!.Labels);
        Assert.True(Applied((await store.GetTaskAsync(task.Id))!));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }
}
