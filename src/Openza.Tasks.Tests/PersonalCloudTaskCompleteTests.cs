using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudTaskCompleteTests : IDisposable
{
    private const string InstanceId = "instance_00000000000000000000000000000001";
    private const string SourceDeviceId = "device_00000000000000000000000000000001";
    private const string ReplicaDeviceId = "device_complete_replica_0000000000001";
    private const string TaskId = "local_00000000000000000000000000000001";
    private static readonly DateTimeOffset CompletedAt = DateTimeOffset.Parse("2026-09-13T06:38:00.000Z");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-personal-cloud-complete-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Shared_completion_contract_is_strict_and_byte_stable()
    {
        var request = ReadFixture("valid-task-complete.json");
        var mutation = PersonalCloudCompletionCodec.ParseMutation(request, InstanceId);
        var change = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskCompletedChange>(
            ReadFixture("task-completed-change.json"));
        PersonalCloudCompletionCodec.ValidateChange(change);

        Assert.Equal(4, mutation.BaseEntityVersion);
        Assert.Equal("2026-09-13T06:38:00.000Z", mutation.Payload.CompletedAt);
        Assert.Equal(ReadFixture("valid-task-complete.canonical.json").Trim(), PersonalCloudK0Codec.Canonicalize(request));
        Assert.Equal(ReadFixture("valid-task-complete.sha256").Trim(), PersonalCloudK0Codec.ComputeCanonicalHash(request));

        var extra = request.Replace(
            "\"completedAt\": \"2026-09-13T06:38:00.000Z\"",
            "\"completedAt\": \"2026-09-13T06:38:00.000Z\", \"reopen\": false",
            StringComparison.Ordinal);
        Assert.Throws<PersonalCloudK0ContractException>(() => PersonalCloudCompletionCodec.ParseMutation(extra, InstanceId));
    }

    [Fact]
    public async Task Local_completion_and_intent_commit_together_and_survive_restart()
    {
        var store = CreateStore("source.db");
        await store.InitializeAsync();
        await PrepareSourceThroughPriorityAsync(store);

        var completed = await store.CompleteTaskWithPersonalCloudIntentAsync(
            TaskId, CompletedAt, 4, InstanceId, SourceDeviceId);

        Assert.Equal(5, completed.DeviceSequence);
        Assert.Equal(PersonalCloudK0Protocol.CompleteTaskOperation, completed.Operation);
        var mutation = PersonalCloudCompletionCodec.ParseMutation(completed.CanonicalJson, InstanceId);
        Assert.Equal(4, mutation.BaseEntityVersion);
        Assert.Equal("2026-09-13T06:38:00.000Z", mutation.Payload.CompletedAt);

        var restarted = new SqliteTaskStore(store.DatabasePath);
        await restarted.InitializeAsync();
        var task = await restarted.GetTaskAsync(TaskId);
        Assert.Equal(TaskCompletionState.Completed, task?.CompletionState);
        Assert.Equal(CompletedAt, task?.CompletedAt);
        Assert.Equal("Validate one renamed task", task?.Title);
        Assert.Equal(TaskWorkflowStatus.Next, task?.WorkflowStatus);
        Assert.Equal(1, task?.Priority);
        Assert.Equal(5, (await restarted.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId)).Count);

        var exception = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            restarted.CompleteTaskWithPersonalCloudIntentAsync(TaskId, CompletedAt, 5, InstanceId, SourceDeviceId));
        Assert.Equal("invalid_transition", exception.Code);
    }

    [Fact]
    public async Task Outbox_failure_rolls_back_completion_and_receipt_requires_version_five()
    {
        var store = CreateStore("atomic.db");
        await store.InitializeAsync();
        await PrepareSourceThroughPriorityAsync(store);
        await ExecuteAsync(store.DatabasePath, """
            CREATE TRIGGER fail_complete_outbox
            BEFORE INSERT ON personal_cloud_outbox
            WHEN NEW.operation = 'task.complete'
            BEGIN
              SELECT RAISE(ABORT, 'injected completion outbox failure');
            END;
            """);

        await Assert.ThrowsAsync<SqliteException>(() => store.CompleteTaskWithPersonalCloudIntentAsync(
            TaskId, CompletedAt, 4, InstanceId, SourceDeviceId));
        var open = await store.GetTaskAsync(TaskId);
        Assert.Equal(TaskCompletionState.Open, open?.CompletionState);
        Assert.Null(open?.CompletedAt);
        Assert.Equal(4, (await store.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId)).Count);
        Assert.Equal(5, await ScalarAsync(store.DatabasePath, "SELECT next_device_sequence FROM personal_cloud_device_state"));

        await ExecuteAsync(store.DatabasePath, "DROP TRIGGER fail_complete_outbox");
        var completed = await store.CompleteTaskWithPersonalCloudIntentAsync(
            TaskId, CompletedAt, 4, InstanceId, SourceDeviceId);
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            store.AcknowledgePersonalCloudMutationAsync(InstanceId, SourceDeviceId, ReceiptFor(completed, 4)));
        await store.AcknowledgePersonalCloudMutationAsync(InstanceId, SourceDeviceId, ReceiptFor(completed, 5));
        Assert.Equal(4, (await store.GetPendingPersonalCloudOutboxAsync(InstanceId, SourceDeviceId)).Count);
    }

    [Fact]
    public async Task Replica_completion_is_atomic_idempotent_and_preserves_openza_fields()
    {
        var store = CreateStore("replica.db");
        await store.InitializeAsync();
        await ApplyReplicaThroughPriorityAsync(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyPersonalCloudChangePageAsync(
            InstanceId, ReplicaDeviceId, ReadFixture("pulled-task-completed-page.json"), failAfterTaskWrite: true));
        var open = await store.GetTaskAsync(TaskId);
        Assert.Equal(TaskCompletionState.Open, open?.CompletionState);
        Assert.Null(open?.CompletedAt);
        Assert.Equal(4, await store.GetPersonalCloudCheckpointAsync(InstanceId, ReplicaDeviceId));

        var restarted = new SqliteTaskStore(store.DatabasePath);
        await restarted.InitializeAsync();
        Assert.Equal(5, await restarted.ApplyPersonalCloudChangePageAsync(
            InstanceId, ReplicaDeviceId, ReadFixture("pulled-task-completed-page.json")));
        var completed = await restarted.GetTaskAsync(TaskId);
        Assert.Equal(TaskCompletionState.Completed, completed?.CompletionState);
        Assert.Equal(CompletedAt, completed?.CompletedAt);
        Assert.Equal("Validate one renamed task", completed?.Title);
        Assert.Equal(TaskWorkflowStatus.Next, completed?.WorkflowStatus);
        Assert.Equal(1, completed?.Priority);

        Assert.Equal(5, await restarted.ApplyPersonalCloudChangePageAsync(
            InstanceId, ReplicaDeviceId, ReadFixture("pulled-task-completed-page.json")));
        Assert.Equal(5, await ScalarAsync(restarted.DatabasePath, "SELECT COUNT(*) FROM personal_cloud_applied_changes"));
    }

    private static async Task PrepareSourceThroughPriorityAsync(SqliteTaskStore store)
    {
        await store.CreateTaskWithPersonalCloudIntentAsync(CreateTask(), InstanceId, SourceDeviceId);
        await store.UpdateTaskTitleWithPersonalCloudIntentAsync(TaskId, "Validate one renamed task", 1, InstanceId, SourceDeviceId);
        await store.UpdateTaskWorkflowStatusWithPersonalCloudIntentAsync(TaskId, TaskWorkflowStatus.Next, 2, InstanceId, SourceDeviceId);
        await store.UpdateTaskPriorityWithPersonalCloudIntentAsync(TaskId, 1, 3, InstanceId, SourceDeviceId);
    }

    private static async Task ApplyReplicaThroughPriorityAsync(SqliteTaskStore store)
    {
        await store.ApplyPersonalCloudChangePageAsync(InstanceId, ReplicaDeviceId, ReadFixture("pulled-change-page.json"));
        await store.ApplyPersonalCloudChangePageAsync(InstanceId, ReplicaDeviceId, ReadFixture("pulled-title-update-page.json"));
        await store.ApplyPersonalCloudChangePageAsync(InstanceId, ReplicaDeviceId, ReadFixture("pulled-workflow-status-update-page.json"));
        await store.ApplyPersonalCloudChangePageAsync(InstanceId, ReplicaDeviceId, ReadFixture("pulled-priority-update-page.json"));
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
            ChangeSeq = 5,
            ServerAcceptedAt = "2026-09-13T06:38:01.000Z",
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
