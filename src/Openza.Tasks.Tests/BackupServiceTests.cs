using System.Text.Json;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-backup-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Stable_backup_paths_separate_production_and_dev()
    {
        var production = BackupPaths.GetStableBackupDirectory(BackupPaths.ProductionPackageIdentity);
        var dev = BackupPaths.GetStableBackupDirectory(BackupPaths.DevPackageIdentity);

        Assert.EndsWith(Path.Combine("Openza", "Tasks", "Backups"), production, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("Openza", "Tasks Dev", "Backups"), dev, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(production, dev, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine("local", "backups"), BackupPaths.GetLegacyPackageBackupDirectory("local"));
    }

    [Fact]
    public async Task CreateBackup_writes_metadata_and_lists_counts()
    {
        var databasePath = await CreateDatabaseAsync("source.db", taskId: "task_1", projectId: "project_1");
        var service = CreateService(databasePath);

        var backupPath = await service.CreateBackupAsync(BackupReasons.Manual);
        var backups = service.ListBackupInfo();

        var backup = Assert.Single(backups);
        Assert.Equal(backupPath, backup.Path);
        Assert.Equal(BackupReasons.Manual, backup.Reason);
        Assert.Equal(1, backup.TaskCount);
        Assert.Equal(1, backup.ProjectCount);
        Assert.Equal(1, backup.SpaceCount);
        Assert.Equal(BackupIntegrityStatuses.Valid, backup.IntegrityStatus);
        Assert.True(File.Exists($"{backupPath}.json"));
        Assert.Contains("Manual", backup.DisplayName, StringComparison.Ordinal);

        var metadata = JsonSerializer.Deserialize<BackupMetadata>(await File.ReadAllTextAsync($"{backupPath}.json"));
        Assert.NotNull(metadata);
        Assert.Equal("test", metadata.AppFlavor);
        Assert.Equal("1.2.3.4", metadata.AppVersion);
    }

    [Fact]
    public async Task MigrateLegacyBackups_copies_valid_backups_once()
    {
        var databasePath = await CreateDatabaseAsync("legacy-source.db", taskId: "task_legacy");
        var legacyDirectory = Path.Combine(_directory, "legacy");
        var stableDirectory = Path.Combine(_directory, "stable");
        var legacyService = CreateService(databasePath, legacyDirectory);
        var legacyBackup = await legacyService.CreateBackupAsync(BackupReasons.Manual);
        var service = CreateService(databasePath, stableDirectory);

        var firstCopyCount = await service.MigrateLegacyBackupsAsync(legacyDirectory);
        var secondCopyCount = await service.MigrateLegacyBackupsAsync(legacyDirectory);

        var copied = Assert.Single(service.ListBackupInfo());
        Assert.Equal(1, firstCopyCount);
        Assert.Equal(0, secondCopyCount);
        Assert.Equal(BackupReasons.Legacy, copied.Reason);
        Assert.NotEqual(legacyBackup, copied.Path);
    }

    [Fact]
    public async Task Retention_prunes_owned_daily_and_event_backups()
    {
        var databasePath = await CreateDatabaseAsync("retention.db", taskId: "task_retention");
        var service = CreateService(
            databasePath,
            Path.Combine(_directory, "retention-backups"),
            new BackupRetentionPolicy(DailyBackups: 1, WeeklyBackups: 0, MonthlyBackups: 0, EventBackups: 1));

        await service.CreateBackupAsync(BackupReasons.Daily);
        await service.CreateBackupAsync(BackupReasons.Daily);
        await service.CreateBackupAsync(BackupReasons.Daily);
        await service.CreateBackupAsync(BackupReasons.Manual);
        await service.CreateBackupAsync(BackupReasons.Manual);
        await service.CreateBackupAsync(BackupReasons.PreImport);

        var backups = service.ListBackupInfo();

        Assert.Equal(2, backups.Count);
        Assert.Single(backups, backup => backup.Reason == BackupReasons.Daily);
        Assert.Single(backups, backup => backup.Reason == BackupReasons.PreImport);
    }

    [Fact]
    public async Task Restore_creates_pre_restore_backup_and_replaces_database()
    {
        var currentPath = await CreateDatabaseAsync("current.db", taskId: "task_current");
        var restoreSourcePath = await CreateDatabaseAsync("restore-source.db", taskId: "task_restored");
        var service = CreateService(currentPath);

        await service.RestoreBackupAsync(restoreSourcePath);

        var restoredStore = new SqliteTaskStore(currentPath);
        await restoredStore.InitializeAsync();
        var backups = service.ListBackupInfo();

        Assert.Null(await restoredStore.GetTaskAsync("task_current"));
        Assert.NotNull(await restoredStore.GetTaskAsync("task_restored"));
        Assert.Single(backups, backup => backup.Reason == BackupReasons.PreRestore);
    }

    [Fact]
    public async Task Restore_does_not_prune_selected_backup_before_copying_it()
    {
        var currentPath = await CreateDatabaseAsync("current-prune.db", taskId: "task_current");
        var restoreSourcePath = await CreateDatabaseAsync("restore-prune-source.db", taskId: "task_restored");
        var backupDirectory = Path.Combine(_directory, "restore-prune-backups");
        var permissiveService = CreateService(
            restoreSourcePath,
            backupDirectory,
            new BackupRetentionPolicy(DailyBackups: 10, WeeklyBackups: 0, MonthlyBackups: 0, EventBackups: 10));
        var selectedBackup = await permissiveService.CreateBackupAsync(BackupReasons.Manual);
        await permissiveService.CreateBackupAsync(BackupReasons.Manual);
        await permissiveService.CreateBackupAsync(BackupReasons.PreImport);
        var restoringService = CreateService(
            currentPath,
            backupDirectory,
            new BackupRetentionPolicy(DailyBackups: 0, WeeklyBackups: 0, MonthlyBackups: 0, EventBackups: 1));

        await restoringService.RestoreBackupAsync(selectedBackup);

        var restoredStore = new SqliteTaskStore(currentPath);
        await restoredStore.InitializeAsync();
        Assert.True(File.Exists(selectedBackup));
        Assert.NotNull(await restoredStore.GetTaskAsync("task_restored"));
    }

    [Fact]
    public async Task FindLatestRestorableBackup_returns_candidate_for_fresh_database()
    {
        var backupSourcePath = await CreateDatabaseAsync("backup-source.db", taskId: "task_backup");
        var freshPath = await CreateDatabaseAsync("fresh.db");
        var backupDirectory = Path.Combine(_directory, "fresh-backups");
        await CreateService(backupSourcePath, backupDirectory).CreateBackupAsync(BackupReasons.Daily);
        var service = CreateService(freshPath, backupDirectory);

        var isFresh = await service.IsCurrentDatabaseFreshAsync();
        var candidate = await service.FindLatestRestorableBackupAsync();

        Assert.True(isFresh);
        Assert.NotNull(candidate);
        Assert.Equal(BackupReasons.Daily, candidate.Reason);
    }

    [Fact]
    public async Task Restore_rejects_corrupt_database()
    {
        var databasePath = await CreateDatabaseAsync("valid.db", taskId: "task_1");
        var corruptPath = Path.Combine(_directory, "corrupt.db");
        await File.WriteAllTextAsync(corruptPath, "not sqlite");
        var service = CreateService(databasePath);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreBackupAsync(corruptPath));
    }

    [Fact]
    public async Task List_export_and_delete_backups()
    {
        var databasePath = await CreateDatabaseAsync("export.db", taskId: "task_1");
        var service = CreateService(databasePath);

        var backupPath = await service.CreateBackupAsync(BackupReasons.Manual);
        var secondBackupPath = await service.CreateBackupAsync(BackupReasons.Manual);
        var backups = service.ListBackupInfo();

        Assert.Equal(2, backups.Count);
        var backup = backups.Single(info => info.Path == backupPath);
        Assert.Equal(backupPath, backup.Path);
        Assert.NotEmpty(backup.DisplayName);
        Assert.NotEqual(backupPath, secondBackupPath);

        var exportedPath = Path.Combine(_directory, "exported.db");
        await service.ExportBackupAsync(backup.Path, exportedPath);
        Assert.True(File.Exists(exportedPath));

        await service.DeleteBackupAsync(backup.Path);
        Assert.DoesNotContain(service.ListBackupInfo(), info => info.Path == backup.Path);
        Assert.Contains(service.ListBackupInfo(), info => info.Path == secondBackupPath);
        Assert.False(File.Exists($"{backup.Path}.json"));
    }

    private BackupService CreateService(
        string databasePath,
        string? backupDirectory = null,
        BackupRetentionPolicy? retentionPolicy = null) =>
        new(
            databasePath,
            backupDirectory ?? Path.Combine(_directory, "backups"),
            retentionPolicy,
            new BackupContext("test.identity", "test", "1.2.3.4"));

    private async Task<string> CreateDatabaseAsync(string fileName, string? taskId = null, string? projectId = null)
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, fileName);
        var store = new SqliteTaskStore(databasePath);
        await store.InitializeAsync();
        if (projectId is not null)
        {
            await store.UpsertProjectAsync(new ProjectItem
            {
                Id = projectId,
                Name = "Backed up project",
            });
        }

        if (taskId is not null)
        {
            await store.UpsertTaskAsync(new TaskItem
            {
                Id = taskId,
                Title = "Backed up task",
                ProjectId = projectId,
            });
        }

        return databasePath;
    }

    public void Dispose()
    {
        TestDirectory.Delete(_directory);
    }
}
