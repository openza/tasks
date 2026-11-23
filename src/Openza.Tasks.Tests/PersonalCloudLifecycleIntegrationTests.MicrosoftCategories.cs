using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudLifecycleIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnect_allows_local_category_adoption_but_defers_published_backfill(bool backfill)
    {
        using var f = await Fixture.CreateAsync();
        var source = new ProviderSourceItem
        {
            Id = "source_categories", IntegrationId = IntegrationIds.MicrosoftToDo, ProviderConnectionId = "mstodo_default",
            ExternalId = "task_categories", ProviderTaskId = "list|task_categories", Title = "Synthetic category task",
            SnapshotJson = """{"msToDo":{"categories":[]}}""",
        };
        await f.Tasks.UpsertProviderSourceItemAsync(source);
        if (backfill)
        {
            var task = (await f.Tasks.AdoptProviderSourceItemAsync(source.Id))!;
            await using var connection = new SqliteConnection($"Data Source={f.Tasks.DatabasePath};Pooling=False");
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE tasks SET source_metadata=json_remove(source_metadata,'$.msToDoCategoriesApplied') WHERE id=@id;
                INSERT INTO personal_cloud_entity_versions(instance_id,product,entity_id,entity_version,updated_at)
                VALUES(@instance,'tasks',@id,1,0);
                """;
            command.Parameters.AddWithValue("@id", task.Id);
            command.Parameters.AddWithValue("@instance", f.Service.CurrentConnection!.InstanceId);
            await command.ExecuteNonQueryAsync();
        }
        source = source with { SnapshotJson = """{"msToDo":{"categories":[{"name":"Release","color":"#32CD32"}]}}""" };
        if (!backfill) await f.Tasks.UpsertProviderSourceItemAsync(source);
        var cloud = f.Service.CurrentConnection!;
        f.Tasks.PersonalCloudConnection = () => f.Service.CurrentConnection;
        f.Http.BeforeDeviceRevoke = async () =>
        {
            Assert.NotNull(f.Service.CurrentConnection);
            if (!backfill)
            {
                Assert.NotNull(await f.Tasks.AdoptProviderSourceItemAsync(source.Id));
                return;
            }
            await f.Tasks.UpsertProviderSourceItemAsync(source);
        };

        await f.Service.DisconnectAsync();

        Assert.Null(f.Service.CurrentConnection);
        Assert.Empty(await f.Tasks.GetPendingPersonalCloudOutboxAsync(cloud.InstanceId, cloud.DeviceId));
        await f.Tasks.EnsureNoPendingPersonalCloudIntentsAsync();
        f.Http.BeforeDeviceRevoke = null;
        await f.Service.PairExistingAsync(Fixture.Origin, "synthetic-pair-code-1234567890", "Paired again", false);
        Assert.NotNull(f.Service.CurrentConnection);
    }
}
