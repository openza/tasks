using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudTaskPlanningTests : IDisposable
{
    private const string InstanceId = "instance_00000000000000000000000000000001";
    private const string DeviceId = "device_00000000000000000000000000000001";
    private const string ReplicaId = "device_planning_replica_0000000000001";
    private const string TaskId = "local_00000000000000000000000000000001";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-personal-cloud-planning-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Shared_date_contracts_are_strict_and_exact_time_normalizes_to_utc()
    {
        var dateJson = ReadFixture("valid-task-planned-on-update.json");
        var instantJson = ReadFixture("valid-task-planned-at-update.json");
        Assert.Equal(ReadFixture("valid-task-planned-on-update.canonical.json").Trim(), PersonalCloudK0Codec.Canonicalize(dateJson));
        Assert.Equal(ReadFixture("valid-task-planned-on-update.sha256").Trim(), PersonalCloudK0Codec.ComputeCanonicalHash(dateJson));
        Assert.Equal(ReadFixture("valid-task-planned-at-update.canonical.json").Trim(), PersonalCloudK0Codec.Canonicalize(instantJson));
        Assert.Equal(ReadFixture("valid-task-planned-at-update.sha256").Trim(), PersonalCloudK0Codec.ComputeCanonicalHash(instantJson));
        Assert.Equal(new DateOnly(2026, 9, 20).ToString("yyyy-MM-dd"),
            PersonalCloudPlanningCodec.ParsePlannedOnMutation(dateJson, InstanceId).Payload.PlannedOn);

        var fromIndia = PersonalCloudPlanningCodec.CreatePlannedAtMutation(
            InstanceId, DeviceId, "mutation_timezone_test", 8, TaskId, 7,
            DateTimeOffset.Parse("2026-09-20T09:30:00+05:30"), "2026-09-13T06:44:00.000Z");
        Assert.Equal("2026-09-20T04:00:00.000Z", fromIndia.Payload.PlannedAt);
        Assert.Throws<PersonalCloudK0ContractException>(() => PersonalCloudPlanningCodec.ParsePlannedOnMutation(
            dateJson.Replace("2026-09-20", "2026-02-30", StringComparison.Ordinal), InstanceId));
    }

    [Fact]
    public async Task Source_and_replica_preserve_date_semantics_and_recover_atomically()
    {
        var source = CreateStore("source.db");
        await source.InitializeAsync();
        await PrepareSourceThroughReopenAsync(source);
        var dateEntry = await source.UpdateTaskPlannedOnWithPersonalCloudIntentAsync(
            TaskId, new DateOnly(2026, 9, 20), 6, InstanceId, DeviceId);
        Assert.Equal(7, dateEntry.DeviceSequence);
        Assert.Equal(new DateOnly(2026, 9, 20), (await source.GetTaskAsync(TaskId))?.PlannedOn);
        Assert.Null((await source.GetTaskAsync(TaskId))?.PlannedAt);

        var timeEntry = await source.UpdateTaskPlannedAtWithPersonalCloudIntentAsync(
            TaskId, DateTimeOffset.Parse("2026-09-20T09:30:00+05:30"), 7, InstanceId, DeviceId);
        Assert.Equal(8, timeEntry.DeviceSequence);
        var sourceTask = await source.GetTaskAsync(TaskId);
        Assert.Null(sourceTask?.PlannedOn);
        Assert.Equal(DateTimeOffset.Parse("2026-09-20T04:00:00Z"), sourceTask?.PlannedAt);

        var replica = CreateStore("replica.db");
        await replica.InitializeAsync();
        await ApplyReplicaThroughReopenAsync(replica);
        await Assert.ThrowsAsync<InvalidOperationException>(() => replica.ApplyPersonalCloudChangePageAsync(
            InstanceId, ReplicaId, ReadFixture("pulled-task-planned-on-update-page.json"), failAfterTaskWrite: true));
        Assert.Null((await replica.GetTaskAsync(TaskId))?.PlannedOn);
        Assert.Equal(6, await replica.GetPersonalCloudCheckpointAsync(InstanceId, ReplicaId));
        Assert.Equal(7, await replica.ApplyPersonalCloudChangePageAsync(
            InstanceId, ReplicaId, ReadFixture("pulled-task-planned-on-update-page.json")));
        Assert.Equal(8, await replica.ApplyPersonalCloudChangePageAsync(
            InstanceId, ReplicaId, ReadFixture("pulled-task-planned-at-update-page.json")));
        var replicaTask = await replica.GetTaskAsync(TaskId);
        Assert.Null(replicaTask?.PlannedOn);
        Assert.Equal(DateTimeOffset.Parse("2026-09-20T04:00:00Z"), replicaTask?.PlannedAt);
    }

    [Fact]
    public async Task Source_outbox_failure_rolls_back_the_date_and_sequence()
    {
        var store = CreateStore("rollback.db");
        await store.InitializeAsync();
        await PrepareSourceThroughReopenAsync(store);
        await ExecuteAsync(store.DatabasePath, """
            CREATE TRIGGER fail_planning_outbox BEFORE INSERT ON personal_cloud_outbox
            WHEN NEW.canonical_json LIKE '%plannedOn%'
            BEGIN SELECT RAISE(ABORT, 'injected planning failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => store.UpdateTaskPlannedOnWithPersonalCloudIntentAsync(
            TaskId, new DateOnly(2026, 9, 20), 6, InstanceId, DeviceId));
        Assert.Null((await store.GetTaskAsync(TaskId))?.PlannedOn);
        Assert.Equal(6, (await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId)).Count);
        Assert.Equal(7, await ScalarAsync(store.DatabasePath, "SELECT next_device_sequence FROM personal_cloud_device_state"));
    }

    private static async Task PrepareSourceThroughReopenAsync(SqliteTaskStore store)
    {
        await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem
        {
            Id = TaskId, SpaceId = SpaceIds.Default, Title = "Validate one task",
            CreatedAt = DateTimeOffset.Parse("2026-09-13T06:30:00Z"),
        }, InstanceId, DeviceId);
        await store.UpdateTaskTitleWithPersonalCloudIntentAsync(TaskId, "Validate one renamed task", 1, InstanceId, DeviceId);
        await store.UpdateTaskWorkflowStatusWithPersonalCloudIntentAsync(TaskId, TaskWorkflowStatus.Next, 2, InstanceId, DeviceId);
        await store.UpdateTaskPriorityWithPersonalCloudIntentAsync(TaskId, 1, 3, InstanceId, DeviceId);
        await store.CompleteTaskWithPersonalCloudIntentAsync(TaskId, DateTimeOffset.Parse("2026-09-13T06:38:00Z"), 4, InstanceId, DeviceId);
        await store.ReopenTaskWithPersonalCloudIntentAsync(TaskId, DateTimeOffset.Parse("2026-09-13T06:40:00Z"), 5, InstanceId, DeviceId);
    }

    private static async Task ApplyReplicaThroughReopenAsync(SqliteTaskStore store)
    {
        foreach (var fixture in new[] { "pulled-change-page.json", "pulled-title-update-page.json",
            "pulled-workflow-status-update-page.json", "pulled-priority-update-page.json",
            "pulled-task-completed-page.json", "pulled-task-reopened-page.json" })
            await store.ApplyPersonalCloudChangePageAsync(InstanceId, ReplicaId, ReadFixture(fixture));
    }

    private SqliteTaskStore CreateStore(string name)
    {
        Directory.CreateDirectory(_directory);
        return new SqliteTaskStore(Path.Combine(_directory, name));
    }
    private static string ReadFixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PersonalCloudK0", name));
    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = Open(path); await connection.OpenAsync();
        var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private static async Task<long> ScalarAsync(string path, string sql)
    {
        await using var connection = Open(path); await connection.OpenAsync();
        var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    private static SqliteConnection Open(string path) => new(new SqliteConnectionStringBuilder
    { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
    public void Dispose() => TestDirectory.Delete(_directory);
}
