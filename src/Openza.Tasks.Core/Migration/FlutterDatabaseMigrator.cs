using Microsoft.Data.Sqlite;

namespace Openza.Tasks.Core.Migration;

public sealed class FlutterDatabaseMigrator(string legacyDatabasePath, string targetDatabasePath)
{
    public string LegacyDatabasePath { get; } = legacyDatabasePath;
    public string TargetDatabasePath { get; } = targetDatabasePath;

    public Task<MigrationResult> MigrateIfNeededAsync(CancellationToken cancellationToken = default) =>
        MigrateIfNeededAsync([LegacyDatabasePath], cancellationToken);

    public async Task<MigrationResult> MigrateIfNeededAsync(IReadOnlyList<string> legacyDatabaseCandidates, CancellationToken cancellationToken = default)
    {
        var existingCandidates = legacyDatabaseCandidates.Where(File.Exists).ToList();
        if (existingCandidates.Count == 0)
        {
            return MigrationResult.Skipped("Legacy Flutter database was not found.");
        }

        var legacyDatabasePath = await FindFirstDatabaseWithTasksAsync(existingCandidates, cancellationToken).ConfigureAwait(false);
        if (legacyDatabasePath is null)
        {
            return MigrationResult.Skipped("Legacy Flutter database does not contain local tasks.");
        }

        if (File.Exists(TargetDatabasePath) && !await IsEmptySeedDatabaseAsync(TargetDatabasePath, cancellationToken).ConfigureAwait(false))
        {
            return MigrationResult.Skipped("Target database already contains user data.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(TargetDatabasePath) ?? ".");
        var backupPath = $"{legacyDatabasePath}.pre-winui-migration-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
        File.Copy(legacyDatabasePath, backupPath, overwrite: false);

        if (File.Exists(TargetDatabasePath))
        {
            var existingTargetBackup = $"{TargetDatabasePath}.empty-before-legacy-migration-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
            File.Copy(TargetDatabasePath, existingTargetBackup, overwrite: false);
            File.Delete(TargetDatabasePath);
        }

        await TryCheckpointWalAsync(legacyDatabasePath, cancellationToken).ConfigureAwait(false);
        File.Copy(legacyDatabasePath, TargetDatabasePath, overwrite: false);

        CopySidecarIfExists($"{legacyDatabasePath}-wal", $"{TargetDatabasePath}-wal");
        CopySidecarIfExists($"{legacyDatabasePath}-shm", $"{TargetDatabasePath}-shm");

        return MigrationResult.Migrated(legacyDatabasePath, backupPath);
    }

    private static async Task TryCheckpointWalAsync(string databasePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // The current app uses DELETE journal mode, but old installs may not.
            // If checkpointing fails because the file is locked, preserve the main DB
            // and sidecar files rather than blocking startup.
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CopySidecarIfExists(string source, string target)
    {
        if (File.Exists(source))
        {
            File.Copy(source, target, overwrite: false);
        }
    }

    private static async Task<bool> IsEmptySeedDatabaseAsync(string databasePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM tasks";
            var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
            return count == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ContainsTasksAsync(string databasePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM tasks";
            var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
            return count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> FindFirstDatabaseWithTasksAsync(IEnumerable<string> databasePaths, CancellationToken cancellationToken)
    {
        foreach (var databasePath in databasePaths)
        {
            if (await ContainsTasksAsync(databasePath, cancellationToken).ConfigureAwait(false))
            {
                return databasePath;
            }
        }

        return null;
    }
}

public sealed record MigrationResult(bool WasMigrated, string Message, string? BackupPath, string? SourcePath)
{
    public static MigrationResult Migrated(string sourcePath, string backupPath) =>
        new(true, $"Migrated the legacy Flutter database from {sourcePath}.", backupPath, sourcePath);

    public static MigrationResult Skipped(string message) =>
        new(false, message, null, null);
}
