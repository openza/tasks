using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    public async Task<string> BeginPersonalCloudWorkspaceBootstrapAsync(string instanceId, string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        // A crashed attempt is disposable staging, never part of the replica.
        await WorkspaceExecuteAsync(connection, "DELETE FROM personal_cloud_workspace_bootstraps WHERE instance_id=@i AND device_id=@d", cancellationToken,
            ("@i", instanceId), ("@d", deviceId)).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, "INSERT INTO personal_cloud_workspace_bootstraps(id,instance_id,device_id) VALUES(@id,@i,@d)", cancellationToken,
            ("@id", id), ("@i", instanceId), ("@d", deviceId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task<string?> StagePersonalCloudWorkspaceBootstrapPageAsync(string transferId, string? requestedCursor, string json, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > 2097152) throw BootstrapLimit();
        var page = PersonalCloudWorkspaceCodec.ParseBootstrapPage(json);
        var count = PersonalCloudWorkspaceCodec.SnapshotSections.Sum(k => page.Snapshot.GetProperty(k).GetArrayLength());
        var section = Array.FindIndex(PersonalCloudWorkspaceCodec.SnapshotSections, k => page.Snapshot.GetProperty(k).GetArrayLength() > 0);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var read = connection.CreateCommand();
        read.CommandText = "SELECT checkpoint,page_count,record_count,json_bytes,next_cursor,complete,last_section FROM personal_cloud_workspace_bootstraps WHERE id=@id";
        read.Parameters.AddWithValue("@id", transferId);
        long? checkpoint; int pages; long records; long accumulated; string? cursor; bool complete; int previousSection;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw PersonalCloudWorkspaceCodec.Invalid();
            checkpoint = reader.IsDBNull(0) ? null : reader.GetInt64(0); pages = reader.GetInt32(1); records = reader.GetInt64(2);
            accumulated = reader.GetInt64(3); cursor = reader.IsDBNull(4) ? null : reader.GetString(4); complete = reader.GetInt64(5) != 0; previousSection = reader.GetInt32(6);
        }
        if (complete || cursor != requestedCursor || section >= 0 && section < previousSection) throw PersonalCloudWorkspaceCodec.Invalid();
        if (checkpoint is { } pinned && pinned != page.Checkpoint)
            throw new PersonalCloudK0ContractException("bootstrap_changed", "Personal Cloud changed its baseline checkpoint during download. Retry Sync.");
        if (pages >= 10000 || records + count > 100000 || accumulated + bytes > 128L * 1024 * 1024) throw BootstrapLimit();
        try
        {
            if (page.NextCursor is { } next)
                await WorkspaceExecuteAsync(connection, "INSERT INTO personal_cloud_workspace_bootstrap_keys(bootstrap_id,section,entity_id) VALUES(@b,'cursor',@e)", cancellationToken,
                    ("@b", transferId), ("@e", next)).ConfigureAwait(false);
            foreach (var name in PersonalCloudWorkspaceCodec.SnapshotSections)
            foreach (var item in page.Snapshot.GetProperty(name).EnumerateArray())
            {
                var key = name switch
                {
                    "taskLabels" => PersonalCloudK0Codec.Serialize(new[] { WText(item, "taskId"), WText(item, "labelId") }),
                    "tombstones" => WText(item, "entityKind") + ":" + WText(item, "entityId"),
                    "sourceDescriptors" or "externalCommands" => WText(item, "taskId"),
                    _ => WText(item, "id"),
                };
                if (string.IsNullOrEmpty(key)) throw PersonalCloudWorkspaceCodec.Invalid();
                await WorkspaceExecuteAsync(connection, "INSERT INTO personal_cloud_workspace_bootstrap_keys(bootstrap_id,section,entity_id) VALUES(@b,@s,@e)", cancellationToken,
                    ("@b", transferId), ("@s", name), ("@e", key)).ConfigureAwait(false);
            }
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new PersonalCloudK0ContractException("invalid_response", "Personal Cloud repeated a baseline cursor or record.");
        }
        await WorkspaceExecuteAsync(connection, "INSERT INTO personal_cloud_workspace_bootstrap_pages(bootstrap_id,page_number,snapshot_json) VALUES(@b,@p,@j)", cancellationToken,
            ("@b", transferId), ("@p", pages), ("@j", page.Snapshot.GetRawText())).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, """
            UPDATE personal_cloud_workspace_bootstraps SET checkpoint=@q,page_count=page_count+1,record_count=record_count+@r,
              json_bytes=json_bytes+@bytes,next_cursor=@c,complete=@done,last_section=@s WHERE id=@id
            """, cancellationToken, ("@id", transferId), ("@q", page.Checkpoint), ("@r", count), ("@bytes", bytes),
            ("@c", page.NextCursor), ("@done", page.HasMore ? 0 : 1), ("@s", Math.Max(section, previousSection))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return page.NextCursor;
    }

    public async Task<long> CommitPersonalCloudWorkspaceBootstrapAsync(string transferId, string instanceId, string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var read = connection.CreateCommand();
        read.CommandText = "SELECT checkpoint,page_count FROM personal_cloud_workspace_bootstraps WHERE id=@b AND instance_id=@i AND device_id=@d AND complete=1 AND page_count>0";
        read.Parameters.AddWithValue("@b", transferId); read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@d", deviceId);
        long checkpoint; int pages;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw PersonalCloudWorkspaceCodec.Invalid();
            checkpoint = reader.GetInt64(0); pages = reader.GetInt32(1);
        }
        await EnsureNoPendingPersonalCloudIntentsAsync(connection, cancellationToken).ConfigureAwait(false);
        await CreateIncomingWorkspaceTasksAsync(connection, cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < pages; index++)
        {
            using var snapshot = await ReadStagedWorkspacePageAsync(connection, transferId, index, cancellationToken).ConfigureAwait(false);
            await AddIncomingWorkspaceTasksAsync(connection, snapshot.RootElement, cancellationToken).ConfigureAwait(false);
        }
        await PrepareWorkspaceBootstrapReplacementAsync(connection, instanceId, cancellationToken).ConfigureAwait(false);
        // Read existing private fields once, rather than rereading the growing task
        // table for every page. Only one staged JSON page is materialized at a time.
        var prior = (await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false)).ToDictionary(t => t.Id, StringComparer.Ordinal);
        for (var index = 0; index < pages; index++)
        {
            using var snapshot = await ReadStagedWorkspacePageAsync(connection, transferId, index, cancellationToken).ConfigureAwait(false);
            await ApplyWorkspaceBootstrapSnapshotAsync(connection, instanceId, snapshot.RootElement, cancellationToken, prior).ConfigureAwait(false);
        }
        await FinishWorkspaceBootstrapAsync(connection, instanceId, deviceId, checkpoint, cancellationToken).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, "DELETE FROM personal_cloud_workspace_bootstraps WHERE id=@b", cancellationToken, ("@b", transferId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private static async Task<JsonDocument> ReadStagedWorkspacePageAsync(SqliteConnection connection, string transferId, int page, CancellationToken cancellationToken)
    {
        var read = connection.CreateCommand(); read.CommandText = "SELECT snapshot_json FROM personal_cloud_workspace_bootstrap_pages WHERE bootstrap_id=@b AND page_number=@p";
        read.Parameters.AddWithValue("@b", transferId); read.Parameters.AddWithValue("@p", page);
        return JsonDocument.Parse((string?)await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? throw PersonalCloudWorkspaceCodec.Invalid());
    }

    public async Task DiscardPersonalCloudWorkspaceBootstrapAsync(string transferId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, "DELETE FROM personal_cloud_workspace_bootstraps WHERE id=@b", cancellationToken, ("@b", transferId)).ConfigureAwait(false);
    }

    private static PersonalCloudK0ContractException BootstrapLimit() => new("sync_incomplete", "The Personal Cloud baseline exceeds this sync's safe download limit. The current replica and pending edits have been preserved.");
}
