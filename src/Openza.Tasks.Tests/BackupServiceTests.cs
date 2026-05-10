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
        await File.WriteAllBytesAsync(databasePath, "SQLite format 3\0"u8.ToArray());
        var service = new BackupService(databasePath, Path.Combine(_directory, "backups"));

        var backupPath = await service.CreateBackupAsync();
        var backups = service.ListBackupInfo();

        var backup = Assert.Single(backups);
        Assert.Equal(backupPath, backup.Path);
        Assert.NotEmpty(backup.DisplayName);

        var exportedPath = Path.Combine(_directory, "exported.db");
        await service.ExportBackupAsync(backup.Path, exportedPath);
        Assert.True(File.Exists(exportedPath));

        await service.DeleteBackupAsync(backup.Path);
        Assert.Empty(service.ListBackupInfo());
    }

    public void Dispose()
    {
        TestDirectory.Delete(_directory);
    }
}
