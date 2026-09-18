using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudK3PullTests : IDisposable
{
    private const string InstanceId = "instance_00000000000000000000000000000001";
    private const string DeviceId = "device_replica_00000000000000000000000000001";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-personal-cloud-k3-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task One_change_reconstructs_the_task_and_checkpoint_and_repeated_apply_is_harmless()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var page = ReadFixture("pulled-change-page.json");

        Assert.Equal(1, await store.ApplyPersonalCloudChangePageAsync(InstanceId, DeviceId, page));
        var task = Assert.IsType<Openza.Tasks.Core.Models.TaskItem>(
            await store.GetTaskAsync("local_00000000000000000000000000000001"));
        Assert.Equal("space_default", task.SpaceId);
        Assert.Equal("Validate one task", task.Title);
        Assert.Equal("open", task.CompletionState.ToStorageValue());
        Assert.Equal("inbox", task.WorkflowStatus.ToStorageValue());
        Assert.Equal(DateTimeOffset.Parse("2026-09-13T06:30:00.000Z"), task.CreatedAt);
        Assert.Equal(1, await store.GetPersonalCloudCheckpointAsync(InstanceId, DeviceId));
        Assert.Equal(1L, await CountRowsAsync(store.DatabasePath, "personal_cloud_applied_changes"));

        var restarted = new SqliteTaskStore(store.DatabasePath);
        await restarted.InitializeAsync();
        Assert.Equal(1, await restarted.GetPersonalCloudCheckpointAsync(InstanceId, DeviceId));
        Assert.Equal(1, await restarted.ApplyPersonalCloudChangePageAsync(InstanceId, DeviceId, page));
        Assert.Equal(1L, await CountRowsAsync(store.DatabasePath, "tasks", "id = 'local_00000000000000000000000000000001'"));
        Assert.Equal(1L, await CountRowsAsync(store.DatabasePath, "personal_cloud_applied_changes"));

        var caughtUp = PersonalCloudK0Codec.Serialize(new PersonalCloudK3ChangePage
        {
            ProtocolVersion = 1,
            Product = "tasks",
            Changes = [],
            NextCheckpoint = 1,
        });
        Assert.Equal(1, await restarted.ApplyPersonalCloudChangePageAsync(InstanceId, DeviceId, caughtUp));
    }

    [Fact]
    public async Task Failure_during_replica_apply_advances_neither_task_nor_checkpoint_and_restart_can_resume()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var page = ReadFixture("pulled-change-page.json");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ApplyPersonalCloudChangePageAsync(InstanceId, DeviceId, page, failAfterTaskWrite: true));
        Assert.Null(await store.GetTaskAsync("local_00000000000000000000000000000001"));
        Assert.Equal(0, await store.GetPersonalCloudCheckpointAsync(InstanceId, DeviceId));
        Assert.Equal(0L, await CountRowsAsync(store.DatabasePath, "personal_cloud_applied_changes"));

        var restarted = new SqliteTaskStore(store.DatabasePath);
        await restarted.InitializeAsync();
        Assert.Equal(0, await restarted.GetPersonalCloudCheckpointAsync(InstanceId, DeviceId));
        Assert.Equal(1, await restarted.ApplyPersonalCloudChangePageAsync(InstanceId, DeviceId, page));
        Assert.NotNull(await restarted.GetTaskAsync("local_00000000000000000000000000000001"));
        Assert.Equal(1, await restarted.GetPersonalCloudCheckpointAsync(InstanceId, DeviceId));
    }

    [Fact]
    public async Task Initialization_migrates_schema_seven_to_the_K3_replica_tables()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await ExecuteAsync(store.DatabasePath, """
            DROP TABLE personal_cloud_applied_changes;
            DROP TABLE personal_cloud_checkpoints;
            PRAGMA user_version = 7;
            """);

        await store.InitializeAsync();

        Assert.Equal(8L, await ScalarAsync(store.DatabasePath, "PRAGMA user_version"));
        Assert.Equal(1L, await CountRowsAsync(store.DatabasePath, "sqlite_master", "type = 'table' AND name = 'personal_cloud_checkpoints'"));
        Assert.Equal(1L, await CountRowsAsync(store.DatabasePath, "sqlite_master", "type = 'table' AND name = 'personal_cloud_applied_changes'"));
    }

    private SqliteTaskStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new SqliteTaskStore(Path.Combine(_directory, "openza_tasks.db"));
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PersonalCloudK0", name));

    private static async Task<long> CountRowsAsync(string databasePath, string table, string? predicate = null) =>
        await ScalarAsync(databasePath, $"SELECT COUNT(*) FROM {table}{(predicate is null ? string.Empty : $" WHERE {predicate}")}");

    private static async Task<long> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
