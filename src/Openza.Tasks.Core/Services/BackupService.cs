using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Openza.Tasks.Core.Services;

public sealed class BackupService(
    string databasePath,
    string backupDirectory,
    BackupRetentionPolicy? retentionPolicy = null,
    BackupContext? context = null)
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new() { WriteIndented = true };

    public string DatabasePath { get; } = databasePath;
    public string BackupDirectory { get; } = backupDirectory;
    public BackupRetentionPolicy RetentionPolicy { get; } = retentionPolicy ?? BackupRetentionPolicy.Default;
    public BackupContext Context { get; } = context ?? BackupContext.Unknown;

    public Task<string> CreateBackupAsync(CancellationToken cancellationToken = default) =>
        CreateBackupAsync(BackupReasons.Manual, cancellationToken);

    public async Task<string> CreateBackupAsync(string reason, CancellationToken cancellationToken = default)
    {
        return await CreateBackupAsync(reason, pruneAfterCreate: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CreateBackupAsync(string reason, bool pruneAfterCreate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(BackupDirectory);
        var backupPath = CreateUniqueBackupPath(BackupDirectory);
        await CopyDatabaseOnlineAsync(DatabasePath, backupPath, overwrite: false, cancellationToken).ConfigureAwait(false);
        await WriteMetadataAsync(backupPath, reason, DateTimeOffset.Now, cancellationToken).ConfigureAwait(false);
        if (pruneAfterCreate)
        {
            PruneOldBackups();
        }

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
            .Select(ReadBackupInfo)
            .OrderByDescending(info => info.CreatedAt)
            .ToList();
    }

    public async Task<int> MigrateLegacyBackupsAsync(string legacyDirectory, CancellationToken cancellationToken = default)
    {
        return await MigrateLegacyBackupsAsync([legacyDirectory], cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> MigrateLegacyBackupsAsync(IEnumerable<string> legacyDirectories, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(BackupDirectory);
        var knownHashes = new HashSet<string>(
            ListBackups().Select(path => ComputeFileHash(path)),
            StringComparer.OrdinalIgnoreCase);
        var copied = 0;
        foreach (var legacyDirectory in legacyDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(legacyDirectory) || IsSameDirectory(legacyDirectory, BackupDirectory))
            {
                continue;
            }

            foreach (var sourcePath in Directory.GetFiles(legacyDirectory, "*.db"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryValidateSqliteFile(sourcePath, out _))
                {
                    continue;
                }

                var hash = ComputeFileHash(sourcePath);
                if (!knownHashes.Add(hash))
                {
                    continue;
                }

                var destinationPath = CreateUniqueBackupPath(BackupDirectory, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, destinationPath, overwrite: false);
                if (!TryCopyMetadata(sourcePath, destinationPath))
                {
                    var createdAt = new DateTimeOffset(File.GetLastWriteTime(destinationPath));
                    await WriteMetadataAsync(destinationPath, BackupReasons.Legacy, createdAt, cancellationToken).ConfigureAwait(false);
                }

                copied++;
            }
        }

        PruneOldBackups();
        return copied;
    }

    public async Task<BackupInfo?> FindLatestRestorableBackupAsync(CancellationToken cancellationToken = default)
    {
        foreach (var backup in ListBackupInfo())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryValidateSqliteFile(backup.Path, out _))
            {
                return backup;
            }
        }

        return null;
    }

    public async Task<bool> IsCurrentDatabaseFreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(DatabasePath))
        {
            return true;
        }

        var snapshot = await InspectDatabaseAsync(DatabasePath, cancellationToken).ConfigureAwait(false);
        return snapshot.IntegrityStatus == BackupIntegrityStatuses.Valid &&
            snapshot.TaskCount == 0 &&
            snapshot.ProjectCount <= 1 &&
            snapshot.SpaceCount <= 1;
    }

    public async Task<bool> ShouldCreatePreMigrationBackupAsync(int currentSchemaVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(DatabasePath) || !TryValidateSqliteHeader(DatabasePath))
        {
            return false;
        }

        // A live database can have a rollback journal after an interrupted exit. Open
        // read-write here so SQLite can recover it before reading schema metadata.
        await using var connection = await OpenConnectionAsync(DatabasePath, SqliteOpenMode.ReadWrite, cancellationToken).ConfigureAwait(false);
        var userVersion = Convert.ToInt32(await ExecuteScalarAsync(connection, "PRAGMA user_version", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (userVersion >= currentSchemaVersion)
        {
            return false;
        }

        var hasUserTables =
            await TableExistsAsync(connection, "tasks", cancellationToken).ConfigureAwait(false) ||
            await TableExistsAsync(connection, "projects", cancellationToken).ConfigureAwait(false) ||
            await TableExistsAsync(connection, "spaces", cancellationToken).ConfigureAwait(false);
        return hasUserTables;
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
            var metadataPath = MetadataPath(sourcePath);
            if (File.Exists(metadataPath))
            {
                File.Delete(metadataPath);
            }
        }

        return Task.CompletedTask;
    }

    public async Task RestoreBackupAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSqliteFile(sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");

        string? safetyBackupPath = null;
        if (File.Exists(DatabasePath))
        {
            safetyBackupPath = await CreateBackupAsync(BackupReasons.PreRestore, pruneAfterCreate: false, cancellationToken).ConfigureAwait(false);
        }

        var restoreTempPath = Path.Combine(
            Path.GetDirectoryName(DatabasePath) ?? ".",
            $".{Path.GetFileName(DatabasePath)}.restore.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourcePath, restoreTempPath, overwrite: false);
            File.Copy(restoreTempPath, DatabasePath, overwrite: true);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(safetyBackupPath) && File.Exists(safetyBackupPath))
            {
                File.Copy(safetyBackupPath, DatabasePath, overwrite: true);
            }

            throw;
        }
        finally
        {
            if (File.Exists(restoreTempPath))
            {
                File.Delete(restoreTempPath);
            }
        }
    }

    private BackupInfo ReadBackupInfo(string path)
    {
        var file = new FileInfo(path);
        var metadata = ReadMetadata(path);
        return new BackupInfo
        {
            Path = path,
            FileName = file.Name,
            CreatedAt = metadata?.CreatedAt.LocalDateTime ?? file.LastWriteTime,
            SizeBytes = file.Length,
            Reason = metadata?.Reason ?? BackupReasons.Unknown,
            TaskCount = metadata?.TaskCount,
            ProjectCount = metadata?.ProjectCount,
            SpaceCount = metadata?.SpaceCount,
            AppFlavor = metadata?.AppFlavor ?? Context.AppFlavor,
            AppVersion = metadata?.AppVersion ?? Context.AppVersion,
            IntegrityStatus = metadata?.IntegrityStatus ?? BackupIntegrityStatuses.Unknown,
            HasMetadata = metadata is not null,
        };
    }

    private async Task WriteMetadataAsync(string backupPath, string reason, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        var snapshot = await InspectDatabaseAsync(backupPath, cancellationToken).ConfigureAwait(false);
        var metadata = new BackupMetadata
        {
            BackupPath = backupPath,
            CreatedAt = createdAt,
            Reason = string.IsNullOrWhiteSpace(reason) ? BackupReasons.Manual : reason,
            AppIdentity = Context.AppIdentity,
            AppFlavor = Context.AppFlavor,
            AppVersion = Context.AppVersion,
            DatabaseSizeBytes = new FileInfo(backupPath).Length,
            TaskCount = snapshot.TaskCount,
            ProjectCount = snapshot.ProjectCount,
            SpaceCount = snapshot.SpaceCount,
            IntegrityStatus = snapshot.IntegrityStatus,
            ContentHash = ComputeFileHash(backupPath),
        };

        await using var stream = File.Create(MetadataPath(backupPath));
        await JsonSerializer.SerializeAsync(stream, metadata, MetadataJsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static BackupMetadata? ReadMetadata(string backupPath)
    {
        var metadataPath = MetadataPath(backupPath);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(metadataPath);
            return JsonSerializer.Deserialize<BackupMetadata>(stream);
        }
        catch
        {
            return null;
        }
    }

    private void PruneOldBackups()
    {
        var backups = ListBackupInfo()
            .Where(info => info.HasMetadata)
            .OrderByDescending(info => info.CreatedAt)
            .ToList();
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var backup in backups.Where(IsDailyBackup).Take(RetentionPolicy.DailyBackups))
        {
            retained.Add(backup.Path);
        }

        foreach (var backup in backups.Where(IsDailyBackup)
                     .GroupBy(backup => $"{ISOWeek.GetYear(backup.CreatedAt)}-{ISOWeek.GetWeekOfYear(backup.CreatedAt):D2}")
                     .Take(RetentionPolicy.WeeklyBackups)
                     .Select(group => group.First()))
        {
            retained.Add(backup.Path);
        }

        foreach (var backup in backups.Where(IsDailyBackup)
                     .GroupBy(backup => backup.CreatedAt.ToString("yyyy-MM", CultureInfo.InvariantCulture))
                     .Take(RetentionPolicy.MonthlyBackups)
                     .Select(group => group.First()))
        {
            retained.Add(backup.Path);
        }

        foreach (var backup in backups.Where(backup => !IsDailyBackup(backup)).Take(RetentionPolicy.EventBackups))
        {
            retained.Add(backup.Path);
        }

        foreach (var oldBackup in backups.Where(backup => !retained.Contains(backup.Path)))
        {
            File.Delete(oldBackup.Path);
            var metadataPath = MetadataPath(oldBackup.Path);
            if (File.Exists(metadataPath))
            {
                File.Delete(metadataPath);
            }
        }
    }

    private static bool IsDailyBackup(BackupInfo backup) =>
        string.Equals(backup.Reason, BackupReasons.Daily, StringComparison.OrdinalIgnoreCase);

    private static string CreateUniqueBackupPath(string backupDirectory, string? preferredFileName = null)
    {
        var baseName = string.IsNullOrWhiteSpace(preferredFileName)
            ? $"openza_tasks_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}.db"
            : Path.GetFileName(preferredFileName);
        var stem = Path.GetFileNameWithoutExtension(baseName);
        var extension = Path.GetExtension(baseName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".db";
        }

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt:00}";
            var path = Path.Combine(backupDirectory, $"{stem}{suffix}{extension}");
            if (!File.Exists(path) && !File.Exists(MetadataPath(path)))
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
        if (!TryValidateSqliteFile(path, out var error))
        {
            throw new InvalidDataException(error);
        }
    }

    private static bool TryValidateSqliteFile(string path, out string error)
    {
        error = string.Empty;
        try
        {
            if (!TryValidateSqliteHeader(path))
            {
                error = "The selected file is not a valid SQLite database.";
                return false;
            }

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            var result = command.ExecuteScalar()?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                error = $"SQLite integrity check failed: {result}";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TryValidateSqliteHeader(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        Span<byte> header = stackalloc byte[16];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == header.Length && header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static async Task<DatabaseSnapshot> InspectDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        if (!TryValidateSqliteFile(path, out _))
        {
            return new DatabaseSnapshot(0, 0, 0, BackupIntegrityStatuses.Invalid);
        }

        await using var connection = await OpenReadOnlyConnectionAsync(path, cancellationToken).ConfigureAwait(false);
        var taskCount = await CountTableRowsAsync(connection, "tasks", cancellationToken).ConfigureAwait(false);
        var projectCount = await CountTableRowsAsync(connection, "projects", cancellationToken).ConfigureAwait(false);
        var spaceCount = await CountTableRowsAsync(connection, "spaces", cancellationToken).ConfigureAwait(false);
        return new DatabaseSnapshot(taskCount, projectCount, spaceCount, BackupIntegrityStatuses.Valid);
    }

    private static Task<SqliteConnection> OpenReadOnlyConnectionAsync(string path, CancellationToken cancellationToken) =>
        OpenConnectionAsync(path, SqliteOpenMode.ReadOnly, cancellationToken);

    private static async Task<SqliteConnection> OpenConnectionAsync(string path, SqliteOpenMode mode, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<int> CountTableRowsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, tableName, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        return Convert.ToInt32(await ExecuteScalarAsync(connection, $"SELECT COUNT(*) FROM {tableName}", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
        command.Parameters.AddWithValue("@name", tableName);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return count > 0;
    }

    private static async Task<object> ExecuteScalarAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0;
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsSameDirectory(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool TryCopyMetadata(string sourcePath, string destinationPath)
    {
        var sourceMetadataPath = MetadataPath(sourcePath);
        if (!File.Exists(sourceMetadataPath))
        {
            return false;
        }

        try
        {
            File.Copy(sourceMetadataPath, MetadataPath(destinationPath), overwrite: false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string MetadataPath(string backupPath) => $"{backupPath}.json";

    private sealed record DatabaseSnapshot(int TaskCount, int ProjectCount, int SpaceCount, string IntegrityStatus);
}

public static class BackupReasons
{
    public const string Unknown = "unknown";
    public const string Manual = "manual";
    public const string Daily = "daily";
    public const string PreRestore = "pre-restore";
    public const string PreImport = "pre-import";
    public const string PreMigration = "pre-migration";
    public const string Legacy = "legacy";
}

public static class BackupIntegrityStatuses
{
    public const string Unknown = "unknown";
    public const string Valid = "valid";
    public const string Invalid = "invalid";
}

public sealed record BackupRetentionPolicy(int DailyBackups, int WeeklyBackups, int MonthlyBackups, int EventBackups)
{
    public static BackupRetentionPolicy Default { get; } = new(14, 8, 12, 20);
}

public sealed record BackupContext(string AppIdentity, string AppFlavor, string AppVersion)
{
    public static BackupContext Unknown { get; } = new("unknown", "unknown", string.Empty);
}

public sealed class BackupMetadata
{
    public string BackupPath { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string Reason { get; set; } = BackupReasons.Unknown;
    public string AppIdentity { get; set; } = string.Empty;
    public string AppFlavor { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public long DatabaseSizeBytes { get; set; }
    public int TaskCount { get; set; }
    public int ProjectCount { get; set; }
    public int SpaceCount { get; set; }
    public string IntegrityStatus { get; set; } = BackupIntegrityStatuses.Unknown;
    public string ContentHash { get; set; } = string.Empty;
}

public sealed class BackupInfo
{
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public long SizeBytes { get; set; }
    public string Reason { get; set; } = BackupReasons.Unknown;
    public int? TaskCount { get; set; }
    public int? ProjectCount { get; set; }
    public int? SpaceCount { get; set; }
    public string AppFlavor { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string IntegrityStatus { get; set; } = BackupIntegrityStatuses.Unknown;
    public bool HasMetadata { get; set; }

    public string DisplayName
    {
        get
        {
            var counts = TaskCount is null
                ? "counts unavailable"
                : $"{TaskCount} tasks, {ProjectCount} projects, {SpaceCount} spaces";
            return $"{CreatedAt:g} - {FormatReason(Reason)} - {counts} - {FormatSize(SizeBytes)}";
        }
    }

    private static string FormatReason(string reason) => reason switch
    {
        BackupReasons.Daily => "Daily",
        BackupReasons.Manual => "Manual",
        BackupReasons.PreRestore => "Before restore",
        BackupReasons.PreImport => "Before import",
        BackupReasons.PreMigration => "Before migration",
        BackupReasons.Legacy => "Legacy",
        _ => "Backup",
    };

    private static string FormatSize(long bytes) =>
        bytes < 1024 * 1024
            ? $"{Math.Max(1, bytes / 1024)} KB"
            : $"{bytes / 1024d / 1024d:0.0} MB";
}
