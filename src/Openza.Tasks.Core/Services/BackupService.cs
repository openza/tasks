namespace Openza.Tasks.Core.Services;

public sealed class BackupService(string databasePath, string backupDirectory, int maxBackups = 7)
{
    public string DatabasePath { get; } = databasePath;
    public string BackupDirectory { get; } = backupDirectory;
    public int MaxBackups { get; } = maxBackups;

    public Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(BackupDirectory);
        var backupPath = Path.Combine(BackupDirectory, $"openza_tasks_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.db");
        File.Copy(DatabasePath, backupPath, overwrite: false);
        PruneOldBackups();
        return Task.FromResult(backupPath);
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

    public Task ExportDatabaseAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        File.Copy(DatabasePath, destinationPath, overwrite: true);
        return Task.CompletedTask;
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
