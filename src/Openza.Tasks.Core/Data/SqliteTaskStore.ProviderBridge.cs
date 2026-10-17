using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed record PersonalCloudProviderCommand(string TaskId, long RequestedChangeSeq, string Command,
    string State, string? ErrorCode, K0SafeSourceDescriptor Source);

public sealed record PersonalCloudProviderAttempt(string ClaimId, string TaskId, long RequestedChangeSeq,
    string DeviceId, string AccountBinding, string Command, string State, K0SafeSourceDescriptor Source);

public sealed record PersonalCloudProviderRequestStatus(string TaskId, string Title, string Status, bool CanRetry);

public sealed partial class SqliteTaskStore
{
    // Preserve factual cloud freshness with at most one unchanged full snapshot every six hours.
    private static readonly TimeSpan SourceSnapshotHeartbeatInterval = TimeSpan.FromHours(6);
    public async Task<bool> IsPersonalCloudCompletionManagedAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM personal_cloud_outbox WHERE entity_id=@t AND operation IN ('task.complete','task.reopen'))
              OR EXISTS(SELECT 1 FROM personal_cloud_provider_commands WHERE task_id=@t)
            """;
        command.Parameters.AddWithValue("@t", taskId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }
    public async Task<IReadOnlyList<PersonalCloudProviderRequestStatus>> GetPersonalCloudProviderRequestStatusAsync(
        string instanceId, CancellationToken cancellationToken = default)
    {
        var bindings = await GetPersonalCloudProviderSourcesAsync(instanceId, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.task_id,COALESCE(t.title,c.task_id),c.command,c.state,c.error_code,
              (SELECT a.state FROM personal_cloud_provider_attempts a WHERE a.instance_id=c.instance_id AND a.task_id=c.task_id
               AND a.state NOT IN ('settled','cancelled') ORDER BY a.requested_seq LIMIT 1)
            FROM personal_cloud_provider_commands c LEFT JOIN tasks t ON t.id=c.task_id
            JOIN personal_cloud_provider_sources s ON s.instance_id=c.instance_id AND s.task_id=c.task_id
            WHERE c.instance_id=@i AND (c.state IN ('pending_desktop','rejected') OR EXISTS(
              SELECT 1 FROM personal_cloud_provider_attempts a WHERE a.instance_id=c.instance_id AND a.task_id=c.task_id
              AND a.state NOT IN ('settled','cancelled')))
            ORDER BY c.requested_seq LIMIT 100
            """;
        command.Parameters.AddWithValue("@i", instanceId);
        var result = new List<PersonalCloudProviderRequestStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = reader.GetString(3);
            var attempt = reader.IsDBNull(5) ? null : reader.GetString(5);
            var source = bindings.FirstOrDefault(value => value.TaskId == reader.GetString(0)).Source;
            var ambiguous = source is not null && bindings.Count(value => value.Source.IntegrationKind == source.IntegrationKind &&
                value.Source.ExternalIdHash == source.ExternalIdHash) > 1;
            var status = attempt is "starting" or "dispatching" or "uncertain"
                ? "Outcome uncertain. Stop and revoke the old desktop, check provider state, then reconcile in Personal Cloud. Automatic retry is blocked."
                : attempt == "outcome" ? "Provider result saved locally; awaiting cloud sync."
                : ambiguous ? "Multiple Openza tasks reference this provider item. Delivery is blocked."
                : state == "rejected" ? $"Rejected ({(reader.IsDBNull(4) ? "provider_rejected" : reader.GetString(4))})."
                : $"{reader.GetString(2)} · {state}";
            result.Add(new(reader.GetString(0), reader.GetString(1), status, state == "rejected" && attempt is null && !ambiguous));
        }
        foreach (var intent in await GetPersonalCloudWorkspaceRecordsAsync(instanceId, "intents", cancellationToken).ConfigureAwait(false))
        {
            var state = WText(intent, "state");
            if (state is "confirmed" or "rejected") continue;
            var id = WText(intent, "id")!;
            var action = await GetPersonalCloudCatalogActionAsync(instanceId, id, cancellationToken).ConfigureAwait(false);
            var status = action?.Phase == "result_queued" ? "Local result saved; awaiting cloud sync."
                : state == "uncertain" || action?.Phase is "dispatching" or "uncertain" ? "Outcome uncertain; reconcile the provider state before another action."
                : state == "pending_desktop" ? "Pending Desktop"
                : "Started on the owning Desktop";
            result.Add(new(id, WText(intent, "action")!, status, false));
        }
        return result;
    }
    public async Task<IReadOnlyList<(string TaskId, K0SafeSourceDescriptor Source)>> GetPersonalCloudProviderSourcesAsync(
        string instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.task_id,s.source_json FROM personal_cloud_provider_sources s
            WHERE s.instance_id=@i AND NOT EXISTS(SELECT 1 FROM personal_cloud_tombstones t
              WHERE t.instance_id=s.instance_id AND t.entity_id=s.task_id)
              AND NOT EXISTS(SELECT 1 FROM personal_cloud_outbox o WHERE o.instance_id=s.instance_id
                AND o.entity_id=s.task_id AND o.operation='task.delete' AND o.acknowledged_at IS NULL)
            ORDER BY s.task_id
            """;
        command.Parameters.AddWithValue("@i", instanceId);
        var result = new List<(string, K0SafeSourceDescriptor)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add((reader.GetString(0), PersonalCloudK0Codec.Deserialize<K0SafeSourceDescriptor>(reader.GetString(1))));
        return result;
    }
    private static async Task SaveBridgeSourceAsync(SqliteConnection connection, string instanceId, string taskId,
        K0SafeSourceDescriptor source, CancellationToken cancellationToken)
    {
        PersonalCloudK0Codec.ValidateSafeSource(source);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO personal_cloud_provider_sources(instance_id,task_id,source_json) VALUES(@i,@t,@j)
            ON CONFLICT(instance_id,task_id) DO UPDATE SET source_json=excluded.source_json
            """;
        command.Parameters.AddWithValue("@i", instanceId);
        command.Parameters.AddWithValue("@t", taskId);
        command.Parameters.AddWithValue("@j", PersonalCloudK0Codec.Serialize(source));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveBridgeCommandAsync(SqliteConnection connection, string instanceId, string taskId,
        long sequence, string operation, string state, string? error, CancellationToken cancellationToken)
    {
        if (sequence <= 0 || operation is not ("complete" or "reopen") ||
            state is not ("pending_desktop" or "confirmed" or "rejected" or "cancelled"))
            throw new PersonalCloudK0ContractException("invalid_request", "Invalid provider command history.");
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO personal_cloud_provider_commands(instance_id,task_id,requested_seq,command,state,error_code)
            VALUES(@i,@t,@q,@c,@s,@e)
            ON CONFLICT(instance_id,task_id) DO UPDATE SET
              requested_seq=excluded.requested_seq,command=excluded.command,state=excluded.state,
              error_code=excluded.error_code,retry_id=CASE WHEN excluded.requested_seq>requested_seq THEN NULL ELSE retry_id END
            WHERE excluded.requested_seq>=requested_seq
            """;
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@t", taskId);
        command.Parameters.AddWithValue("@q", sequence); command.Parameters.AddWithValue("@c", operation);
        command.Parameters.AddWithValue("@s", state); command.Parameters.AddWithValue("@e", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var cancel = connection.CreateCommand();
        cancel.CommandText = """
            UPDATE personal_cloud_provider_attempts SET state='cancelled'
            WHERE instance_id=@i AND task_id=@t AND state IN ('claiming','reserved')
              AND (requested_seq<@q OR (requested_seq=@q AND @s IN ('confirmed','rejected','cancelled')))
            """;
        cancel.Parameters.AddWithValue("@i", instanceId); cancel.Parameters.AddWithValue("@t", taskId);
        cancel.Parameters.AddWithValue("@q", sequence); cancel.Parameters.AddWithValue("@s", state);
        await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecordProviderBridgeChangeAsync(SqliteConnection connection, string instanceId,
        IPersonalCloudTaskChange change, CancellationToken cancellationToken)
    {
        if (change is K0TaskCreatedChange { Payload.Source: { } source })
            await SaveBridgeSourceAsync(connection, instanceId, change.EntityId, source, cancellationToken).ConfigureAwait(false);
        if (change is PersonalCloudSourceRefreshedChange refreshed)
            await SaveBridgeSourceAsync(connection, instanceId, change.EntityId, refreshed.Payload.Task.Source, cancellationToken).ConfigureAwait(false);
        if (change is PersonalCloudTaskCompletedChange or PersonalCloudTaskReopenedChange)
        {
            // Completion is also replicated for Openza-owned tasks. Only a linked source
            // needs an external delivery; never manufacture a provider request for local work.
            var sourceExists = connection.CreateCommand();
            sourceExists.CommandText = "SELECT EXISTS(SELECT 1 FROM personal_cloud_provider_sources WHERE instance_id=@i AND task_id=@t)";
            sourceExists.Parameters.AddWithValue("@i", instanceId);
            sourceExists.Parameters.AddWithValue("@t", change.EntityId);
            if (Convert.ToInt64(await sourceExists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
                await SaveBridgeCommandAsync(connection, instanceId, change.EntityId, change.ChangeSeq,
                    change is PersonalCloudTaskCompletedChange ? "complete" : "reopen", "pending_desktop", null, cancellationToken).ConfigureAwait(false);
        }
        if (change is PersonalCloudExternalCommandChangedChange external)
        {
            var value = external.Payload.Task.ExternalCommand;
            // Older protocol results omitted request identity. They may settle only the matching
            // current command, never invent a new request or supersede a newer intent.
            var sequence = value.RequestedChangeSeq;
            if (sequence is null)
            {
                var read = connection.CreateCommand();
                read.CommandText = "SELECT requested_seq FROM personal_cloud_provider_commands WHERE instance_id=@i AND task_id=@t AND command=@c";
                read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@t", change.EntityId);
                read.Parameters.AddWithValue("@c", value.Command);
                var existing = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (existing is null or DBNull) return;
                sequence = Convert.ToInt64(existing);
            }
            await SaveBridgeCommandAsync(connection, instanceId, change.EntityId, sequence.Value,
                value.Command, value.State, value.ErrorCode, cancellationToken).ConfigureAwait(false);
        }
        if (change is PersonalCloudTaskDeletedChange)
        {
            var cancel = connection.CreateCommand();
            cancel.CommandText = "UPDATE personal_cloud_provider_commands SET state='cancelled' WHERE instance_id=@i AND task_id=@t";
            cancel.Parameters.AddWithValue("@i", instanceId); cancel.Parameters.AddWithValue("@t", change.EntityId);
            await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RecordProviderBridgeBootstrapAsync(SqliteConnection connection, string instanceId,
        PersonalCloudBootstrap bootstrap, CancellationToken cancellationToken)
    {
        foreach (var value in bootstrap.Snapshot.SourceDescriptors)
            await SaveBridgeSourceAsync(connection, instanceId, value.TaskId, new K0SafeSourceDescriptor
            {
                IntegrationKind = value.IntegrationKind, ExternalIdHash = value.ExternalIdHash, SourceUrl = value.SourceUrl,
                Title = value.Title, Description = value.Description, ProjectName = value.ProjectName, Priority = value.Priority,
                PlannedOn = value.PlannedOn, PlannedAt = value.PlannedAt, DeadlineOn = value.DeadlineOn,
                DeadlineAt = value.DeadlineAt, RecurrenceRule = value.RecurrenceRule, RefreshedAt = value.RefreshedAt,
            }, cancellationToken).ConfigureAwait(false);
        foreach (var value in bootstrap.Snapshot.ExternalCommands)
            await SaveBridgeCommandAsync(connection, instanceId, value.TaskId, value.RequestedChangeSeq,
                value.Command, value.State, value.ErrorCode, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PersonalCloudProviderCommand>> GetPersonalCloudProviderCommandsAsync(
        string instanceId, CancellationToken cancellationToken = default)
        => await ReadPersonalCloudProviderCommandsAsync(instanceId, false, 0, null, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<PersonalCloudProviderCommand>> GetPendingPersonalCloudProviderCommandsAsync(
        string instanceId, long afterRequestedSequence = 0, CancellationToken cancellationToken = default)
        => await ReadPersonalCloudProviderCommandsAsync(instanceId, true, afterRequestedSequence, null, cancellationToken).ConfigureAwait(false);

    public async Task<PersonalCloudProviderCommand?> GetPersonalCloudProviderCommandAsync(
        string instanceId, string taskId, CancellationToken cancellationToken = default)
        => (await ReadPersonalCloudProviderCommandsAsync(instanceId, false, 0, taskId, cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    private async Task<IReadOnlyList<PersonalCloudProviderCommand>> ReadPersonalCloudProviderCommandsAsync(
        string instanceId, bool pendingOnly, long afterRequestedSequence, string? taskId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.task_id,c.requested_seq,c.command,c.state,c.error_code,s.source_json
            FROM personal_cloud_provider_commands c JOIN personal_cloud_provider_sources s
              ON s.instance_id=c.instance_id AND s.task_id=c.task_id
            WHERE c.instance_id=@i AND c.state IN ('pending_desktop','rejected')
              AND (@pending=0 OR c.state='pending_desktop') AND c.requested_seq>@after
              AND (@task IS NULL OR c.task_id=@task)
            ORDER BY c.requested_seq LIMIT 100
            """;
        command.Parameters.AddWithValue("@i", instanceId);
        command.Parameters.AddWithValue("@pending", pendingOnly ? 1 : 0);
        command.Parameters.AddWithValue("@after", afterRequestedSequence);
        command.Parameters.AddWithValue("@task", (object?)taskId ?? DBNull.Value);
        var result = new List<PersonalCloudProviderCommand>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), PersonalCloudK0Codec.Deserialize<K0SafeSourceDescriptor>(reader.GetString(5))));
        return result;
    }

    public async Task<PersonalCloudProviderAttempt> PreparePersonalCloudProviderAttemptAsync(string instanceId,
        string deviceId, PersonalCloudProviderCommand request, string accountBinding, CancellationToken cancellationToken = default)
    {
        using var lease = PersonalCloudConnectionLease.AcquireAuthoring(DatabasePath);
        if (string.IsNullOrWhiteSpace(accountBinding)) throw new ArgumentException("A local provider account binding is required.", nameof(accountBinding));
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO personal_cloud_provider_attempts
              (claim_id,instance_id,task_id,requested_seq,device_id,account_binding,command,source_json,state)
            SELECT @id,@i,@t,@q,@d,@a,@c,@j,'claiming'
            WHERE EXISTS(SELECT 1 FROM personal_cloud_provider_commands WHERE instance_id=@i AND task_id=@t
              AND requested_seq=@q AND state='pending_desktop')
            """;
        command.Parameters.AddWithValue("@id", $"claim_{Guid.NewGuid():N}"); command.Parameters.AddWithValue("@i", instanceId);
        command.Parameters.AddWithValue("@t", request.TaskId); command.Parameters.AddWithValue("@q", request.RequestedChangeSeq);
        command.Parameters.AddWithValue("@d", deviceId); command.Parameters.AddWithValue("@a", accountBinding);
        command.Parameters.AddWithValue("@c", request.Command); command.Parameters.AddWithValue("@j", PersonalCloudK0Codec.Serialize(request.Source));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var read = connection.CreateCommand();
        read.CommandText = "SELECT claim_id,account_binding,command,state,source_json FROM personal_cloud_provider_attempts WHERE instance_id=@i AND task_id=@t AND requested_seq=@q AND device_id=@d";
        read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@t", request.TaskId);
        read.Parameters.AddWithValue("@q", request.RequestedChangeSeq); read.Parameters.AddWithValue("@d", deviceId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new PersonalCloudK0ContractException("command_superseded", "The provider command changed before reservation.");
        var value = new PersonalCloudProviderAttempt(reader.GetString(0), request.TaskId, request.RequestedChangeSeq,
            deviceId, reader.GetString(1), reader.GetString(2), reader.GetString(3), PersonalCloudK0Codec.Deserialize<K0SafeSourceDescriptor>(reader.GetString(4)));
        if (value.AccountBinding != accountBinding || value.Source.ExternalIdHash != request.Source.ExternalIdHash ||
            value.Source.IntegrationKind != request.Source.IntegrationKind || value.Command != request.Command)
            throw new PersonalCloudK0ContractException("source_identity_conflict", "The reserved provider account or source has changed.");
        return value;
    }

    public async Task<bool> TransitionPersonalCloudProviderAttemptAsync(string claimId, string expected, string next,
        CancellationToken cancellationToken = default)
    {
        // CAS is the dispatch gate shared by all processes using this replica.
        if (next is not ("reserved" or "starting" or "dispatching" or "uncertain" or "cancelled")) throw new ArgumentException("Invalid transition.", nameof(next));
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE personal_cloud_provider_attempts SET state=@next WHERE claim_id=@id AND state=@prior";
        command.Parameters.AddWithValue("@next", next); command.Parameters.AddWithValue("@id", claimId);
        command.Parameters.AddWithValue("@prior", expected);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task CompletePersonalCloudProviderAttemptAsync(string instanceId, PersonalCloudProviderAttempt attempt,
        string? errorCode, CancellationToken cancellationToken = default)
    {
        using var lease = PersonalCloudConnectionLease.AcquireAuthoring(DatabasePath);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var mutationId = $"mutation_{Guid.NewGuid():N}";
        var reserve = connection.CreateCommand();
        reserve.CommandText = "UPDATE personal_cloud_provider_attempts SET state='outcome',result_mutation_id=@m WHERE claim_id=@id AND instance_id=@i AND device_id=@d AND state='dispatching'";
        reserve.Parameters.AddWithValue("@m", mutationId); reserve.Parameters.AddWithValue("@id", attempt.ClaimId);
        reserve.Parameters.AddWithValue("@i", instanceId); reserve.Parameters.AddWithValue("@d", attempt.DeviceId);
        if (await reserve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new PersonalCloudK0ContractException("invalid_transition", "The provider outcome is already stored or requires reconciliation.");
        await AppendProviderSemanticAsync(connection, instanceId, attempt.DeviceId, attempt.TaskId, mutationId,
            (sequence, timestamp) => PersonalCloudProviderBridgeCodec.ResultMutation(instanceId, attempt.DeviceId,
                mutationId, sequence, attempt.TaskId, attempt.ClaimId, attempt.RequestedChangeSeq, timestamp, errorCode), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> PreparePersonalCloudProviderRetryAsync(string instanceId, string taskId, long sequence,
        CancellationToken cancellationToken = default)
    {
        using var lease = PersonalCloudConnectionLease.AcquireAuthoring(DatabasePath);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var update = connection.CreateCommand();
        update.CommandText = "UPDATE personal_cloud_provider_commands SET retry_id=COALESCE(retry_id,@r) WHERE instance_id=@i AND task_id=@t AND requested_seq=@q AND state='rejected'";
        update.Parameters.AddWithValue("@r", $"retry_{Guid.NewGuid():N}"); update.Parameters.AddWithValue("@i", instanceId);
        update.Parameters.AddWithValue("@t", taskId); update.Parameters.AddWithValue("@q", sequence);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new PersonalCloudK0ContractException("invalid_transition", "Only a rejected provider request can be retried.");
        var read = connection.CreateCommand();
        read.CommandText = "SELECT retry_id FROM personal_cloud_provider_commands WHERE instance_id=@i AND task_id=@t AND requested_seq=@q";
        read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@t", taskId); read.Parameters.AddWithValue("@q", sequence);
        return (string)(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task EnqueuePersonalCloudSourceSnapshotAsync(string instanceId, string deviceId, string taskId,
        K0SafeSourceDescriptor source, CancellationToken cancellationToken = default)
    {
        PersonalCloudK0Codec.ValidateSafeSource(source);
        using var lease = PersonalCloudConnectionLease.AcquireAuthoring(DatabasePath);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var read = connection.CreateCommand();
        read.CommandText = """
            SELECT s.source_json FROM personal_cloud_provider_sources s WHERE s.instance_id=@i AND s.task_id=@t
              AND NOT EXISTS(SELECT 1 FROM personal_cloud_tombstones t WHERE t.instance_id=@i AND t.entity_id=@t)
              AND NOT EXISTS(SELECT 1 FROM personal_cloud_outbox o WHERE o.instance_id=@i AND o.entity_id=@t
                AND o.operation='task.delete' AND o.acknowledged_at IS NULL)
            """;
        read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@t", taskId);
        var json = (string?)await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (json is null) return;
        var current = PersonalCloudK0Codec.Deserialize<K0SafeSourceDescriptor>(json);
        if (current.IntegrationKind != source.IntegrationKind || current.ExternalIdHash != source.ExternalIdHash)
            throw new PersonalCloudK0ContractException("source_identity_conflict", "Source identity changed during refresh.");
        var baseline = current;
        var latest = connection.CreateCommand();
        latest.CommandText = """
            SELECT canonical_json FROM personal_cloud_outbox
            WHERE instance_id=@i AND entity_id=@t AND operation='source.refreshSnapshot'
            ORDER BY local_sequence DESC LIMIT 1
            """;
        latest.Parameters.AddWithValue("@i", instanceId); latest.Parameters.AddWithValue("@t", taskId);
        if (await latest.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string latestJson)
        {
            using var document = System.Text.Json.JsonDocument.Parse(latestJson);
            var queued = PersonalCloudK0Codec.Deserialize<K0SafeSourceDescriptor>(document.RootElement.GetProperty("payload").GetProperty("source").GetRawText());
            if (queued.IntegrationKind == current.IntegrationKind && queued.ExternalIdHash == current.ExternalIdHash &&
                ParseProtocolTimestamp(queued.RefreshedAt) > ParseProtocolTimestamp(current.RefreshedAt))
                baseline = queued; // Includes pending/in-flight and receipt-acknowledged snapshots whose echo has not arrived.
        }
        var observedAt = ParseProtocolTimestamp(source.RefreshedAt);
        var watermark = connection.CreateCommand();
        watermark.CommandText = """
            SELECT observed_at FROM personal_cloud_source_observations WHERE instance_id=@i AND task_id=@t
                AND integration_kind=@kind AND external_id_hash=@hash
            """;
        watermark.Parameters.AddWithValue("@i", instanceId); watermark.Parameters.AddWithValue("@t", taskId);
        watermark.Parameters.AddWithValue("@kind", source.IntegrationKind); watermark.Parameters.AddWithValue("@hash", source.ExternalIdHash);
        var previousObservation = await watermark.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (observedAt <= ParseProtocolTimestamp(baseline.RefreshedAt) ||
            previousObservation is long timestamp && observedAt.ToUnixTimeMilliseconds() <= timestamp) return;

        var observe = connection.CreateCommand();
        observe.CommandText = """
            INSERT INTO personal_cloud_source_observations(instance_id,task_id,integration_kind,external_id_hash,observed_at)
            VALUES(@i,@t,@kind,@hash,@at)
            ON CONFLICT(instance_id,task_id) DO UPDATE SET integration_kind=excluded.integration_kind,
                external_id_hash=excluded.external_id_hash,observed_at=excluded.observed_at
            """;
        observe.Parameters.AddWithValue("@i", instanceId); observe.Parameters.AddWithValue("@t", taskId);
        observe.Parameters.AddWithValue("@kind", source.IntegrationKind); observe.Parameters.AddWithValue("@hash", source.ExternalIdHash);
        observe.Parameters.AddWithValue("@at", observedAt.ToUnixTimeMilliseconds());
        await observe.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if ((source with { RefreshedAt = string.Empty }) == (baseline with { RefreshedAt = string.Empty }) &&
            observedAt - ParseProtocolTimestamp(baseline.RefreshedAt) < SourceSnapshotHeartbeatInterval)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        var mutationId = $"mutation_{Guid.NewGuid():N}";
        await AppendProviderSemanticAsync(connection, instanceId, deviceId, taskId, mutationId,
            (sequence, timestamp) => PersonalCloudProviderBridgeCodec.SourceMutation(instanceId, deviceId,
                mutationId, sequence, taskId, source, timestamp), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AppendProviderSemanticAsync(SqliteConnection connection, string instanceId,
        string deviceId, string taskId, string mutationId, Func<long, string, string> serialize,
        CancellationToken cancellationToken)
    {
        var initialize = connection.CreateCommand();
        initialize.CommandText = "INSERT OR IGNORE INTO personal_cloud_device_state(instance_id,device_id,next_device_sequence) VALUES(@i,@d,1)";
        initialize.Parameters.AddWithValue("@i", instanceId); initialize.Parameters.AddWithValue("@d", deviceId);
        await initialize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var read = connection.CreateCommand();
        read.CommandText = "SELECT next_device_sequence FROM personal_cloud_device_state WHERE instance_id=@i AND device_id=@d";
        read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@d", deviceId);
        var sequence = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        var now = DateTimeOffset.UtcNow;
        var json = serialize(sequence, PersonalCloudK0Mapper.FormatTimestamp(now));
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024)
            throw new PersonalCloudK0ContractException("invalid_request", "The source snapshot exceeds the Personal Cloud request size limit; no outbox entry was stored.");
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var append = connection.CreateCommand();
        append.CommandText = """
            INSERT INTO personal_cloud_outbox(mutation_id,product,instance_id,device_id,device_sequence,
              operation,entity_id,canonical_json,canonical_hash,created_at)
            VALUES(@m,'tasks',@i,@d,@q,@o,@t,@j,@h,@n);
            UPDATE personal_cloud_device_state SET next_device_sequence=@next WHERE instance_id=@i AND device_id=@d;
            """;
        append.Parameters.AddWithValue("@m", mutationId); append.Parameters.AddWithValue("@i", instanceId);
        append.Parameters.AddWithValue("@d", deviceId); append.Parameters.AddWithValue("@q", sequence);
        append.Parameters.AddWithValue("@o", document.RootElement.GetProperty("operation").GetString()!);
        append.Parameters.AddWithValue("@t", taskId); append.Parameters.AddWithValue("@j", json);
        append.Parameters.AddWithValue("@h", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant());
        append.Parameters.AddWithValue("@n", now.ToUnixTimeMilliseconds()); append.Parameters.AddWithValue("@next", checked(sequence + 1));
        await append.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
