using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudK1OutboxTests : IDisposable
{
    private const string InstanceId = "instance_00000000000000000000000000000001";
    private const string DeviceId = "device_00000000000000000000000000000001";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-personal-cloud-k1-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Create_commits_one_task_and_one_immutable_outbox_entry()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var task = CreateTask("local_k1_first", "Atomic task");

        var created = await store.CreateTaskWithPersonalCloudIntentAsync(task, InstanceId, DeviceId);
        var storedTask = await store.GetTaskAsync(task.Id);
        var pending = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId));

        Assert.NotNull(storedTask);
        Assert.Equal(task.Title, storedTask.Title);
        Assert.Equal(1, created.DeviceSequence);
        Assert.Equal(PersonalCloudK0Protocol.TasksProduct, created.Product);
        Assert.Equal(created, pending);
        Assert.Equal(created.CanonicalHash, PersonalCloudK0Codec.ComputeCanonicalHash(created.CanonicalJson));
        Assert.Equal(created.MutationId, PersonalCloudK0Codec.ParseTaskCreateMutation(created.CanonicalJson, InstanceId).MutationId);
        Assert.Equal(1L, await CountRowsAsync(store.DatabasePath, "tasks", "id = 'local_k1_first'"));
        Assert.Equal(1L, await CountRowsAsync(store.DatabasePath, "personal_cloud_outbox"));
    }

    [Fact]
    public async Task Outbox_insert_failure_rolls_back_task_and_device_sequence()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await ExecuteAsync(store.DatabasePath, """
            CREATE TRIGGER fail_personal_cloud_outbox
            BEFORE INSERT ON personal_cloud_outbox
            BEGIN
              SELECT RAISE(ABORT, 'injected outbox failure');
            END;
            """);

        await Assert.ThrowsAsync<SqliteException>(() =>
            store.CreateTaskWithPersonalCloudIntentAsync(
                CreateTask("local_k1_rollback", "Must roll back"),
                InstanceId,
                DeviceId));

        Assert.Null(await store.GetTaskAsync("local_k1_rollback"));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId));
        Assert.Equal(0L, await CountRowsAsync(store.DatabasePath, "personal_cloud_device_state"));
    }

    [Fact]
    public async Task Restart_preserves_message_and_next_creation_uses_next_device_sequence()
    {
        var firstStore = CreateStore();
        await firstStore.InitializeAsync();
        var first = await firstStore.CreateTaskWithPersonalCloudIntentAsync(
            CreateTask("local_k1_restart_1", "Survive restart"),
            InstanceId,
            DeviceId);

        var restartedStore = new SqliteTaskStore(firstStore.DatabasePath);
        await restartedStore.InitializeAsync();
        var afterRestart = Assert.Single(await restartedStore.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId));
        var repeatedRead = Assert.Single(await restartedStore.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId));
        var second = await restartedStore.CreateTaskWithPersonalCloudIntentAsync(
            CreateTask("local_k1_restart_2", "Next sequence"),
            InstanceId,
            DeviceId);

        Assert.Equal(first.MutationId, afterRestart.MutationId);
        Assert.Equal(first.CanonicalJson, afterRestart.CanonicalJson);
        Assert.Equal(afterRestart, repeatedRead);
        Assert.Equal(1, first.DeviceSequence);
        Assert.Equal(2, second.DeviceSequence);
        Assert.NotEqual(first.MutationId, second.MutationId);
    }

    [Fact]
    public async Task Initialization_preserves_K1_tables_when_migrating_to_the_current_personal_cloud_schema()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await ExecuteAsync(store.DatabasePath, """
            DROP TABLE personal_cloud_outbox;
            DROP TABLE personal_cloud_device_state;
            PRAGMA user_version = 6;
            """);

        await store.InitializeAsync();

        Assert.Equal(SqliteTaskStore.CurrentSchemaVersion, await ScalarAsync(store.DatabasePath, "PRAGMA user_version"));
        Assert.Equal(1L, await CountRowsAsync(store.DatabasePath, "sqlite_master", "type = 'table' AND name = 'personal_cloud_device_state'"));
        Assert.Equal(1L, await CountRowsAsync(store.DatabasePath, "sqlite_master", "type = 'table' AND name = 'personal_cloud_outbox'"));
        Assert.Equal(1L, await CountRowsAsync(store.DatabasePath, "sqlite_master", "type = 'table' AND name = 'personal_cloud_checkpoints'"));
        Assert.Equal(1L, await CountRowsAsync(store.DatabasePath, "sqlite_master", "type = 'table' AND name = 'personal_cloud_applied_changes'"));
    }

    private SqliteTaskStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new SqliteTaskStore(Path.Combine(_directory, "openza_tasks.db"));
    }

    private static TaskItem CreateTask(string id, string title) => new()
    {
        Id = id,
        SpaceId = SpaceIds.Default,
        Title = title,
        CreatedAt = DateTimeOffset.Parse("2026-09-13T08:00:00Z"),
    };

    private static async Task<long> CountRowsAsync(string databasePath, string table, string? predicate = null) =>
        await ScalarAsync(databasePath, $"SELECT COUNT(*) FROM {table}{(predicate is null ? string.Empty : $" WHERE {predicate}")}");

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
