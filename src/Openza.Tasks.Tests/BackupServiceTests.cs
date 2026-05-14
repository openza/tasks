using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-backup-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task List_export_and_delete_backups()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "openza_tasks.db");
        var store = new SqliteTaskStore(databasePath);
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "task_1", Title = "Backed up task" });
        var service = new BackupService(databasePath, Path.Combine(_directory, "backups"));

        var backupPath = await service.CreateBackupAsync();
        var secondBackupPath = await service.CreateBackupAsync();
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
    }

    public void Dispose()
    {
        TestDirectory.Delete(_directory);
    }
}
