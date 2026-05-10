using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Migration;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed class FlutterDatabaseMigratorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-tasks-migration-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Migrates_existing_database_once_and_creates_backup()
    {
        Directory.CreateDirectory(_directory);
        var legacy = Path.Combine(_directory, "legacy.db");
        var target = Path.Combine(_directory, "target.db");

        var legacyStore = new SqliteTaskStore(legacy);
        await legacyStore.InitializeAsync();
        await legacyStore.UpsertTaskAsync(new TaskItem { Id = "task_1", Title = "Migrated task" });

        var migrator = new FlutterDatabaseMigrator(legacy, target);
        var result = await migrator.MigrateIfNeededAsync();
        var second = await migrator.MigrateIfNeededAsync();

        Assert.True(result.WasMigrated);
        Assert.True(File.Exists(target));
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.False(second.WasMigrated);
    }

    [Fact]
    public async Task Replaces_empty_seed_database_when_legacy_database_appears()
    {
        Directory.CreateDirectory(_directory);
        var legacy = Path.Combine(_directory, "legacy.db");
        var target = Path.Combine(_directory, "target.db");

        var targetStore = new SqliteTaskStore(target);
        await targetStore.InitializeAsync();

        var legacyStore = new SqliteTaskStore(legacy);
        await legacyStore.InitializeAsync();
        await legacyStore.UpsertTaskAsync(new TaskItem { Id = "legacy_task", Title = "From old app" });

        var migrator = new FlutterDatabaseMigrator(legacy, target);
        var result = await migrator.MigrateIfNeededAsync();

        var migratedStore = new SqliteTaskStore(target);
        await migratedStore.InitializeAsync();
        var tasks = await migratedStore.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All });

        Assert.True(result.WasMigrated);
        Assert.Contains(tasks, task => task.Id == "legacy_task");
        Assert.True(File.Exists(Directory.GetFiles(_directory, "target.db.empty-before-legacy-migration-*.bak").Single()));
    }

    [Fact]
    public async Task Uses_later_candidate_when_earlier_legacy_database_is_empty()
    {
        Directory.CreateDirectory(_directory);
        var emptyLegacy = Path.Combine(_directory, "empty-legacy.db");
        var legacyWithTasks = Path.Combine(_directory, "legacy-with-tasks.db");
        var target = Path.Combine(_directory, "target.db");

        var emptyStore = new SqliteTaskStore(emptyLegacy);
        await emptyStore.InitializeAsync();

        var legacyStore = new SqliteTaskStore(legacyWithTasks);
        await legacyStore.InitializeAsync();
        await legacyStore.UpsertTaskAsync(new TaskItem { Id = "legacy_task", Title = "From second candidate" });

        var migrator = new FlutterDatabaseMigrator(emptyLegacy, target);
        var result = await migrator.MigrateIfNeededAsync([emptyLegacy, legacyWithTasks]);

        Assert.True(result.WasMigrated);
        Assert.Equal(legacyWithTasks, result.SourcePath);
    }

    [Fact]
    public async Task Does_not_replace_target_with_existing_tasks()
    {
        Directory.CreateDirectory(_directory);
        var legacy = Path.Combine(_directory, "legacy.db");
        var target = Path.Combine(_directory, "target.db");

        var legacyStore = new SqliteTaskStore(legacy);
        await legacyStore.InitializeAsync();
        await legacyStore.UpsertTaskAsync(new TaskItem { Id = "legacy_task", Title = "From old app" });

        var targetStore = new SqliteTaskStore(target);
        await targetStore.InitializeAsync();
        await targetStore.UpsertTaskAsync(new TaskItem { Id = "current_task", Title = "Current app" });

        var migrator = new FlutterDatabaseMigrator(legacy, target);
        var result = await migrator.MigrateIfNeededAsync();
        var tasks = await targetStore.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All });

        Assert.False(result.WasMigrated);
        Assert.Contains(tasks, task => task.Id == "current_task");
        Assert.DoesNotContain(tasks, task => task.Id == "legacy_task");
    }

    [Fact]
    public void Includes_roaming_openza_openza_database_candidate()
    {
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var expected = Path.Combine(roamingAppData, "openza", "openza.db");

        Assert.Contains(expected, AppDataPaths.GetLegacyFlutterDatabaseCandidates());
    }

    [Fact]
    public async Task Skips_when_legacy_database_is_missing()
    {
        Directory.CreateDirectory(_directory);
        var migrator = new FlutterDatabaseMigrator(Path.Combine(_directory, "missing.db"), Path.Combine(_directory, "target.db"));

        var result = await migrator.MigrateIfNeededAsync();

        Assert.False(result.WasMigrated);
    }

    public void Dispose()
    {
        TestDirectory.Delete(_directory);
    }
}
