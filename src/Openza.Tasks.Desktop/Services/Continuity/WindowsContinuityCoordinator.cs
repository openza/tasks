using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Desktop.Services.Continuity;

/// <summary>Package-local, Dev-gated preparation before the store or main window opens.</summary>
public sealed class WindowsContinuityCoordinator
{
    public const string CloudRecoveryNotice = "Personal Cloud sync is paused. Open Sync or Settings to recover the saved pairing or export/restore a safety copy. Local tasks and pending operations remain; legacy identity will not be re-imported.";
    private const string ArchiveName = "windows-continuity-v1";
    private static readonly string[] Inputs = ["settings.json", "personal-cloud-connection.json", "personal-cloud-provisioning.json", "msal-cache.bin", "openza-tasks.db", "openza-tasks.db-wal"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Plan(int Version, string FamilyName, Dictionary<string, string?> Inputs, string? DatabaseBackupHash);
    private sealed record Completion(int Version, string SettingsHash);

    public async Task PrepareAsync(WindowsContinuitySession session, IWindowsContinuityPlatform platform,
        string clientId, string tenantId, CancellationToken cancellationToken = default, Action<string>? checkpoint = null)
    {
        session.EnsureActive();
        var data = session.Data;
        session.Package.ValidateDev();
        var root = data.Runtime.DataDirectory;
        RejectLink(root);
        PrivateFilePermissions.EnsureDirectory(root);
        using var runtimeLease = ChannelRuntimeLease.AcquireExclusive(data.Runtime);
        var lockPath = Path.Combine(root, ".windows-continuity.lock");
        RejectLink(lockPath);
        using var preparationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var archive = Path.Combine(root, ArchiveName);
        RejectLink(archive);
        if (!Directory.Exists(archive))
        {
            if (File.Exists(session.SettingsPath)) throw Stop("A destination settings file already exists without transition history. Resolve that conflict before continuing.");
            await CreateArchiveAsync(session, archive, cancellationToken).ConfigureAwait(false);
        }
        var plan = JsonSerializer.Deserialize<Plan>(await ReadAsync(Path.Combine(archive, "plan.json"), cancellationToken).ConfigureAwait(false), Json)
            ?? throw Stop("The transition journal is invalid. Its original files were retained.");
        if (plan.Version != 1 || plan.FamilyName != session.Package.FamilyName || plan.Inputs.Count != Inputs.Length || Inputs.Any(name => !plan.Inputs.ContainsKey(name)))
            throw Stop("The transition journal does not match this package. No source state was replaced.");
        foreach (var name in Inputs.Where(name => name is not ("openza-tasks.db" or "openza-tasks.db-wal")))
            if (plan.Inputs[name] is { } hash && Hash(await ReadAsync(Path.Combine(archive, name), cancellationToken).ConfigureAwait(false)) != hash)
                throw Stop("A retained original file failed verification. Recover the transition archive before continuing.");
        if (plan.DatabaseBackupHash is { } backupHash && Hash(await ReadAsync(Path.Combine(archive, "original.db"), cancellationToken).ConfigureAwait(false)) != backupHash)
            throw Stop("The retained database restore point failed verification.");

        var completed = Path.Combine(archive, "complete.json");
        RejectLink(completed);
        if (File.Exists(completed))
        {
            var receipt = JsonSerializer.Deserialize<Completion>(await ReadAsync(completed, cancellationToken).ConfigureAwait(false), Json);
            if (receipt?.Version != 1 || receipt.SettingsHash != Hash(await ReadAsync(Path.Combine(archive, "converted-settings.json"), cancellationToken).ConfigureAwait(false)))
                throw Stop("The transition completion record is invalid. Recover its retained archive before continuing.");
            if (!File.Exists(session.SettingsPath)) throw Stop("Converted settings are missing. The original remains in the transition archive.");
            await ReadPreferencesAsync(session.SettingsPath, cancellationToken).ConfigureAwait(false);
            var recoveryRequired = false;
            try
            {
                var currentProfile = await ValidateCloudStateAsync(data, cancellationToken).ConfigureAwait(false);
                if (currentProfile is not null) await WindowsCredentialContinuity.ValidateCloudAsync(currentProfile, session.Credentials, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WindowsContinuityException or PersonalCloudK0ContractException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            { recoveryRequired = true; }
            var preferencesStore = new DesktopPreferencesStore(session.SettingsPath);
            var currentPreferences = preferencesStore.Load();
            if (recoveryRequired)
            {
                await preferencesStore.UpdateAsync(value => value with
                {
                    WindowsCloudRecoveryRequired = true, AutomaticSyncEnabled = false,
                    WindowsContinuityNotices = value.WindowsContinuityNotices.Append(CloudRecoveryNotice).Distinct(StringComparer.Ordinal).ToArray(),
                }).ConfigureAwait(false);
            }
            else if (currentPreferences.WindowsCloudRecoveryRequired)
                await preferencesStore.UpdateAsync(value => value with
                {
                    WindowsCloudRecoveryRequired = false,
                    WindowsContinuityNotices = value.WindowsContinuityNotices.Where(notice => notice != CloudRecoveryNotice).ToArray(),
                }).ConfigureAwait(false);
            return;
        }
        var profile = await ValidateCloudStateAsync(data, cancellationToken).ConfigureAwait(false);
        if (!Same(plan.Inputs, await FingerprintAsync(root, cancellationToken).ConfigureAwait(false)))
            throw Stop("The Windows source changed after preparation. Keep both copies and resolve the interrupted transition before continuing.");
        checkpoint?.Invoke("archive-ready");
        var sourceSettings = plan.Inputs["settings.json"] is null ? "{}" : Encoding.UTF8.GetString(await ReadAsync(Path.Combine(archive, "settings.json"), cancellationToken).ConfigureAwait(false));
        var conversion = WinUiSettingsConverter.Convert(sourceSettings.TrimStart('\uFEFF'));
        var notices = new List<string>
        {
            "Windows Dev transition: automatic sync and OneDrive upload start disabled. Review connections before enabling them.",
            "Legacy provider tokens have no channel ownership proof. Reconnect Todoist, GitHub or backup as needed; original credentials were not read or removed.",
        };
        if (conversion.RetainedFields.Count > 0) notices.Add("Some Windows settings remain in the retained original; review the transition archive for unsupported preferences.");
        var protectedCache = plan.Inputs["msal-cache.bin"] is null ? null : await ReadAsync(Path.Combine(archive, "msal-cache.bin"), cancellationToken).ConfigureAwait(false);
        notices.AddRange(await WindowsCredentialContinuity.TransferMicrosoftAsync(protectedCache, conversion.Preferences, clientId, tenantId,
            platform.UnprotectMicrosoftCache, session.Credentials, cancellationToken).ConfigureAwait(false));
        if (profile is not null)
            await WindowsCredentialContinuity.TransferCloudAsync(profile, platform.OpenLegacyCloudCredentialReader(), session.Credentials, cancellationToken).ConfigureAwait(false);
        checkpoint?.Invoke("credentials-ready");
        var preferences = conversion.Preferences with { AutomaticSyncEnabled = false, OneDriveEnabled = false, WindowsContinuityNotices = notices };
        var converted = JsonSerializer.SerializeToUtf8Bytes(preferences, Json);
        await WriteImmutableAsync(Path.Combine(archive, "converted-settings.json"), converted, cancellationToken).ConfigureAwait(false);
        if (!Same(plan.Inputs, await FingerprintAsync(root, cancellationToken).ConfigureAwait(false)))
            throw Stop("The Windows source changed during preparation. No source files were replaced.");
        await WriteImmutableAsync(session.SettingsPath, converted, cancellationToken).ConfigureAwait(false);
        checkpoint?.Invoke("settings-applied");
        await WriteImmutableAsync(completed, JsonSerializer.SerializeToUtf8Bytes(new Completion(1, Hash(converted)), Json), cancellationToken).ConfigureAwait(false);
        checkpoint?.Invoke("complete");
    }

    private static async Task CreateArchiveAsync(WindowsContinuitySession session, string archive, CancellationToken cancellationToken)
    {
        var root = session.Data.Runtime.DataDirectory;
        var before = await FingerprintAsync(root, cancellationToken).ConfigureAwait(false);
        var stage = Path.Combine(root, $".windows-continuity-stage-{Guid.NewGuid():N}");
        PrivateFilePermissions.EnsureDirectory(stage);
        foreach (var name in Inputs.Where(name => name is not ("openza-tasks.db" or "openza-tasks.db-wal")))
            if (before[name] is not null)
                await WriteImmutableAsync(Path.Combine(stage, name), await ReadAsync(Path.Combine(root, name), cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        string? backupHash = null;
        if (before["openza-tasks.db"] is not null)
        {
            using var source = ReadOnlyDatabase(session.Data.Runtime.DatabasePath);
            await source.OpenAsync(cancellationToken).ConfigureAwait(false);
            var backupPath = Path.Combine(stage, "original.db");
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
            {
                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                source.BackupDatabase(destination);
            }
            PrivateFilePermissions.EnsureFile(backupPath);
            backupHash = Hash(await ReadAsync(backupPath, cancellationToken).ConfigureAwait(false));
        }
        if (!Same(before, await FingerprintAsync(root, cancellationToken).ConfigureAwait(false)))
            throw Stop("The Windows source changed while its safety copy was being made. The source and incomplete safety copy were retained.");
        var plan = new Plan(1, session.Package.FamilyName, before, backupHash);
        await WriteImmutableAsync(Path.Combine(stage, "plan.json"), JsonSerializer.SerializeToUtf8Bytes(plan, Json), cancellationToken).ConfigureAwait(false);
        Directory.Move(stage, archive);
    }

    private static async Task<PersonalCloudConnectionProfile?> ValidateCloudStateAsync(
        Openza.Tasks.Application.Continuity.WindowsPackageDataContext data, CancellationToken cancellationToken)
    {
        RejectLink(data.ConnectionPath);
        var profile = new JsonPersonalCloudConnectionStore(data.ConnectionPath).Load();
        if (profile is not null && !string.IsNullOrEmpty(profile.ServiceOrigin.UserInfo))
            throw Stop("The saved Personal Cloud origin contains unsupported user information. Recover the original connection before continuing.");
        RejectLink(data.Runtime.DatabasePath);
        if (!File.Exists(data.Runtime.DatabasePath))
        {
            if (profile is not null) throw Stop("Personal Cloud is paired but its local replica is missing. Recover the replica before continuing.");
            return null;
        }
        using var db = ReadOnlyDatabase(data.Runtime.DatabasePath);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var matching = 0L;
        foreach (var table in new[] { "personal_cloud_outbox", "personal_cloud_checkpoints", "personal_cloud_device_state" })
        {
            using var exists = db.CreateCommand();
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
            exists.Parameters.AddWithValue("@name", table);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0) continue;
            using var query = db.CreateCommand();
            if (profile is not null)
            {
                query.CommandText = $"SELECT COUNT(*) FROM {table} WHERE instance_id=@i AND device_id=@d";
                query.Parameters.AddWithValue("@i", profile.InstanceId);
                query.Parameters.AddWithValue("@d", profile.DeviceId);
                matching += Convert.ToInt64(await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            }
            if (table == "personal_cloud_outbox")
            {
                query.CommandText = profile is null ? "SELECT COUNT(*) FROM personal_cloud_outbox WHERE acknowledged_at IS NULL"
                    : "SELECT COUNT(*) FROM personal_cloud_outbox WHERE acknowledged_at IS NULL AND (instance_id!=@i OR device_id!=@d)";
                if (Convert.ToInt64(await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0)
                    throw Stop("Pending Personal Cloud operations do not match the saved connection. Recover the pairing before continuing; operations were retained.");
            }
        }
        if (profile is not null && matching == 0) throw Stop("The saved Personal Cloud device does not match the local replica. Recover its existing pairing before continuing.");
        return profile;
    }

    private static SqliteConnection ReadOnlyDatabase(string path) => new(new SqliteConnectionStringBuilder
    { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());

    private static async Task<Dictionary<string, string?>> FingerprintAsync(string root, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in Inputs)
        {
            var path = Path.Combine(root, name);
            RejectLink(path);
            result[name] = File.Exists(path) ? Hash(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)) : null;
        }
        return result;
    }
    private static bool Same(Dictionary<string, string?> left, Dictionary<string, string?> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static WindowsContinuityException Stop(string message) => new(message);
    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            throw Stop("Linked transition files are not supported. Use the package's original files without redirecting them.");
    }
    private static async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
    {
        RejectLink(path);
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }
    private static async Task<DesktopPreferences> ReadPreferencesAsync(string path, CancellationToken cancellationToken)
    {
        var preferences = JsonSerializer.Deserialize<DesktopPreferences>(await ReadAsync(path, cancellationToken).ConfigureAwait(false))
            ?? throw Stop("The converted settings file is invalid. Restore its retained copy before continuing.");
        if (preferences.ProjectSortSettings is null || preferences.TaskViewSettings is null || preferences.WindowsContinuityNotices is null)
            throw Stop("The converted settings file is invalid. Restore its retained copy before continuing.");
        return preferences;
    }
    private static async Task WriteImmutableAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        RejectLink(path);
        if (File.Exists(path))
        {
            var existing = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (!bytes.AsSpan().SequenceEqual(existing))
                throw Stop("A transition destination conflicts with the retained preparation. Neither copy was overwritten.");
            return;
        }
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            PrivateFilePermissions.EnsureFile(temporary);
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
