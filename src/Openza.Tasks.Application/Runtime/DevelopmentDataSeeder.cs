using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Openza.Tasks.Application.Runtime;

public static class DevelopmentDataSeeder
{
    public static async Task SeedFromProductionAsync(
        OpenzaChannel targetChannel,
        bool replace,
        CancellationToken cancellationToken = default)
    {
        if (targetChannel == OpenzaChannel.Production)
        {
            throw new ArgumentException("Production cannot be a development seed target.", nameof(targetChannel));
        }

        var source = OpenzaRuntimeContext.Create(OpenzaChannel.Production);
        var target = OpenzaRuntimeContext.Create(targetChannel);
        await SeedAsync(source, target, replace, cancellationToken).ConfigureAwait(false);
    }

    public static async Task SeedAsync(
        OpenzaRuntimeContext source,
        OpenzaRuntimeContext target,
        bool replace,
        CancellationToken cancellationToken = default)
    {
        if (source.Channel != OpenzaChannel.Production || target.Channel == OpenzaChannel.Production)
        {
            throw new ArgumentException("Development seeding requires a Production source and a non-Production target.");
        }
        if (!File.Exists(source.DatabasePath))
        {
            throw new FileNotFoundException("The Production database was not found.", source.DatabasePath);
        }
        if (File.Exists(target.DatabasePath) && !replace)
        {
            throw new InvalidOperationException($"{target.DisplayName} already has data. Pass --replace after reviewing the target.");
        }

        using var lease = ChannelRuntimeLease.AcquireExclusive(target);
        PrivateFilePermissions.EnsureOwnedDirectory(target.DataDirectory);
        var tempPath = Path.Combine(target.DataDirectory, $".seed-{Guid.NewGuid():N}.db");
        try
        {
            await using (var sourceConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = source.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()))
            await using (var targetConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString()))
            {
                await sourceConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await targetConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                sourceConnection.BackupDatabase(targetConnection);
            }

            await ScrubRemoteWriteStateAsync(tempPath, cancellationToken).ConfigureAwait(false);
            ValidateIntegrity(tempPath);
            await DisableAutomaticSyncAsync(target.SettingsPath, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, target.DatabasePath, overwrite: true);
            File.Delete(target.DatabasePath + "-wal");
            File.Delete(target.DatabasePath + "-shm");
            PrivateFilePermissions.EnsureFile(target.DatabasePath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task DisableAutomaticSyncAsync(string settingsPath, CancellationToken cancellationToken)
    {
        JsonObject settings;
        try
        {
            settings = File.Exists(settingsPath)
                ? JsonNode.Parse(await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false)) as JsonObject ?? []
                : [];
        }
        catch (JsonException)
        {
            settings = [];
        }

        settings["AutomaticSyncEnabled"] = false;
        var tempPath = settingsPath + $".seed-{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);
            PrivateFilePermissions.EnsureFile(tempPath);
            File.Move(tempPath, settingsPath, overwrite: true);
            PrivateFilePermissions.EnsureFile(settingsPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task ScrubRemoteWriteStateAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE integrations
            SET is_active = CASE WHEN id = 'openza_tasks' THEN 1 ELSE 0 END,
                is_configured = CASE WHEN id = 'openza_tasks' THEN 1 ELSE 0 END,
                last_sync_at = NULL,
                sync_token = NULL;
            UPDATE provider_connections
            SET status = CASE WHEN integration_id = 'openza_tasks' THEN 'connected' ELSE 'disconnected' END,
                last_sync_at = NULL;
            UPDATE sync_routes SET is_enabled = 0;
            DELETE FROM pending_completions;
            DELETE FROM pending_task_date_updates;
            DELETE FROM sync_operations WHERE status = 'pending';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateIntegrity(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The seeded database failed SQLite integrity validation.");
        }
    }
}
