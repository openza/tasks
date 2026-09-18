using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudK4RecoveryTests : IDisposable
{
    private const string InstanceId = "instance_00000000000000000000000000000001";
    private const string SourceDeviceId = "device_00000000000000000000000000000001";
    private const string ReplicaDeviceId = "device_k4_replica";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-personal-cloud-k4-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Committed_intent_survives_no_send_lost_response_and_restart_until_valid_receipt()
    {
        var store = CreateStore("source.db");
        await store.InitializeAsync();
        var outbox = await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem
        {
            Id = "local_k4_recovery",
            SpaceId = SpaceIds.Default,
            Title = "Recover one task",
            CreatedAt = DateTimeOffset.Parse("2026-09-13T06:30:00Z"),
        }, InstanceId, SourceDeviceId);

        var restarted = new SqliteTaskStore(store.DatabasePath);
        await restarted.InitializeAsync();
        Assert.Equal(outbox.MutationId, Assert.Single(
            await restarted.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId)).MutationId);

        var duplicateReceipt = PersonalCloudK0Codec.Serialize(new K0MutationReceipt
        {
            ProtocolVersion = 1,
            Product = "tasks",
            MutationId = outbox.MutationId,
            Outcome = "duplicate",
            EntityId = outbox.EntityId,
            EntityVersion = 1,
            ChangeSeq = 1,
            ServerAcceptedAt = "2026-09-13T06:30:01.000Z",
        });

        Assert.Single(await restarted.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId));
        await restarted.AcknowledgePersonalCloudMutationAsync(InstanceId, SourceDeviceId, duplicateReceipt);
        Assert.Empty(await restarted.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId));

        await restarted.AcknowledgePersonalCloudMutationAsync(InstanceId, SourceDeviceId, duplicateReceipt);
        Assert.Empty(await restarted.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId));
        Assert.Equal(1L, await CountRowsAsync(store.DatabasePath, "personal_cloud_outbox"));
        Assert.NotNull(await restarted.GetTaskAsync("local_k4_recovery"));
    }

    [Fact]
    public async Task Invalid_or_mismatched_receipt_is_typed_and_cannot_acknowledge_the_outbox()
    {
        var store = CreateStore("reject.db");
        await store.InitializeAsync();
        var outbox = await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem
        {
            Id = "local_k4_reject",
            SpaceId = SpaceIds.Default,
            Title = "Keep pending",
            CreatedAt = DateTimeOffset.Parse("2026-09-13T06:30:00Z"),
        }, InstanceId, SourceDeviceId);

        var exception = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            store.AcknowledgePersonalCloudMutationAsync(InstanceId, SourceDeviceId, "{not-json"));
        Assert.Equal("invalid_request", exception.Code);

        var mismatched = PersonalCloudK0Codec.Serialize(new K0MutationReceipt
        {
            ProtocolVersion = 1,
            Product = "tasks",
            MutationId = outbox.MutationId,
            Outcome = "accepted",
            EntityId = "local_another_task",
            EntityVersion = 1,
            ChangeSeq = 1,
            ServerAcceptedAt = "2026-09-13T06:30:01.000Z",
        });
        exception = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            store.AcknowledgePersonalCloudMutationAsync(InstanceId, SourceDeviceId, mismatched));
        Assert.Equal("invalid_request", exception.Code);
        Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId));
    }

    [Fact]
    public async Task Replica_commit_survives_restart_before_checkpoint_acknowledgement_is_created()
    {
        var replica = CreateStore("replica.db");
        await replica.InitializeAsync();
        var page = ReadFixture("pulled-change-page.json");
        Assert.Equal(1, await replica.ApplyPersonalCloudChangePageAsync(InstanceId, ReplicaDeviceId, page));

        var restarted = new SqliteTaskStore(replica.DatabasePath);
        await restarted.InitializeAsync();
        Assert.NotNull(await restarted.GetTaskAsync("local_00000000000000000000000000000001"));
        var checkpoint = await restarted.GetPersonalCloudCheckpointAsync(InstanceId, ReplicaDeviceId);
        Assert.Equal(1, checkpoint);

        var acknowledgement = PersonalCloudK4Codec.CreateCheckpointAcknowledgement(
            InstanceId,
            ReplicaDeviceId,
            checkpoint);
        var expected = PersonalCloudK0Codec.Canonicalize(ReadFixture("checkpoint-acknowledgement.json"));
        Assert.Equal(expected, PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(acknowledgement)));

        var response = PersonalCloudK4Codec.ParseCheckpointReceipt("""
            {
              "protocolVersion": 1,
              "product": "tasks",
              "deviceId": "device_k4_replica",
              "acknowledgedCheckpoint": 1
            }
            """, ReplicaDeviceId);
        Assert.Equal(checkpoint, response.AcknowledgedCheckpoint);
    }

    private SqliteTaskStore CreateStore(string fileName)
    {
        Directory.CreateDirectory(_directory);
        return new SqliteTaskStore(Path.Combine(_directory, fileName));
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PersonalCloudK0", name));

    private static async Task<long> CountRowsAsync(string databasePath, string table)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
