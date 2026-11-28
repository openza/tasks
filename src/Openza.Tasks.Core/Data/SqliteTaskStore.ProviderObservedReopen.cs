using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    private static async Task RecordProviderStateConfirmationAsync(SqliteConnection connection, string? instanceId,
        string taskId, string confirmationId, long? requestedChangeSeq, DateTimeOffset confirmedAt,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO provider_completion_confirmations(confirmation_id,instance_id,task_id,requested_change_seq,confirmed_at)
            VALUES(@m,@i,@t,@q,@a)
            ON CONFLICT(confirmation_id) DO UPDATE SET confirmed_at=MAX(confirmed_at,excluded.confirmed_at)
            """;
        command.Parameters.AddWithValue("@m", confirmationId);
        command.Parameters.AddWithValue("@i", ToDbValue(instanceId));
        command.Parameters.AddWithValue("@t", taskId);
        command.Parameters.AddWithValue("@q", requestedChangeSeq is { } sequence ? sequence : DBNull.Value);
        command.Parameters.AddWithValue("@a", confirmedAt.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DateTimeOffset?> GetProviderStateConfirmedAtAsync(SqliteConnection connection, string taskId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(f.confirmed_at AS TEXT)
            FROM provider_completion_confirmations f
            WHERE f.task_id=@t AND f.instance_id IS NULL
              AND NOT EXISTS(SELECT 1 FROM personal_cloud_outbox latest
                WHERE latest.entity_id=f.task_id AND latest.operation IN ('task.complete','task.reopen')
                  AND latest.local_sequence=(SELECT MAX(o.local_sequence) FROM personal_cloud_outbox o
                    WHERE o.entity_id=f.task_id AND o.operation IN ('task.complete','task.reopen')))
            UNION ALL
            SELECT CAST(f.confirmed_at AS TEXT)
            FROM provider_completion_confirmations f
            JOIN personal_cloud_provider_commands c ON c.instance_id=f.instance_id AND c.task_id=f.task_id
            JOIN personal_cloud_outbox result ON result.mutation_id=f.confirmation_id
              AND result.instance_id=f.instance_id AND result.entity_id=f.task_id
              AND result.operation='externalCommand.confirm' AND result.acknowledged_at IS NOT NULL
              AND CAST(json_extract(result.canonical_json,'$.payload.requestedChangeSeq') AS INTEGER)=c.requested_seq
            WHERE f.task_id=@t AND c.command='complete' AND c.state='confirmed'
              AND f.requested_change_seq=c.requested_seq
              AND NOT EXISTS(SELECT 1 FROM personal_cloud_outbox intent
                WHERE intent.instance_id=c.instance_id AND intent.entity_id=c.task_id
                  AND intent.operation IN ('task.complete','task.reopen')
                  AND intent.local_sequence=(SELECT MAX(o.local_sequence) FROM personal_cloud_outbox o
                    WHERE o.instance_id=c.instance_id AND o.entity_id=c.task_id
                      AND o.operation IN ('task.complete','task.reopen'))
                  AND (intent.acknowledged_at IS NULL
                    OR (intent.server_change_seq IS NOT NULL AND intent.server_change_seq>c.requested_seq)
                    OR (intent.server_change_seq IS NULL AND intent.local_sequence>=result.local_sequence)))
            UNION ALL
            SELECT json_extract(result.canonical_json,'$.payload.resolvedAt')
            FROM personal_cloud_provider_commands c
            JOIN personal_cloud_outbox result ON result.instance_id=c.instance_id AND result.entity_id=c.task_id
              AND result.operation='externalCommand.confirm'
              AND result.acknowledged_at IS NOT NULL
              AND CAST(json_extract(result.canonical_json,'$.payload.requestedChangeSeq') AS INTEGER)=c.requested_seq
            WHERE c.task_id=@t AND c.command='complete' AND c.state='confirmed'
              AND NOT EXISTS(SELECT 1 FROM personal_cloud_outbox intent
                WHERE intent.instance_id=c.instance_id AND intent.entity_id=c.task_id
                  AND intent.operation IN ('task.complete','task.reopen')
                  AND intent.local_sequence=(SELECT MAX(o.local_sequence) FROM personal_cloud_outbox o
                    WHERE o.instance_id=c.instance_id AND o.entity_id=c.task_id
                      AND o.operation IN ('task.complete','task.reopen'))
                  AND (intent.acknowledged_at IS NULL
                    OR (intent.server_change_seq IS NOT NULL AND intent.server_change_seq>c.requested_seq)
                    OR (intent.server_change_seq IS NULL AND intent.local_sequence>=result.local_sequence)))
            """;
        command.Parameters.AddWithValue("@t", taskId);
        var confirmations = new List<DateTimeOffset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0)) continue;
            var value = reader.GetString(0);
            if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds))
                confirmations.Add(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
            else if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                confirmations.Add(parsed);
        }
        return confirmations.Count == 0 ? null : confirmations.Max();
    }

    private static async Task<bool> IsProviderCompletionIntentInFlightAsync(SqliteConnection connection, string taskId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM pending_completions WHERE task_id=@t)
                OR EXISTS(SELECT 1 FROM personal_cloud_outbox o WHERE o.entity_id=@t
                    AND o.operation IN ('task.complete','task.reopen') AND o.acknowledged_at IS NULL
                    AND NOT (o.operation='task.complete' AND EXISTS(
                      SELECT 1 FROM personal_cloud_initial_sync seed
                      WHERE seed.instance_id=o.instance_id AND seed.device_id=o.device_id
                        AND seed.seed_end_sequence IS NOT NULL AND o.device_sequence<=seed.seed_end_sequence)))
                OR EXISTS(SELECT 1 FROM personal_cloud_provider_commands c
                    JOIN personal_cloud_provider_sources s ON s.instance_id=c.instance_id AND s.task_id=c.task_id
                    WHERE c.task_id=@t AND c.command='complete' AND c.state='pending_desktop'
                      AND NOT EXISTS(SELECT 1 FROM personal_cloud_provider_attempts a
                        WHERE a.instance_id=c.instance_id AND a.task_id=c.task_id
                          AND a.requested_seq=c.requested_seq AND a.state='cancelled')
                      AND NOT EXISTS(SELECT 1 FROM personal_cloud_outbox o
                        JOIN personal_cloud_initial_sync seed ON seed.instance_id=o.instance_id AND seed.device_id=o.device_id
                        WHERE o.instance_id=c.instance_id AND o.entity_id=c.task_id AND o.operation='task.complete'
                          AND o.acknowledged_at IS NOT NULL AND seed.seed_end_sequence IS NOT NULL
                          AND o.device_sequence<=seed.seed_end_sequence
                          AND o.server_change_seq=c.requested_seq)
                OR EXISTS(
                    SELECT 1 FROM personal_cloud_provider_attempts a
                    JOIN personal_cloud_provider_commands c ON c.instance_id=a.instance_id AND c.task_id=a.task_id
                      AND c.requested_seq=a.requested_seq
                    WHERE a.task_id=@t AND c.command='complete' AND c.state='pending_desktop'
                      AND a.state IN ('claiming','reserved','starting','dispatching','uncertain','outcome'))
                OR EXISTS(
                    SELECT 1 FROM personal_cloud_outbox o
                    JOIN personal_cloud_provider_sources s ON s.instance_id=o.instance_id AND s.task_id=o.entity_id
                    WHERE o.entity_id=@t AND o.operation IN ('task.complete','task.reopen') AND o.acknowledged_at IS NOT NULL
                      AND NOT (o.operation='task.complete' AND EXISTS(
                        SELECT 1 FROM personal_cloud_initial_sync seed
                        JOIN personal_cloud_provider_commands seeded ON seeded.instance_id=o.instance_id
                          AND seeded.task_id=o.entity_id AND seeded.command='complete' AND seeded.requested_seq=o.server_change_seq
                        WHERE seed.instance_id=o.instance_id AND seed.device_id=o.device_id
                          AND seed.seed_end_sequence IS NOT NULL AND o.device_sequence<=seed.seed_end_sequence))
                      AND o.local_sequence=(SELECT MAX(latest.local_sequence) FROM personal_cloud_outbox latest
                        WHERE latest.instance_id=o.instance_id AND latest.entity_id=o.entity_id
                          AND latest.operation IN ('task.complete','task.reopen') AND latest.acknowledged_at IS NOT NULL)
                      AND (
                        NOT EXISTS(SELECT 1 FROM personal_cloud_provider_commands c
                          WHERE c.instance_id=o.instance_id AND c.task_id=o.entity_id)
                        OR EXISTS(
                        SELECT 1 FROM personal_cloud_provider_commands c
                        WHERE c.instance_id=o.instance_id AND c.task_id=o.entity_id
                          AND ((o.server_change_seq IS NOT NULL AND c.requested_seq=o.server_change_seq)
                            OR (o.server_change_seq IS NULL AND c.command=CASE o.operation WHEN 'task.complete' THEN 'complete' ELSE 'reopen' END))
                          AND c.state NOT IN ('confirmed','rejected','cancelled')))
                ))
            """;
        command.Parameters.AddWithValue("@t", taskId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    private static async Task QueueProviderObservedReopenAsync(SqliteConnection connection, string taskId,
        DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        var read = connection.CreateCommand();
        read.CommandText = "SELECT instance_id FROM personal_cloud_provider_sources WHERE task_id=@t";
        read.Parameters.AddWithValue("@t", taskId);
        var instances = new List<string>();
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) instances.Add(reader.GetString(0));
        foreach (var instanceId in instances)
        {
            var observationId = $"mutation_{Guid.NewGuid():N}";
            var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO personal_cloud_provider_observed_reopens(instance_id,task_id,observation_id,observed_at) VALUES(@i,@t,@m,@a)";
            insert.Parameters.AddWithValue("@i", instanceId);
            insert.Parameters.AddWithValue("@t", taskId);
            insert.Parameters.AddWithValue("@m", observationId);
            insert.Parameters.AddWithValue("@a", observedAt.ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SupersedeUnmaterializedProviderObservedReopensAsync(SqliteConnection connection,
        string taskId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM personal_cloud_provider_observed_reopens
            WHERE task_id=@t AND NOT EXISTS(
              SELECT 1 FROM personal_cloud_outbox o WHERE o.mutation_id=personal_cloud_provider_observed_reopens.observation_id)
            """;
        command.Parameters.AddWithValue("@t", taskId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PrepareProviderObservedReopensAsync(string instanceId, string deviceId, string artifactVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var ensureDevice = connection.CreateCommand();
        ensureDevice.CommandText = "INSERT OR IGNORE INTO personal_cloud_device_state(instance_id,device_id,next_device_sequence) VALUES(@i,@d,1)";
        ensureDevice.Parameters.AddWithValue("@i", instanceId); ensureDevice.Parameters.AddWithValue("@d", deviceId);
        await ensureDevice.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var read = connection.CreateCommand();
        read.CommandText = "SELECT q.task_id,q.observation_id,q.observed_at FROM personal_cloud_provider_observed_reopens q WHERE q.instance_id=@i AND (q.blocked_artifact_version IS NULL OR q.blocked_artifact_version<>@v) AND NOT EXISTS(SELECT 1 FROM personal_cloud_outbox o WHERE o.mutation_id=q.observation_id) ORDER BY q.observed_at,q.observation_id";
        read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@v", artifactVersion);
        var entries = new List<(string TaskId, string Id, long ObservedAt)>();
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) entries.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        var sequenceCommand = connection.CreateCommand();
        sequenceCommand.CommandText = """
            SELECT MAX(d.next_device_sequence,COALESCE((SELECT MAX(o.device_sequence)+1 FROM personal_cloud_outbox o
              WHERE o.instance_id=d.instance_id AND o.device_id=d.device_id),1))
            FROM personal_cloud_device_state d WHERE d.instance_id=@i AND d.device_id=@d
            """;
        sequenceCommand.Parameters.AddWithValue("@i", instanceId); sequenceCommand.Parameters.AddWithValue("@d", deviceId);
        var sequence = Convert.ToInt64(await sequenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        foreach (var entry in entries)
        {
            var state = connection.CreateCommand();
            state.CommandText = "SELECT completion_state FROM tasks WHERE id=@t";
            state.Parameters.AddWithValue("@t", entry.TaskId);
            var localState = (string?)await state.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var confirmedAt = await GetProviderStateConfirmedAtAsync(connection, entry.TaskId, cancellationToken).ConfigureAwait(false);
            var inFlight = await IsProviderCompletionIntentInFlightAsync(connection, entry.TaskId, cancellationToken).ConfigureAwait(false);
            if (localState != "open" || inFlight || confirmedAt is null || confirmedAt.Value.ToUnixTimeMilliseconds() >= entry.ObservedAt)
            {
                var superseded = connection.CreateCommand();
                superseded.CommandText = "DELETE FROM personal_cloud_provider_observed_reopens WHERE instance_id=@i AND observation_id=@m";
                superseded.Parameters.AddWithValue("@i", instanceId); superseded.Parameters.AddWithValue("@m", entry.Id);
                await superseded.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            var entityVersion = await ReadPersonalCloudEntityVersionAsync(connection, instanceId, entry.TaskId, cancellationToken).ConfigureAwait(false);
            if (entityVersion is null) continue;
            var createdAt = DateTimeOffset.UtcNow;
            var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(entry.ObservedAt).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            var mutation = PersonalCloudProviderObservedReopenCodec.CreateMutation(instanceId, deviceId, entry.Id, sequence,
                entry.TaskId, entityVersion.Value, observedAt,
                createdAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
            var json = PersonalCloudK0Codec.Serialize(mutation);
            var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO personal_cloud_outbox(mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at) VALUES(@m,'tasks',@i,@d,@q,@o,@e,@j,@h,@a)";
            insert.Parameters.AddWithValue("@m", entry.Id); insert.Parameters.AddWithValue("@i", instanceId); insert.Parameters.AddWithValue("@d", deviceId);
            insert.Parameters.AddWithValue("@q", sequence); insert.Parameters.AddWithValue("@o", PersonalCloudK0Protocol.ProviderObservedReopenOperation);
            insert.Parameters.AddWithValue("@e", entry.TaskId); insert.Parameters.AddWithValue("@j", json);
            insert.Parameters.AddWithValue("@h", PersonalCloudK0Codec.ComputeCanonicalHash(json)); insert.Parameters.AddWithValue("@a", createdAt.ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sequence++;
        }
        var updateSequence = connection.CreateCommand();
        updateSequence.CommandText = "UPDATE personal_cloud_device_state SET next_device_sequence=@q WHERE instance_id=@i AND device_id=@d";
        updateSequence.Parameters.AddWithValue("@q", sequence); updateSequence.Parameters.AddWithValue("@i", instanceId); updateSequence.Parameters.AddWithValue("@d", deviceId);
        await updateSequence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeferRejectedProviderObservedReopenAsync(string instanceId, string deviceId, string mutationId,
        string artifactVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var read = connection.CreateCommand();
        read.CommandText = "SELECT local_sequence,device_sequence,entity_id FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND mutation_id=@m AND operation=@o AND acknowledged_at IS NULL";
        read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@d", deviceId); read.Parameters.AddWithValue("@m", mutationId);
        read.Parameters.AddWithValue("@o", PersonalCloudK0Protocol.ProviderObservedReopenOperation);
        (long Local, long Sequence, string TaskId)? rejected = null;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rejected = (reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2));
        if (rejected is null) return false;
        var tailRead = connection.CreateCommand();
        tailRead.CommandText = "SELECT local_sequence,mutation_id,device_sequence,operation,entity_id,canonical_json,created_at FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND acknowledged_at IS NULL AND device_sequence>=@q ORDER BY device_sequence";
        tailRead.Parameters.AddWithValue("@i", instanceId); tailRead.Parameters.AddWithValue("@d", deviceId); tailRead.Parameters.AddWithValue("@q", rejected.Value.Sequence);
        var tail = new List<(long Local,string Id,long Sequence,string Operation,string Entity,string Json,long Created)>();
        await using (var reader = await tailRead.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tail.Add((reader.GetInt64(0),reader.GetString(1),reader.GetInt64(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetInt64(6)));
        var blocked = connection.CreateCommand();
        blocked.CommandText = "UPDATE personal_cloud_provider_observed_reopens SET blocked_artifact_version=@v WHERE instance_id=@i AND observation_id=@m";
        blocked.Parameters.AddWithValue("@v", artifactVersion); blocked.Parameters.AddWithValue("@i", instanceId); blocked.Parameters.AddWithValue("@m", mutationId);
        await blocked.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var seed = connection.CreateCommand();
        seed.CommandText = "UPDATE personal_cloud_initial_sync SET seed_end_sequence=seed_end_sequence-1 WHERE instance_id=@i AND device_id=@d AND seed_end_sequence>=@q";
        seed.Parameters.AddWithValue("@i", instanceId); seed.Parameters.AddWithValue("@d", deviceId); seed.Parameters.AddWithValue("@q", rejected.Value.Sequence);
        await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND acknowledged_at IS NULL AND device_sequence>=@q";
        delete.Parameters.AddWithValue("@i", instanceId); delete.Parameters.AddWithValue("@d", deviceId); delete.Parameters.AddWithValue("@q", rejected.Value.Sequence);
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var next = rejected.Value.Sequence;
        foreach (var row in tail.Where(r => r.Id != mutationId))
        {
            var jsonNode = JsonNode.Parse(row.Json)!.AsObject();
            var newId = $"mutation_{Guid.NewGuid():N}";
            jsonNode["mutationId"] = newId; jsonNode["deviceSequence"] = next;
            var json = PersonalCloudK0Codec.Canonicalize(jsonNode.ToJsonString());
            var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO personal_cloud_outbox(local_sequence,mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at) VALUES(@l,@m,'tasks',@i,@d,@q,@o,@e,@j,@h,@a)";
            insert.Parameters.AddWithValue("@l", row.Local); insert.Parameters.AddWithValue("@m", newId); insert.Parameters.AddWithValue("@i", instanceId); insert.Parameters.AddWithValue("@d", deviceId);
            insert.Parameters.AddWithValue("@q", next); insert.Parameters.AddWithValue("@o", row.Operation); insert.Parameters.AddWithValue("@e", row.Entity);
            insert.Parameters.AddWithValue("@j", json); insert.Parameters.AddWithValue("@h", PersonalCloudK0Codec.ComputeCanonicalHash(json)); insert.Parameters.AddWithValue("@a", row.Created);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var indirect = connection.CreateCommand(); indirect.CommandText = "UPDATE personal_cloud_workspace_indirect_versions SET mutation_id=@n WHERE mutation_id=@m";
            indirect.Parameters.AddWithValue("@n", newId); indirect.Parameters.AddWithValue("@m", row.Id); await indirect.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var attempt = connection.CreateCommand(); attempt.CommandText = "UPDATE personal_cloud_provider_attempts SET result_mutation_id=@n WHERE result_mutation_id=@m";
            attempt.Parameters.AddWithValue("@n", newId); attempt.Parameters.AddWithValue("@m", row.Id); await attempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var queue = connection.CreateCommand(); queue.CommandText = "UPDATE personal_cloud_provider_observed_reopens SET observation_id=@n WHERE instance_id=@i AND observation_id=@m";
            queue.Parameters.AddWithValue("@n", newId); queue.Parameters.AddWithValue("@i", instanceId); queue.Parameters.AddWithValue("@m", row.Id); await queue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            next++;
        }
        var updateSequence = connection.CreateCommand(); updateSequence.CommandText = "UPDATE personal_cloud_device_state SET next_device_sequence=@q WHERE instance_id=@i AND device_id=@d";
        updateSequence.Parameters.AddWithValue("@q", next); updateSequence.Parameters.AddWithValue("@i", instanceId); updateSequence.Parameters.AddWithValue("@d", deviceId);
        await updateSequence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
