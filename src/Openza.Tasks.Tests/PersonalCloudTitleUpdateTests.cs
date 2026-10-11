using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudTitleUpdateTests : IDisposable
{
    private const string InstanceId = "instance_00000000000000000000000000000001";
    private const string SourceDeviceId = "device_00000000000000000000000000000001";
    private const string ReplicaDeviceId = "device_title_replica_00000000000000000001";
    private const string TaskId = "local_00000000000000000000000000000001";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-personal-cloud-title-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Shared_title_update_contract_is_strict_and_byte_stable()
    {
        var request = ReadFixture("valid-task-title-update.json");
        var mutation = PersonalCloudTitleUpdateCodec.ParseMutation(request, InstanceId);
        var change = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskTitleUpdatedChange>(ReadFixture("task-title-updated-change.json"));
        PersonalCloudTitleUpdateCodec.ValidateChange(change);

        Assert.Equal(1, mutation.BaseEntityVersion);
        Assert.Equal("Validate one renamed task", mutation.Payload.Title);
        Assert.Equal(ReadFixture("valid-task-title-update.canonical.json").Trim(), PersonalCloudK0Codec.Canonicalize(request));
        Assert.Equal(ReadFixture("valid-task-title-update.sha256").Trim(), PersonalCloudK0Codec.ComputeCanonicalHash(request));
        Assert.Equal(2, PersonalCloudK0Codec.Deserialize<K0MutationReceipt>(
            ReadFixture("accepted-task-title-update-receipt.json")).EntityVersion);

        var withExtraField = request.Replace(
            "\"title\": \"Validate one renamed task\"",
            "\"title\": \"Validate one renamed task\", \"workflowStatus\": \"done\"",
            StringComparison.Ordinal);
        Assert.Throws<PersonalCloudK0ContractException>(() =>
            PersonalCloudTitleUpdateCodec.ParseMutation(withExtraField, InstanceId));
    }

    [Fact]
    public async Task Local_title_and_update_intent_commit_together_and_survive_restart()
    {
        var store = CreateStore("source.db");
        await store.InitializeAsync();
        await store.CreateTaskWithPersonalCloudIntentAsync(CreateTask(), InstanceId, SourceDeviceId);

        var updated = await store.UpdateTaskTitleWithPersonalCloudIntentAsync(
            TaskId,
            "Validate one renamed task",
            1,
            InstanceId,
            SourceDeviceId);

        Assert.Equal(2, updated.DeviceSequence);
        Assert.Equal(PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation, updated.Operation);
        var mutation = PersonalCloudTitleUpdateCodec.ParseMutation(updated.CanonicalJson, InstanceId);
        Assert.Equal(1, mutation.BaseEntityVersion);
        Assert.Equal("Validate one renamed task", mutation.Payload.Title);

        var restarted = new SqliteTaskStore(store.DatabasePath);
        await restarted.InitializeAsync();
        Assert.Equal("Validate one renamed task", (await restarted.GetTaskAsync(TaskId))?.Title);
        var pending = await restarted.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId);
        Assert.Equal(2, pending.Count);
        Assert.Equal([1L, 2L], pending.Select(entry => entry.DeviceSequence).ToArray());
    }

    [Fact]
    public async Task Outbox_failure_rolls_back_title_and_sequence_and_receipt_requires_version_two()
    {
        var store = CreateStore("atomic.db");
        await store.InitializeAsync();
        await store.CreateTaskWithPersonalCloudIntentAsync(CreateTask(), InstanceId, SourceDeviceId);
        await ExecuteAsync(store.DatabasePath, """
            CREATE TRIGGER fail_title_update_outbox
            BEFORE INSERT ON personal_cloud_outbox
            WHEN NEW.operation = 'task.updateOpenzaFields'
            BEGIN
              SELECT RAISE(ABORT, 'injected title outbox failure');
            END;
            """);

        await Assert.ThrowsAsync<SqliteException>(() => store.UpdateTaskTitleWithPersonalCloudIntentAsync(
            TaskId, "Must roll back", 1, InstanceId, SourceDeviceId));
        Assert.Equal("Validate one task", (await store.GetTaskAsync(TaskId))?.Title);
        Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId));
        Assert.Equal(2, await ScalarAsync(store.DatabasePath,
            "SELECT next_device_sequence FROM personal_cloud_device_state"));

        await ExecuteAsync(store.DatabasePath, "DROP TRIGGER fail_title_update_outbox");
        var updated = await store.UpdateTaskTitleWithPersonalCloudIntentAsync(
            TaskId, "Validate one renamed task", 1, InstanceId, SourceDeviceId);
        var wrongVersion = ReceiptFor(updated, 1);
        var exception = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            store.AcknowledgePersonalCloudMutationAsync(InstanceId, SourceDeviceId, wrongVersion));
        Assert.Equal("invalid_request", exception.Code);
        Assert.Equal(2, (await store.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId)).Count);

        await store.AcknowledgePersonalCloudMutationAsync(InstanceId, SourceDeviceId, ReceiptFor(updated, 2));
        Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId));
    }

    [Fact]
    public async Task Replica_applies_title_once_and_restart_recovers_from_mid_apply_failure()
    {
        var store = CreateStore("replica.db");
        await store.InitializeAsync();
        await store.ApplyPersonalCloudChangePageAsync(InstanceId, ReplicaDeviceId, ReadFixture("pulled-change-page.json"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyPersonalCloudChangePageAsync(
            InstanceId,
            ReplicaDeviceId,
            ReadFixture("pulled-title-update-page.json"),
            failAfterTaskWrite: true));
        Assert.Equal("Validate one task", (await store.GetTaskAsync(TaskId))?.Title);
        Assert.Equal(1, await store.GetPersonalCloudCheckpointAsync(InstanceId, ReplicaDeviceId));
        Assert.Equal(1, await ScalarAsync(store.DatabasePath, "SELECT COUNT(*) FROM personal_cloud_applied_changes"));

        var restarted = new SqliteTaskStore(store.DatabasePath);
        await restarted.InitializeAsync();
        Assert.Equal(2, await restarted.ApplyPersonalCloudChangePageAsync(
            InstanceId, ReplicaDeviceId, ReadFixture("pulled-title-update-page.json")));
        Assert.Equal("Validate one renamed task", (await restarted.GetTaskAsync(TaskId))?.Title);
        Assert.Equal(2, await restarted.GetPersonalCloudCheckpointAsync(InstanceId, ReplicaDeviceId));
        Assert.Equal(2, await ScalarAsync(store.DatabasePath, "SELECT COUNT(*) FROM personal_cloud_applied_changes"));

        Assert.Equal(2, await restarted.ApplyPersonalCloudChangePageAsync(
            InstanceId, ReplicaDeviceId, ReadFixture("pulled-title-update-page.json")));
        Assert.Equal(1, await ScalarAsync(store.DatabasePath, $"SELECT COUNT(*) FROM tasks WHERE id = '{TaskId}'"));
        Assert.Equal(2, await ScalarAsync(store.DatabasePath, "SELECT COUNT(*) FROM personal_cloud_applied_changes"));
    }

    private static TaskItem CreateTask() => new()
    {
        Id = TaskId,
        SpaceId = SpaceIds.Default,
        Title = "Validate one task",
        CreatedAt = DateTimeOffset.Parse("2026-09-13T06:30:00Z"),
    };

    private static string ReceiptFor(PersonalCloudK1OutboxEntry entry, long entityVersion) =>
        PersonalCloudK0Codec.Serialize(new K0MutationReceipt
        {
            ProtocolVersion = 1,
            Product = PersonalCloudK0Protocol.TasksProduct,
            MutationId = entry.MutationId,
            Outcome = "accepted",
            EntityId = entry.EntityId,
            EntityVersion = entityVersion,
            ChangeSeq = 2,
            ServerAcceptedAt = "2026-09-13T06:32:01.000Z",
        });

    private SqliteTaskStore CreateStore(string name)
    {
        Directory.CreateDirectory(_directory);
        return new SqliteTaskStore(Path.Combine(_directory, name));
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PersonalCloudK0", name));

    private static async Task<long> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static SqliteConnection OpenConnection(string databasePath) => new(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false,
    }.ToString());

    public void Dispose() => TestDirectory.Delete(_directory);
}
