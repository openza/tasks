using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    public async Task<long> GetPersonalCloudFanoutBoundAsync(PersonalCloudK1OutboxEntry entry,
        bool useWorkspaceFallback = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM personal_cloud_workspace_indirect_versions WHERE instance_id=@i AND mutation_id=@m";
        count.Parameters.AddWithValue("@i", entry.InstanceId);
        count.Parameters.AddWithValue("@m", entry.MutationId);
        var indirect = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        // The same authoring transaction captured descendants/children even if
        // the local delete or move already detached them from their old parent.
        var frozen = indirect + (entry.Operation == "task.moveTree" ? 1 : 0);
        if (indirect > 0 && !useWorkspaceFallback) return frozen;
        count.CommandText = """
            SELECT MAX((SELECT COUNT(*) FROM tasks)+(SELECT COUNT(*) FROM projects),
                (SELECT COUNT(*) FROM personal_cloud_workspace_records WHERE instance_id=@i AND kind IN ('tasks','projects')))+1
            """;
        return Math.Max(frozen, Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
    }
}
