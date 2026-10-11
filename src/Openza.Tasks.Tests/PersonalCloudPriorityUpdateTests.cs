using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudPriorityUpdateTests : IDisposable
{
    private const string InstanceId = "instance_00000000000000000000000000000001";
    private const string SourceDeviceId = "device_00000000000000000000000000000001";
    private const string ReplicaDeviceId = "device_priority_replica_00000000000000001";
    private const string TaskId = "local_00000000000000000000000000000001";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-personal-cloud-priority-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Shared_priority_contract_is_strict_and_byte_stable()
    {
        var request = ReadFixture("valid-task-priority-update.json");
        var mutation = PersonalCloudPriorityUpdateCodec.ParseMutation(request, InstanceId);
        var generic = PersonalCloudOpenzaFieldsUpdateCodec.ParseMutation(request, InstanceId);
        var change = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskPriorityUpdatedChange>(
            ReadFixture("task-priority-updated-change.json"));
        PersonalCloudPriorityUpdateCodec.ValidateChange(change);

        Assert.Equal(3, mutation.BaseEntityVersion);
        Assert.Equal(1, mutation.Payload.Priority);
        Assert.IsType<PersonalCloudTaskPriorityUpdateMutation>(generic);
        Assert.Equal(
            ReadFixture("valid-task-priority-update.canonical.json").Trim(),
            PersonalCloudK0Codec.Canonicalize(request));
        Assert.Equal(
            ReadFixture("valid-task-priority-update.sha256").Trim(),
            PersonalCloudK0Codec.ComputeCanonicalHash(request));

        var withTitle = request.Replace(
            "\"priority\": 1",
            "\"priority\": 1, \"title\": \"Not combined\"",
            StringComparison.Ordinal);
        Assert.Throws<PersonalCloudK0ContractException>(() =>
            PersonalCloudOpenzaFieldsUpdateCodec.ParseMutation(withTitle, InstanceId));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Contract_accepts_each_local_priority(int priority)
    {
        var request = ReadFixture("valid-task-priority-update.json")
            .Replace("\"priority\": 1", $"\"priority\": {priority}", StringComparison.Ordinal);

        Assert.Equal(priority, PersonalCloudPriorityUpdateCodec.ParseMutation(request, InstanceId).Payload.Priority);
    }

    [Fact]
    public async Task Local_priority_and_intent_commit_together_and_survive_restart()
    {
        var store = CreateStore("source.db");
        await store.InitializeAsync();
        await PrepareSourceThroughWorkflowAsync(store);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.UpdateTaskPriorityWithPersonalCloudIntentAsync(TaskId, 0, 3, InstanceId, SourceDeviceId));

        var updated = await store.UpdateTaskPriorityWithPersonalCloudIntentAsync(
            TaskId, 1, 3, InstanceId, SourceDeviceId);

        Assert.Equal(4, updated.DeviceSequence);
        Assert.Equal(PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation, updated.Operation);
        var mutation = PersonalCloudPriorityUpdateCodec.ParseMutation(updated.CanonicalJson, InstanceId);
        Assert.Equal(3, mutation.BaseEntityVersion);
        Assert.Equal(1, mutation.Payload.Priority);

        var restarted = new SqliteTaskStore(store.DatabasePath);
        await restarted.InitializeAsync();
        var task = await restarted.GetTaskAsync(TaskId);
        Assert.Equal("Validate one renamed task", task?.Title);
        Assert.Equal(TaskWorkflowStatus.Next, task?.WorkflowStatus);
        Assert.Equal(1, task?.Priority);
        var pending = await restarted.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId);
        Assert.Equal(4, pending.Count);
        Assert.Equal([1L, 2L, 3L, 4L], pending.Select(entry => entry.DeviceSequence).ToArray());
    }

    [Fact]
    public async Task Outbox_failure_rolls_back_priority_and_receipt_requires_version_four()
    {
        var store = CreateStore("atomic.db");
        await store.InitializeAsync();
        await PrepareSourceThroughWorkflowAsync(store);
        await ExecuteAsync(store.DatabasePath, """
            CREATE TRIGGER fail_priority_outbox
            BEFORE INSERT ON personal_cloud_outbox
            WHEN NEW.canonical_json LIKE '%"priority"%'
            BEGIN
              SELECT RAISE(ABORT, 'injected priority outbox failure');
            END;
            """);

        await Assert.ThrowsAsync<SqliteException>(() => store.UpdateTaskPriorityWithPersonalCloudIntentAsync(
            TaskId, 1, 3, InstanceId, SourceDeviceId));
        Assert.Equal(2, (await store.GetTaskAsync(TaskId))?.Priority);
        Assert.Equal(3, (await store.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId)).Count);
        Assert.Equal(4, await ScalarAsync(store.DatabasePath,
            "SELECT next_device_sequence FROM personal_cloud_device_state"));

        await ExecuteAsync(store.DatabasePath, "DROP TRIGGER fail_priority_outbox");
        var updated = await store.UpdateTaskPriorityWithPersonalCloudIntentAsync(
            TaskId, 1, 3, InstanceId, SourceDeviceId);
        var exception = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            store.AcknowledgePersonalCloudMutationAsync(InstanceId, SourceDeviceId, ReceiptFor(updated, 3)));
        Assert.Equal("invalid_request", exception.Code);
        Assert.Equal(4, (await store.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId)).Count);

        await store.AcknowledgePersonalCloudMutationAsync(InstanceId, SourceDeviceId, ReceiptFor(updated, 4));
        Assert.Equal(3, (await store.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId)).Count);
    }

    [Fact]
    public async Task Replica_applies_priority_once_and_restart_recovers_from_mid_apply_failure()
    {
        var store = CreateStore("replica.db");
        await store.InitializeAsync();
        await store.ApplyPersonalCloudChangePageAsync(InstanceId, ReplicaDeviceId, ReadFixture("pulled-change-page.json"));
        await store.ApplyPersonalCloudChangePageAsync(InstanceId, ReplicaDeviceId, ReadFixture("pulled-title-update-page.json"));
        await store.ApplyPersonalCloudChangePageAsync(
            InstanceId, ReplicaDeviceId, ReadFixture("pulled-workflow-status-update-page.json"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyPersonalCloudChangePageAsync(
            InstanceId,
            ReplicaDeviceId,
            ReadFixture("pulled-priority-update-page.json"),
            failAfterTaskWrite: true));
        var beforeRestart = await store.GetTaskAsync(TaskId);
        Assert.Equal("Validate one renamed task", beforeRestart?.Title);
        Assert.Equal(TaskWorkflowStatus.Next, beforeRestart?.WorkflowStatus);
        Assert.Equal(2, beforeRestart?.Priority);
        Assert.Equal(3, await store.GetPersonalCloudCheckpointAsync(InstanceId, ReplicaDeviceId));

        var restarted = new SqliteTaskStore(store.DatabasePath);
        await restarted.InitializeAsync();
        Assert.Equal(4, await restarted.ApplyPersonalCloudChangePageAsync(
            InstanceId, ReplicaDeviceId, ReadFixture("pulled-priority-update-page.json")));
        var applied = await restarted.GetTaskAsync(TaskId);
        Assert.Equal("Validate one renamed task", applied?.Title);
        Assert.Equal(TaskWorkflowStatus.Next, applied?.WorkflowStatus);
        Assert.Equal(1, applied?.Priority);
        Assert.Equal(4, await restarted.GetPersonalCloudCheckpointAsync(InstanceId, ReplicaDeviceId));

        Assert.Equal(4, await restarted.ApplyPersonalCloudChangePageAsync(
            InstanceId, ReplicaDeviceId, ReadFixture("pulled-priority-update-page.json")));
        Assert.Equal(1, await ScalarAsync(restarted.DatabasePath, $"SELECT COUNT(*) FROM tasks WHERE id = '{TaskId}'"));
        Assert.Equal(4, await ScalarAsync(restarted.DatabasePath, "SELECT COUNT(*) FROM personal_cloud_applied_changes"));
    }

    private static async Task PrepareSourceThroughWorkflowAsync(SqliteTaskStore store)
    {
        await store.CreateTaskWithPersonalCloudIntentAsync(CreateTask(), InstanceId, SourceDeviceId);
        await store.UpdateTaskTitleWithPersonalCloudIntentAsync(
            TaskId, "Validate one renamed task", 1, InstanceId, SourceDeviceId);
        await store.UpdateTaskWorkflowStatusWithPersonalCloudIntentAsync(
            TaskId, TaskWorkflowStatus.Next, 2, InstanceId, SourceDeviceId);
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
            ChangeSeq = 4,
            ServerAcceptedAt = "2026-09-13T06:36:01.000Z",
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
