using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudLifecycleIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Category_batch_rechecks_connection_after_repair_and_publishes_later_task_labels(bool published)
    {
        using var f = await Fixture.CreateAsync();
        var original = f.Service.CurrentConnection!;
        var sources = new List<ProviderSourceItem>();
        var parents = new List<TaskItem>();
        for (var index = 0; index < 2; index++)
        {
            var source = new ProviderSourceItem { Id = $"repair_category_{index}", ExternalId = $"repair_category_{index}",
                IntegrationId = IntegrationIds.MicrosoftToDo, ProviderConnectionId = "mstodo_default", Title = $"Parent {index}", SnapshotJson = "{}" };
            await f.Tasks.UpsertProviderSourceItemAsync(source);
            parents.Add((await f.Tasks.AdoptProviderSourceItemAsync(source.Id))!);
            sources.Add(source with { SnapshotJson = """{"msToDo":{"categories":[{"name":"Release"}]}}""" });
        }
        if (published)
        {
            await using var connection = new SqliteConnection($"Data Source={f.Tasks.DatabasePath};Pooling=False");
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO personal_cloud_entity_versions(instance_id,product,entity_id,entity_version,updated_at) VALUES(@i,'tasks',@t,1,0); INSERT INTO personal_cloud_replica_entities(instance_id,product,entity_id) VALUES(@i,'tasks',@t)";
            command.Parameters.AddWithValue("@i", original.InstanceId);
            command.Parameters.AddWithValue("@t", parents[1].Id);
            await command.ExecuteNonQueryAsync();
        }
        await f.Service.DisconnectAsync();
        f.Tasks.PersonalCloudConnection = () => f.Service.CurrentConnection;
        {
            await f.Tasks.UpsertProviderSourceItemAsync(sources[0]);
            Assert.Empty(await f.Tasks.GetPendingPersonalCloudOutboxAsync(original.InstanceId, original.DeviceId));
            await f.Service.PairExistingAsync(Fixture.Origin, "synthetic-pair-code-1234567890", "Paired again", false);
            Assert.NotNull(f.Service.CurrentConnection);
            await f.Tasks.UpsertProviderSourceItemAsync(sources[1]);
        }
        await f.Tasks.ApplyPendingMicrosoftCategoriesAfterPullAsync(f.Service.CurrentConnection!);
        await f.Tasks.StageUnpublishedTasksForPersonalCloudAsync(original.InstanceId, original.DeviceId);
        var outbox = await f.Tasks.GetPendingPersonalCloudOutboxAsync(original.InstanceId, original.DeviceId);
        Assert.Single(outbox, entry => entry.EntityId == parents[1].Id && entry.Operation == "task.addLabel");
        Assert.Equal(2L, await f.Tasks.GetPersonalCloudEntityVersionAsync(original.InstanceId, parents[1].Id));
        Assert.Equal("Release", Assert.Single((await f.Tasks.GetTaskAsync(parents[1].Id))!.Labels).Name);
        await f.Tasks.UpsertProviderSourceItemAsync(sources[1]);
        Assert.Equal(outbox.Count, (await f.Tasks.GetPendingPersonalCloudOutboxAsync(original.InstanceId, original.DeviceId)).Count);
    }
}
