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
    public async Task Bootstrap_with_pending_local_edit_is_rejected_without_changing_values_or_checkpoint()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.ApplyPersonalCloudChangePageAsync(InstanceId, DeviceId, ReadFixture("pulled-change-page.json"));
        const string id = "local_00000000000000000000000000000001";
        await store.UpdateTaskTitleWithPersonalCloudIntentAsync(id, "Preserve pending edit", 1, InstanceId, DeviceId);
        const string bootstrap = """
            {"protocolVersion":1,"product":"tasks","checkpoint":5,"snapshot":{"spaces":[],"projects":[],"tasks":[],"labels":[],"taskLabels":[],"tombstones":[]}}
            """;

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            store.ReplacePersonalCloudReplicaFromBootstrapAsync(InstanceId, DeviceId, bootstrap));

        Assert.Equal("pending_intents", error.Code);
        Assert.Equal("Preserve pending edit", (await store.GetTaskAsync(id))!.Title);
        Assert.Equal(1, await store.GetPersonalCloudCheckpointAsync(InstanceId, DeviceId));
        Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId));
        Assert.Equal(1, await CountRowsAsync(store.DatabasePath, "personal_cloud_applied_changes"));
    }

    [Fact]
    public async Task Replacement_device_recognizes_acknowledged_create_echo_from_previous_device()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        const string id = "local_00000000000000000000000000000001";
        var outbox = await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem
        {
            Id = id, Title = "Validate one task", SpaceId = SpaceIds.Default,
            CreatedAt = DateTimeOffset.Parse("2026-09-13T06:30:00Z"),
        }, InstanceId, DeviceId);
        await store.AcknowledgePersonalCloudMutationAsync(InstanceId, DeviceId,
            PersonalCloudK0Codec.Serialize(new K0MutationReceipt
            {
                ProtocolVersion = 1, Product = "tasks", MutationId = outbox.MutationId,
                Outcome = "accepted", EntityId = id, EntityVersion = 1, ChangeSeq = 1,
                ServerAcceptedAt = "2026-09-13T06:30:01.000Z",
            }));
        var originalPage = PersonalCloudK3Codec.ParseChangePage(ReadFixture("pulled-change-page.json"));
        var change = Assert.IsType<K0TaskCreatedChange>(Assert.Single(originalPage.Changes));
        var page = PersonalCloudK0Codec.Serialize(new
        {
            originalPage.ProtocolVersion,
            originalPage.Product,
            Changes = new[] { change with { MutationId = outbox.MutationId } },
            originalPage.NextCheckpoint,
        });

        Assert.Equal(1, await store.ApplyPersonalCloudChangePageAsync(InstanceId, "device_replacement", page));
        Assert.Equal(1, await CountRowsAsync(store.DatabasePath, "tasks"));
        Assert.Equal(1, await CountRowsAsync(store.DatabasePath, "personal_cloud_applied_changes"));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId));
    }

    [Fact]
    public async Task Repaired_device_resumes_shared_replica_cursor_without_replaying_or_skipping_uncommitted_changes()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.ApplyPersonalCloudChangePageAsync(InstanceId, DeviceId, ReadFixture("pulled-change-page.json"));
        const string replacement = "device_replacement";
        Assert.Equal(1, await store.GetPersonalCloudCheckpointAsync(InstanceId, replacement));
        Assert.Equal(0, await store.GetPersonalCloudCheckpointAsync("different_instance", replacement));
        var titlePage = ReadFixture("pulled-title-update-page.json");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyPersonalCloudChangePageAsync(InstanceId, replacement, titlePage, failAfterTaskWrite: true));
        Assert.Equal(1, await store.GetPersonalCloudCheckpointAsync(InstanceId, replacement));
        Assert.Equal("Validate one task", (await store.GetTaskAsync("local_00000000000000000000000000000001"))!.Title);
        Assert.Equal(2, await store.ApplyPersonalCloudChangePageAsync(InstanceId, replacement, titlePage));
        Assert.Equal(2, await store.GetPersonalCloudCheckpointAsync(InstanceId, DeviceId));
        Assert.Equal(2, await store.GetPersonalCloudCheckpointAsync(InstanceId, "third_device"));
        Assert.Equal(2, await store.ApplyPersonalCloudChangePageAsync(InstanceId, DeviceId, titlePage));
        Assert.Equal(1, await CountRowsAsync(store.DatabasePath, "tasks"));
        Assert.Equal(2, await CountRowsAsync(store.DatabasePath, "personal_cloud_applied_changes"));
        var fresh = new SqliteTaskStore(Path.Combine(_directory, "fresh.db"));
        await fresh.InitializeAsync();
        Assert.Equal(0, await fresh.GetPersonalCloudCheckpointAsync(InstanceId, replacement));
        const string emptyBootstrap = """
            {"protocolVersion":1,"product":"tasks","checkpoint":0,"snapshot":{"spaces":[],"projects":[],"tasks":[],"labels":[],"taskLabels":[],"tombstones":[]}}
            """;
        await store.ReplacePersonalCloudReplicaFromBootstrapAsync("different_instance", replacement, emptyBootstrap.Replace("\"checkpoint\":0", "\"checkpoint\":9"));
        Assert.Equal(2, await store.GetPersonalCloudCheckpointAsync(InstanceId, replacement));
        await store.ReplacePersonalCloudReplicaFromBootstrapAsync(InstanceId, replacement, emptyBootstrap);
        Assert.Equal(0, await store.GetPersonalCloudCheckpointAsync(InstanceId, DeviceId));
        Assert.Equal(9, await store.GetPersonalCloudCheckpointAsync("different_instance", replacement));
    }

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

        Assert.Equal((long)SqliteTaskStore.CurrentSchemaVersion, await ScalarAsync(store.DatabasePath, "PRAGMA user_version"));
        Assert.Equal(1L, await CountRowsAsync(store.DatabasePath, "sqlite_master", "type = 'table' AND name = 'personal_cloud_checkpoints'"));
        Assert.Equal(1L, await CountRowsAsync(store.DatabasePath, "sqlite_master", "type = 'table' AND name = 'personal_cloud_applied_changes'"));
    }

    [Fact]
    public async Task Schema_eight_journals_migrate_without_losing_history_and_allow_physical_task_deletion()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.ApplyPersonalCloudChangePageAsync(InstanceId, DeviceId, ReadFixture("pulled-change-page.json"));
        const string replicaId = "local_00000000000000000000000000000001";
        var outbox = await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem
        {
            Id = "local_journal_task", Title = "Journal history", SpaceId = SpaceIds.Default,
        }, InstanceId, DeviceId);
        await store.AcknowledgePersonalCloudMutationAsync(InstanceId, DeviceId,
            PersonalCloudK0Codec.Serialize(new K0MutationReceipt
            {
                ProtocolVersion = 1, Product = "tasks", MutationId = outbox.MutationId,
                Outcome = "accepted", EntityId = outbox.EntityId, EntityVersion = 1, ChangeSeq = 2,
                ServerAcceptedAt = "2026-09-13T06:30:01.000Z",
            }));
        await ExecuteAsync(store.DatabasePath, $"""
            INSERT INTO personal_cloud_tombstones(instance_id,product,entity_id,entity_version,deleted_at)
            VALUES('{InstanceId}','tasks','{replicaId}',2,1);
            """);
        await using (var fixture = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await fixture.OpenAsync();
            foreach (var table in new[] { "personal_cloud_outbox", "personal_cloud_applied_changes", "personal_cloud_tombstones" })
            {
                var schema = fixture.CreateCommand();
                schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@table";
                schema.Parameters.AddWithValue("@table", table);
                var oldSchema = ((string)(await schema.ExecuteScalarAsync())!)
                    .Replace($"CREATE TABLE {table}", $"CREATE TABLE {table}_v8", StringComparison.Ordinal)
                    .Replace("entity_id TEXT NOT NULL,", "entity_id TEXT NOT NULL REFERENCES tasks(id),", StringComparison.Ordinal);
                var rebuild = fixture.CreateCommand();
                rebuild.CommandText = $"""
                    {oldSchema};
                    INSERT INTO {table}_v8 SELECT * FROM {table};
                    DROP TABLE {table};
                    ALTER TABLE {table}_v8 RENAME TO {table};
                    """;
                await rebuild.ExecuteNonQueryAsync();
            }
            var version = fixture.CreateCommand();
            version.CommandText = "PRAGMA user_version=8";
            await version.ExecuteNonQueryAsync();
        }

        await store.InitializeAsync();
        await store.InitializeAsync(); // The migration remains safe on restart.
        await store.DeleteTaskAsync(outbox.EntityId);
        await store.DeleteTaskAsync(replicaId);

        Assert.Equal(SqliteTaskStore.CurrentSchemaVersion, await ScalarAsync(store.DatabasePath, "PRAGMA user_version"));
        Assert.Equal(1, await CountRowsAsync(store.DatabasePath, "personal_cloud_outbox"));
        Assert.Equal(1, await CountRowsAsync(store.DatabasePath, "personal_cloud_applied_changes"));
        Assert.Equal(1, await CountRowsAsync(store.DatabasePath, "personal_cloud_tombstones"));
        var next = await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem
        {
            Id = "local_after_migration", Title = "After migration", SpaceId = SpaceIds.Default,
        }, InstanceId, DeviceId);
        Assert.Equal(outbox.LocalSequence + 1, next.LocalSequence);
        Assert.Equal(outbox.DeviceSequence + 1, next.DeviceSequence);
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
