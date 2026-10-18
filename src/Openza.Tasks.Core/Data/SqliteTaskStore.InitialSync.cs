using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    public static void ValidatePersonalCloudMutationReceipt(PersonalCloudK1OutboxEntry mutation, string json)
    {
        var receipt = PersonalCloudK0Codec.ParseMutationReceipt(json);
        var auxiliary = mutation.Operation is "source.refreshSnapshot" or "externalCommand.confirm" or "externalCommand.reject";
        if (receipt.MutationId != mutation.MutationId || receipt.EntityId != mutation.EntityId ||
            !auxiliary && receipt.EntityVersion != PersonalCloudMutationEntityVersion(mutation.Operation, mutation.CanonicalJson, mutation.InstanceId))
            throw new PersonalCloudK0ContractException("invalid_response", "The receipt did not match the submitted intent. Pending edits have been preserved.");
    }
    public async Task BeginPersonalCloudInitialSyncAsync(string instanceId, string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO personal_cloud_initial_sync(instance_id,device_id) VALUES(@i,@d)";
        command.Parameters.AddWithValue("@i", instanceId);
        command.Parameters.AddWithValue("@d", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Called inside the seed staging transaction, including the zero-task case. Later edits cannot grow the seed.
    private static async Task SealInitialSyncSeedAsync(SqliteConnection connection, string instanceId, string deviceId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE personal_cloud_initial_sync
            SET seed_end_sequence=(SELECT COALESCE(MAX(device_sequence),0) FROM personal_cloud_outbox
                WHERE instance_id=@i AND device_id=@d AND product='tasks')
            WHERE instance_id=@i AND device_id=@d AND seed_end_sequence IS NULL
            """;
        command.Parameters.AddWithValue("@i", instanceId);
        command.Parameters.AddWithValue("@d", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersonalCloudInitialSyncProgress?> GetPersonalCloudInitialSyncProgressAsync(
        string instanceId, string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.phase,s.seed_end_sequence,s.error_code,
                (SELECT COUNT(*) FROM personal_cloud_outbox o WHERE o.instance_id=s.instance_id AND o.device_id=s.device_id
                    AND o.product='tasks' AND o.device_sequence<=s.seed_end_sequence),
                (SELECT COUNT(*) FROM personal_cloud_outbox o WHERE o.instance_id=s.instance_id AND o.device_id=s.device_id
                    AND o.product='tasks' AND o.device_sequence<=s.seed_end_sequence AND o.acknowledged_at IS NOT NULL)
            FROM personal_cloud_initial_sync s WHERE s.instance_id=@i AND s.device_id=@d
            """;
        command.Parameters.AddWithValue("@i", instanceId);
        command.Parameters.AddWithValue("@d", deviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var sealedSeed = !reader.IsDBNull(1);
        return new(instanceId, deviceId, Enum.Parse<PersonalCloudInitialSyncPhase>(reader.GetString(0)),
            sealedSeed ? reader.GetInt64(4) : null, sealedSeed ? reader.GetInt64(3) : null,
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task SetPersonalCloudInitialSyncPhaseAsync(string instanceId, string deviceId,
        PersonalCloudInitialSyncPhase phase, string? errorCode = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE personal_cloud_initial_sync SET phase=@phase,error_code=@error
            WHERE instance_id=@i AND device_id=@d AND phase<>'Complete'
            """;
        command.Parameters.AddWithValue("@i", instanceId);
        command.Parameters.AddWithValue("@d", deviceId);
        command.Parameters.AddWithValue("@phase", phase.ToString());
        command.Parameters.AddWithValue("@error", (object?)errorCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> AdvancePersonalCloudInitialReportSequenceAsync(string instanceId, string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE personal_cloud_initial_sync SET report_sequence=report_sequence+1
            WHERE instance_id=@i AND device_id=@d AND report_sequence<9007199254740991
            RETURNING report_sequence
            """;
        command.Parameters.AddWithValue("@i", instanceId);
        command.Parameters.AddWithValue("@d", deviceId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }
}
