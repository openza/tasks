using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    public async Task<int> ReconcileUndeliverableProviderCommandsAsync(string instanceId, string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var read = connection.CreateCommand();
        read.CommandText = """
            SELECT c.task_id,c.requested_seq,
              CASE
                WHEN NOT EXISTS(SELECT 1 FROM personal_cloud_provider_sources s
                  WHERE s.instance_id=c.instance_id AND s.task_id=c.task_id) THEN 'missing_source'
                WHEN EXISTS(SELECT 1 FROM personal_cloud_provider_attempts a
                  WHERE a.instance_id=c.instance_id AND a.task_id=c.task_id
                    AND a.requested_seq=c.requested_seq AND a.state='cancelled') THEN 'cancelled_attempt'
                WHEN EXISTS(SELECT 1 FROM personal_cloud_outbox o
                  JOIN personal_cloud_initial_sync seed ON seed.instance_id=o.instance_id AND seed.device_id=o.device_id
                  WHERE o.instance_id=c.instance_id AND o.device_id=@d AND o.entity_id=c.task_id
                    AND o.operation='task.complete'
                    AND COALESCE(o.server_change_seq,(SELECT applied.change_seq FROM personal_cloud_applied_changes applied
                      WHERE applied.instance_id=o.instance_id AND applied.product='tasks'
                        AND applied.mutation_id=o.mutation_id AND applied.entity_id=o.entity_id))=c.requested_seq
                    AND seed.seed_end_sequence IS NOT NULL AND o.device_sequence<=seed.seed_end_sequence) THEN 'initial_seed'
              END
            FROM personal_cloud_provider_commands c
            WHERE c.instance_id=@i AND c.command='complete' AND c.state='pending_desktop'
              AND NOT EXISTS(SELECT 1 FROM personal_cloud_provider_attempts a
                WHERE a.instance_id=c.instance_id AND a.task_id=c.task_id
                  AND a.requested_seq=c.requested_seq
                  AND a.state IN ('claiming','reserved','starting','dispatching','uncertain','outcome'))
              AND (
                NOT EXISTS(SELECT 1 FROM personal_cloud_provider_sources s
                  WHERE s.instance_id=c.instance_id AND s.task_id=c.task_id)
                OR EXISTS(SELECT 1 FROM personal_cloud_provider_attempts a
                  WHERE a.instance_id=c.instance_id AND a.task_id=c.task_id
                    AND a.requested_seq=c.requested_seq AND a.state='cancelled')
                OR EXISTS(SELECT 1 FROM personal_cloud_outbox o
                  JOIN personal_cloud_initial_sync seed ON seed.instance_id=o.instance_id AND seed.device_id=o.device_id
                  WHERE o.instance_id=c.instance_id AND o.device_id=@d AND o.entity_id=c.task_id
                    AND o.operation='task.complete'
                    AND COALESCE(o.server_change_seq,(SELECT applied.change_seq FROM personal_cloud_applied_changes applied
                      WHERE applied.instance_id=o.instance_id AND applied.product='tasks'
                        AND applied.mutation_id=o.mutation_id AND applied.entity_id=o.entity_id))=c.requested_seq
                    AND seed.seed_end_sequence IS NOT NULL AND o.device_sequence<=seed.seed_end_sequence)
              )
            ORDER BY c.requested_seq,c.task_id
            """;
        read.Parameters.AddWithValue("@i", instanceId);
        read.Parameters.AddWithValue("@d", deviceId);
        var candidates = new List<(string TaskId, long RequestedSequence, string Reason)>();
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                if (!reader.IsDBNull(2)) candidates.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));

        foreach (var candidate in candidates)
        {
            var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE personal_cloud_provider_commands SET state='cancelled',error_code=@reason
                WHERE instance_id=@i AND task_id=@t AND requested_seq=@q AND state='pending_desktop'
                """;
            update.Parameters.AddWithValue("@i", instanceId);
            update.Parameters.AddWithValue("@t", candidate.TaskId);
            update.Parameters.AddWithValue("@q", candidate.RequestedSequence);
            update.Parameters.AddWithValue("@reason", candidate.Reason);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) continue;

            var cancelAttempts = connection.CreateCommand();
            cancelAttempts.CommandText = """
                UPDATE personal_cloud_provider_attempts SET state='cancelled'
                WHERE instance_id=@i AND task_id=@t AND requested_seq=@q AND state IN ('claiming','reserved')
                """;
            cancelAttempts.Parameters.AddWithValue("@i", instanceId);
            cancelAttempts.Parameters.AddWithValue("@t", candidate.TaskId);
            cancelAttempts.Parameters.AddWithValue("@q", candidate.RequestedSequence);
            await cancelAttempts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT OR IGNORE INTO personal_cloud_provider_command_cancellations
                    (instance_id,device_id,task_id,requested_seq,reason,created_at)
                VALUES(@i,@d,@t,@q,@reason,@created)
                """;
            insert.Parameters.AddWithValue("@i", instanceId);
            insert.Parameters.AddWithValue("@d", deviceId);
            insert.Parameters.AddWithValue("@t", candidate.TaskId);
            insert.Parameters.AddWithValue("@q", candidate.RequestedSequence);
            insert.Parameters.AddWithValue("@reason", candidate.Reason);
            insert.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return candidates.Count;
    }

    public async Task RetireDeferredPersonalCloudMutationAsync(string instanceId, string deviceId, string mutationId,
        string? blockedArtifactVersion, bool superseded = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var read = connection.CreateCommand();
        read.CommandText = "SELECT device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND mutation_id=@m AND acknowledged_at IS NULL";
        read.Parameters.AddWithValue("@i", instanceId);
        read.Parameters.AddWithValue("@d", deviceId);
        read.Parameters.AddWithValue("@m", mutationId);
        long sequence;
        string operation;
        string entityId;
        string canonicalJson;
        string canonicalHash;
        long createdAt;
        long? skippedEntityVersion = null;
        long? confirmedEntityVersion = null;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            sequence = reader.GetInt64(0);
            operation = reader.GetString(1);
            entityId = reader.GetString(2);
            canonicalJson = reader.GetString(3);
            canonicalHash = reader.GetString(4);
            createdAt = reader.GetInt64(5);
        }

        if (operation == PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation)
        {
            using var historical = System.Text.Json.JsonDocument.Parse(canonicalJson);
            if (historical.RootElement.TryGetProperty("baseEntityVersion", out var baseVersion) &&
                baseVersion.ValueKind == System.Text.Json.JsonValueKind.Number)
                skippedEntityVersion = baseVersion.GetInt64();
            var version = connection.CreateCommand();
            version.CommandText = "SELECT entity_version FROM personal_cloud_entity_versions WHERE instance_id=@i AND product='tasks' AND entity_id=@e";
            version.Parameters.AddWithValue("@i", instanceId);
            version.Parameters.AddWithValue("@e", entityId);
            var storedVersion = await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            confirmedEntityVersion = storedVersion is null or DBNull ? null : Convert.ToInt64(storedVersion);
            var defer = connection.CreateCommand();
            defer.CommandText = """
                INSERT OR IGNORE INTO personal_cloud_deferred_historical_completions
                    (instance_id,device_id,mutation_id,entity_id,canonical_json,canonical_hash,created_at,blocked_artifact_version)
                VALUES(@i,@d,@m,@e,@j,@h,@a,@v)
                """;
            defer.Parameters.AddWithValue("@i", instanceId);
            defer.Parameters.AddWithValue("@d", deviceId);
            defer.Parameters.AddWithValue("@m", mutationId);
            defer.Parameters.AddWithValue("@e", entityId);
            defer.Parameters.AddWithValue("@j", canonicalJson);
            defer.Parameters.AddWithValue("@h", canonicalHash);
            defer.Parameters.AddWithValue("@a", createdAt);
            defer.Parameters.AddWithValue("@v", (object?)blockedArtifactVersion ?? DBNull.Value);
            await defer.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (operation == PersonalCloudK0Protocol.ExternalCommandCancelOperation)
        {
            using var document = System.Text.Json.JsonDocument.Parse(canonicalJson);
            var requestedSequence = document.RootElement.GetProperty("payload").GetProperty("requestedChangeSeq").GetInt64();
            var cancellation = connection.CreateCommand();
            cancellation.CommandText = superseded
                ? "DELETE FROM personal_cloud_provider_command_cancellations WHERE instance_id=@i AND device_id=@d AND task_id=@t AND requested_seq=@q AND mutation_id=@m"
                : "UPDATE personal_cloud_provider_command_cancellations SET mutation_id=NULL,blocked_artifact_version=@v WHERE instance_id=@i AND device_id=@d AND task_id=@t AND requested_seq=@q AND mutation_id=@m";
            cancellation.Parameters.AddWithValue("@i", instanceId);
            cancellation.Parameters.AddWithValue("@d", deviceId);
            cancellation.Parameters.AddWithValue("@t", entityId);
            cancellation.Parameters.AddWithValue("@q", requestedSequence);
            cancellation.Parameters.AddWithValue("@m", mutationId);
            cancellation.Parameters.AddWithValue("@v", (object?)blockedArtifactVersion ?? DBNull.Value);
            await cancellation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
            throw new InvalidOperationException("Only historical imports and command cancellations can be retired to a deferred queue.");

        var later = new List<(string MutationId, long DeviceSequence, string CanonicalJson)>();
        var readLater = connection.CreateCommand();
        readLater.CommandText = "SELECT mutation_id,device_sequence,canonical_json FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND acknowledged_at IS NULL AND device_sequence>@q ORDER BY device_sequence";
        readLater.Parameters.AddWithValue("@i", instanceId);
        readLater.Parameters.AddWithValue("@d", deviceId);
        readLater.Parameters.AddWithValue("@q", sequence);
        await using (var reader = await readLater.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                later.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));

        var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND mutation_id=@m AND acknowledged_at IS NULL";
        delete.Parameters.AddWithValue("@i", instanceId);
        delete.Parameters.AddWithValue("@d", deviceId);
        delete.Parameters.AddWithValue("@m", mutationId);
        if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The deferred Personal Cloud mutation changed while it was being retired.");

        var adjustSeed = connection.CreateCommand();
        adjustSeed.CommandText = "UPDATE personal_cloud_initial_sync SET seed_end_sequence=seed_end_sequence-1 WHERE instance_id=@i AND device_id=@d AND seed_end_sequence>=@q";
        adjustSeed.Parameters.AddWithValue("@i", instanceId);
        adjustSeed.Parameters.AddWithValue("@d", deviceId);
        adjustSeed.Parameters.AddWithValue("@q", sequence);
        await adjustSeed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var item in later)
        {
            var nextSequence = checked(item.DeviceSequence - 1);
            var envelope = JsonNode.Parse(item.CanonicalJson)?.AsObject()
                ?? throw new InvalidOperationException("A queued Personal Cloud mutation has an invalid canonical envelope.");
            envelope["deviceSequence"] = nextSequence;
            if (skippedEntityVersion is { } skippedVersion &&
                (confirmedEntityVersion is null || confirmedEntityVersion <= skippedVersion) &&
                envelope["entityId"]?.GetValue<string>() == entityId &&
                envelope["baseEntityVersion"] is JsonValue queuedBase &&
                queuedBase.TryGetValue<long>(out var queuedVersion) && queuedVersion > skippedVersion)
                envelope["baseEntityVersion"] = queuedVersion - 1;
            var json = PersonalCloudK0Codec.Canonicalize(envelope.ToJsonString());
            var update = connection.CreateCommand();
            update.CommandText = "UPDATE personal_cloud_outbox SET device_sequence=@q,canonical_json=@j,canonical_hash=@h WHERE instance_id=@i AND device_id=@d AND mutation_id=@m AND acknowledged_at IS NULL";
            update.Parameters.AddWithValue("@q", nextSequence);
            update.Parameters.AddWithValue("@j", json);
            update.Parameters.AddWithValue("@h", PersonalCloudK0Codec.ComputeCanonicalHash(json));
            update.Parameters.AddWithValue("@i", instanceId);
            update.Parameters.AddWithValue("@d", deviceId);
            update.Parameters.AddWithValue("@m", item.MutationId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("A later queued Personal Cloud mutation changed while resequencing.");
        }

        var advance = connection.CreateCommand();
        advance.CommandText = "UPDATE personal_cloud_device_state SET next_device_sequence=next_device_sequence-1 WHERE instance_id=@i AND device_id=@d AND next_device_sequence>@q";
        advance.Parameters.AddWithValue("@i", instanceId);
        advance.Parameters.AddWithValue("@d", deviceId);
        advance.Parameters.AddWithValue("@q", sequence);
        await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PrepareDeferredHistoricalCompletionImportsAsync(string instanceId, string deviceId,
        string artifactVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var ensureDevice = connection.CreateCommand();
        ensureDevice.CommandText = "INSERT OR IGNORE INTO personal_cloud_device_state(instance_id,device_id,next_device_sequence) VALUES(@i,@d,1)";
        ensureDevice.Parameters.AddWithValue("@i", instanceId);
        ensureDevice.Parameters.AddWithValue("@d", deviceId);
        await ensureDevice.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var read = connection.CreateCommand();
        read.CommandText = "SELECT device_id,mutation_id,entity_id,canonical_json FROM personal_cloud_deferred_historical_completions WHERE instance_id=@i AND (blocked_artifact_version IS NULL OR blocked_artifact_version<>@v OR device_id<>@d) ORDER BY created_at,mutation_id";
        read.Parameters.AddWithValue("@i", instanceId);
        read.Parameters.AddWithValue("@d", deviceId);
        read.Parameters.AddWithValue("@v", artifactVersion);
        var rows = new List<(string DeviceId, string MutationId, string EntityId, string CanonicalJson)>();
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));

        foreach (var row in rows)
        {
            var pending = connection.CreateCommand();
            pending.CommandText = "SELECT EXISTS(SELECT 1 FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND entity_id=@e AND acknowledged_at IS NULL)";
            pending.Parameters.AddWithValue("@i", instanceId);
            pending.Parameters.AddWithValue("@d", deviceId);
            pending.Parameters.AddWithValue("@e", row.EntityId);
            if (Convert.ToInt64(await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0) continue;
            var baseVersion = await ReadPersonalCloudEntityVersionAsync(connection, instanceId, row.EntityId, cancellationToken).ConfigureAwait(false);
            if (baseVersion is null) continue;
            using var document = System.Text.Json.JsonDocument.Parse(row.CanonicalJson);
            var completedAt = document.RootElement.GetProperty("payload").GetProperty("completedAt").GetString();
            if (string.IsNullOrWhiteSpace(completedAt)) continue;
            await AppendWorkspaceMutationAsync(connection, instanceId, deviceId,
                PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation, row.EntityId, baseVersion,
                new { completedAt }, cancellationToken).ConfigureAwait(false);
            var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM personal_cloud_deferred_historical_completions WHERE instance_id=@i AND device_id=@oldDevice AND mutation_id=@m";
            delete.Parameters.AddWithValue("@i", instanceId);
            delete.Parameters.AddWithValue("@oldDevice", row.DeviceId);
            delete.Parameters.AddWithValue("@m", row.MutationId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasDeferredHistoricalCompletionImportsAsync(string instanceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM personal_cloud_deferred_historical_completions WHERE instance_id=@i)";
        command.Parameters.AddWithValue("@i", instanceId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    public async Task PrepareProviderCommandCancellationsAsync(string instanceId, string deviceId,
        string artifactVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var read = connection.CreateCommand();
        read.CommandText = """
            SELECT task_id,requested_seq,reason,blocked_artifact_version FROM personal_cloud_provider_command_cancellations
            WHERE instance_id=@i AND device_id=@d AND mutation_id IS NULL
            ORDER BY requested_seq,task_id
            """;
        read.Parameters.AddWithValue("@i", instanceId);
        read.Parameters.AddWithValue("@d", deviceId);
        read.Parameters.AddWithValue("@v", artifactVersion);
        var rows = new List<(string TaskId, long RequestedSequence, string Reason, string? BlockedArtifactVersion)>();
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));

        foreach (var row in rows)
        {
            var current = connection.CreateCommand();
            current.CommandText = """
                SELECT state FROM personal_cloud_provider_commands
                WHERE instance_id=@i AND task_id=@t AND requested_seq=@q AND state='cancelled'
                """;
            current.Parameters.AddWithValue("@i", instanceId);
            current.Parameters.AddWithValue("@t", row.TaskId);
            current.Parameters.AddWithValue("@q", row.RequestedSequence);
            if (await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string)
            {
                var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM personal_cloud_provider_command_cancellations WHERE instance_id=@i AND task_id=@t AND requested_seq=@q AND mutation_id IS NULL";
                delete.Parameters.AddWithValue("@i", instanceId);
                delete.Parameters.AddWithValue("@t", row.TaskId);
                delete.Parameters.AddWithValue("@q", row.RequestedSequence);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (row.BlockedArtifactVersion == artifactVersion) continue;

            var entry = await AppendWorkspaceMutationAsync(connection, instanceId, deviceId,
                PersonalCloudK0Protocol.ExternalCommandCancelOperation, row.TaskId, null,
                new { requestedChangeSeq = row.RequestedSequence, reason = row.Reason }, cancellationToken).ConfigureAwait(false);
            var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE personal_cloud_provider_command_cancellations
                SET mutation_id=@m,blocked_artifact_version=NULL
                WHERE instance_id=@i AND task_id=@t AND requested_seq=@q AND mutation_id IS NULL
                """;
            update.Parameters.AddWithValue("@m", entry.MutationId);
            update.Parameters.AddWithValue("@i", instanceId);
            update.Parameters.AddWithValue("@t", row.TaskId);
            update.Parameters.AddWithValue("@q", row.RequestedSequence);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsProviderCommandCancellationDeferredAsync(string instanceId, string mutationId,
        string artifactVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM personal_cloud_provider_command_cancellations
              WHERE instance_id=@i AND mutation_id=@m AND blocked_artifact_version=@v)
            """;
        command.Parameters.AddWithValue("@i", instanceId);
        command.Parameters.AddWithValue("@m", mutationId);
        command.Parameters.AddWithValue("@v", artifactVersion);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    public async Task<bool> IsPersonalCloudMutationDeferredAsync(string instanceId, string mutationId,
        string artifactVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var read = connection.CreateCommand();
        read.CommandText = "SELECT operation,entity_id,canonical_json,blocked_artifact_version FROM personal_cloud_outbox WHERE instance_id=@i AND mutation_id=@m";
        read.Parameters.AddWithValue("@i", instanceId);
        read.Parameters.AddWithValue("@m", mutationId);
        string? operation = null;
        string? entityId = null;
        string? canonical = null;
        string? blocked = null;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                operation = reader.GetString(0);
                entityId = reader.GetString(1);
                canonical = reader.GetString(2);
                blocked = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
        if (operation is null) return false;
        if (blocked == artifactVersion) return true;
        if (blocked is null) return false;

        if (operation == PersonalCloudK0Protocol.ExternalCommandCancelOperation && canonical is not null)
        {
            using var document = System.Text.Json.JsonDocument.Parse(canonical);
            var sequence = document.RootElement.GetProperty("payload").GetProperty("requestedChangeSeq").GetInt64();
            var current = connection.CreateCommand();
            current.CommandText = """
                SELECT state FROM personal_cloud_provider_commands
                WHERE instance_id=@i AND task_id=@t AND requested_seq=@q
                """;
            current.Parameters.AddWithValue("@i", instanceId);
            current.Parameters.AddWithValue("@t", entityId);
            current.Parameters.AddWithValue("@q", sequence);
            if (await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string state && state == "pending_desktop")
                return false;
        }
        var clear = connection.CreateCommand();
        clear.CommandText = "UPDATE personal_cloud_outbox SET blocked_artifact_version=NULL WHERE instance_id=@i AND mutation_id=@m";
        clear.Parameters.AddWithValue("@i", instanceId);
        clear.Parameters.AddWithValue("@m", mutationId);
        await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    public async Task DeferPersonalCloudMutationAsync(string instanceId, string mutationId, string artifactVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE personal_cloud_outbox SET blocked_artifact_version=@v WHERE instance_id=@i AND mutation_id=@m";
        command.Parameters.AddWithValue("@v", artifactVersion);
        command.Parameters.AddWithValue("@i", instanceId);
        command.Parameters.AddWithValue("@m", mutationId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var cancellation = connection.CreateCommand();
        cancellation.CommandText = "UPDATE personal_cloud_provider_command_cancellations SET blocked_artifact_version=@v WHERE instance_id=@i AND mutation_id=@m";
        cancellation.Parameters.AddWithValue("@v", artifactVersion);
        cancellation.Parameters.AddWithValue("@i", instanceId);
        cancellation.Parameters.AddWithValue("@m", mutationId);
        await cancellation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeferProviderCommandCancellationAsync(string instanceId, string mutationId,
        string artifactVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE personal_cloud_provider_command_cancellations SET blocked_artifact_version=@v WHERE instance_id=@i AND mutation_id=@m";
        command.Parameters.AddWithValue("@v", artifactVersion);
        command.Parameters.AddWithValue("@i", instanceId);
        command.Parameters.AddWithValue("@m", mutationId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasDeferredProviderCommandCancellationsAsync(string instanceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM personal_cloud_provider_command_cancellations WHERE instance_id=@i)";
        command.Parameters.AddWithValue("@i", instanceId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }
}
