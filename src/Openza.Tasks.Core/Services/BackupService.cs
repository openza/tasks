using Microsoft.Data.Sqlite;

namespace Openza.Tasks.Core.Services;

public sealed class BackupService(string databasePath, string backupDirectory, int maxBackups = 7)
{
    public string DatabasePath { get; } = databasePath;
    public string BackupDirectory { get; } = backupDirectory;
    public int MaxBackups { get; } = maxBackups;

    public async Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(BackupDirectory);
        var backupPath = CreateUniqueBackupPath(BackupDirectory);
        await CopyDatabaseOnlineAsync(DatabasePath, backupPath, overwrite: false, cancellationToken).ConfigureAwait(false);
        PruneOldBackups();
        return backupPath;
    }

    public IReadOnlyList<string> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return [];
        }

        return Directory.GetFiles(BackupDirectory, "*.db")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    public IReadOnlyList<BackupInfo> ListBackupInfo()
    {
        return ListBackups()
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new BackupInfo(path, info.Name, info.LastWriteTime, info.Length);
            })
            .ToList();
    }

    public async Task ExportDatabaseAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        await CopyDatabaseOnlineAsync(DatabasePath, destinationPath, overwrite: true, cancellationToken).ConfigureAwait(false);
    }

    public Task ExportBackupAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSqliteFile(sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return Task.CompletedTask;
    }

    public Task DeleteBackupAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceDirectory = Path.GetFullPath(Path.GetDirectoryName(sourcePath) ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var backupDirectory = Path.GetFullPath(BackupDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (File.Exists(sourcePath) && string.Equals(sourceDirectory, backupDirectory, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(sourcePath);
        }

        return Task.CompletedTask;
    }

    public Task RestoreBackupAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSqliteFile(sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");

        var safetyBackupPath = $"{DatabasePath}.restore-backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
        if (File.Exists(DatabasePath))
        {
            File.Copy(DatabasePath, safetyBackupPath, overwrite: false);
        }

        try
        {
            File.Copy(sourcePath, DatabasePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(safetyBackupPath))
            {
                File.Copy(safetyBackupPath, DatabasePath, overwrite: true);
            }

            throw;
        }

        return Task.CompletedTask;
    }

    private void PruneOldBackups()
    {
        foreach (var oldBackup in ListBackups().Skip(MaxBackups))
        {
            File.Delete(oldBackup);
        }
    }

    private static string CreateUniqueBackupPath(string backupDirectory)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt:00}";
            var fileName = $"openza_tasks_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}{suffix}.db";
            var path = Path.Combine(backupDirectory, fileName);
            if (!File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(backupDirectory, $"openza_tasks_{DateTimeOffset.UtcNow.Ticks}.db");
    }

    private static async Task CopyDatabaseOnlineAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var finalDestinationPath = Path.GetFullPath(destinationPath);
        var destinationDirectory = Path.GetDirectoryName(finalDestinationPath) ?? ".";
        Directory.CreateDirectory(destinationDirectory);

        if (!overwrite && File.Exists(finalDestinationPath))
        {
            throw new IOException($"Backup file already exists: {finalDestinationPath}");
        }

        var tempPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(finalDestinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await source.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            await destination.OpenAsync(cancellationToken).ConfigureAwait(false);

            source.BackupDatabase(destination);
            await destination.CloseAsync().ConfigureAwait(false);
            await source.CloseAsync().ConfigureAwait(false);

            File.Move(tempPath, finalDestinationPath, overwrite);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void ValidateSqliteFile(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length || !header.SequenceEqual("SQLite format 3\0"u8))
        {
            throw new InvalidDataException("The selected file is not a valid SQLite database.");
        }
    }
}

public sealed class BackupInfo
{
    public BackupInfo()
    {
    }

    public BackupInfo(string path, string fileName, DateTime createdAt, long sizeBytes)
    {
        Path = path;
        FileName = fileName;
        CreatedAt = createdAt;
        SizeBytes = sizeBytes;
    }

    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public long SizeBytes { get; set; }

    public string DisplayName => $"{CreatedAt:g}  -  {FormatSize(SizeBytes)}";

    private static string FormatSize(long bytes) =>
        bytes < 1024 * 1024
            ? $"{Math.Max(1, bytes / 1024)} KB"
            : $"{bytes / 1024d / 1024d:0.0} MB";
}
