namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    public async Task<long> GetPendingPersonalCloudOutboxCountAsync(string instanceId, string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND product='tasks' AND acknowledged_at IS NULL";
        command.Parameters.AddWithValue("@i", instanceId);
        command.Parameters.AddWithValue("@d", deviceId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }
}
