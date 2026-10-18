using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.Services.Continuity;

namespace Openza.Tasks.Tests;

[Collection(nameof(EditorTestCollection))]
public sealed partial class WindowsContinuityIntegrationTests(EditorTestSession fixture) : IDisposable
{
    private const string ClientId = "11111111-1111-4111-8111-111111111111";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"openza-windows-transition-{Guid.NewGuid():N}");
    private readonly WindowsContinuityCoordinator _coordinator = new();
    private FakePlatform Platform => new(_root);
    private string Archive => Path.Combine(_root, "windows-continuity-v1");

    [Fact]
    public async Task Applies_sidecar_with_durable_original_and_database_copy_without_changing_source()
    {
        var platform = Platform;
        var original = await SeedAsync();
        var database = await File.ReadAllBytesAsync(Path.Combine(_root, "openza-tasks.db"));
        using var session = new WindowsContinuitySession(platform);
        await PrepareAsync(session, platform);
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(_root, "settings.json")));
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(Archive, "settings.json")));
        Assert.Equal(database, await File.ReadAllBytesAsync(session.Data.Runtime.DatabasePath));
        var converted = JsonSerializer.Deserialize<DesktopPreferences>(await File.ReadAllTextAsync(session.SettingsPath))!;
        Assert.Equal("Dark", converted.Theme);
        Assert.False(converted.AutomaticSyncEnabled);
        Assert.False(converted.OneDriveEnabled);
        Assert.NotEmpty(converted.WindowsContinuityNotices);
        Assert.True(File.Exists(Path.Combine(Archive, "complete.json")));
        using var restore = new SqliteConnection($"Data Source={Path.Combine(Archive, "original.db")};Mode=ReadOnly;Pooling=False");
        await restore.OpenAsync();
        using var command = restore.CreateCommand();
        command.CommandText = "SELECT title FROM tasks";
        Assert.Equal("Synthetic retained task", await command.ExecuteScalarAsync());
        command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await command.ExecuteScalarAsync());
        Assert.Equal(0, platform.LegacyReads);
        var applied = await File.ReadAllBytesAsync(session.SettingsPath);
        await PrepareAsync(session, platform);
        Assert.Equal(applied, await File.ReadAllBytesAsync(session.SettingsPath));
    }

    [Theory]
    [InlineData("archive-ready")]
    [InlineData("credentials-ready")]
    [InlineData("settings-applied")]
    public async Task Interrupted_preparation_resumes_idempotently_and_retains_original(string step)
    {
        var platform = Platform;
        var original = await SeedAsync();
        using (var session = new WindowsContinuitySession(platform))
            await Assert.ThrowsAsync<SyntheticInterruption>(() => _coordinator.PrepareAsync(session, platform, ClientId, "common", checkpoint: value =>
            { if (value == step) throw new SyntheticInterruption(); }));
        Assert.False(File.Exists(Path.Combine(Archive, "complete.json")));
        using (var session = new WindowsContinuitySession(platform)) await PrepareAsync(session, platform);
        Assert.True(File.Exists(Path.Combine(Archive, "complete.json")));
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(_root, "settings.json")));
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(Archive, "settings.json")));
    }

    [Fact]
    public async Task Changed_source_during_interruption_fails_without_reinterpreting_or_overwriting_it()
    {
        var platform = Platform;
        var original = await SeedAsync();
        using var session = new WindowsContinuitySession(platform);
        await Assert.ThrowsAsync<SyntheticInterruption>(() => _coordinator.PrepareAsync(session, platform, ClientId, "common", checkpoint: _ => throw new SyntheticInterruption()));
        await File.WriteAllTextAsync(session.Data.Runtime.SettingsPath, "{\"Theme\":\"Light\"}");
        await Assert.ThrowsAsync<WindowsContinuityException>(() => PrepareAsync(session, platform));
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(Archive, "settings.json")));
        Assert.Equal("{\"Theme\":\"Light\"}", await File.ReadAllTextAsync(session.Data.Runtime.SettingsPath));
        Assert.False(File.Exists(session.SettingsPath));
    }

    [Fact]
    public async Task Conflicting_destination_is_retained_after_interrupted_apply()
    {
        var platform = Platform;
        await SeedAsync();
        using var session = new WindowsContinuitySession(platform);
        await Assert.ThrowsAsync<SyntheticInterruption>(() => _coordinator.PrepareAsync(session, platform, ClientId, "common", checkpoint: step =>
        { if (step == "settings-applied") throw new SyntheticInterruption(); }));
        await File.WriteAllTextAsync(session.SettingsPath, "{\"Theme\":\"Light\"}");
        await Assert.ThrowsAsync<WindowsContinuityException>(() => PrepareAsync(session, platform));
        Assert.Equal("{\"Theme\":\"Light\"}", await File.ReadAllTextAsync(session.SettingsPath));
        Assert.False(File.Exists(Path.Combine(Archive, "complete.json")));
    }

    [Fact]
    public void Old_host_exclusion_failure_prevents_profile_and_credential_access()
    {
        var platform = Platform;
        platform.SourceBusy = true;
        Assert.Throws<WindowsContinuityException>(() => new WindowsContinuitySession(platform));
        Assert.Equal(0, platform.TargetCreations);
        Assert.Equal(0, platform.LegacyReads);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Registration_is_retained_until_session_exit_and_competing_start_cannot_prepare()
    {
        var platform = Platform;
        using (var session = new WindowsContinuitySession(platform))
        {
            Assert.True(platform.Registered);
            Assert.Throws<WindowsContinuityException>(() => new WindowsContinuitySession(platform));
        }
        Assert.False(platform.Registered);
        using var retry = new WindowsContinuitySession(platform);
        Assert.True(platform.Registered);
    }

    [Theory]
    [InlineData("Openza.OpenzaTasks", "CN=Openza")]
    [InlineData("Openza.OpenzaTasks.Preview", "CN=Openza")]
    [InlineData("Openza.OpenzaTasks.Dev", "CN=Other")]
    public void Identity_rejection_precedes_registration_files_and_vault(string name, string publisher)
    {
        var platform = Platform;
        platform.Name = name; platform.Publisher = publisher;
        Assert.Throws<WindowsContinuityException>(() => new WindowsContinuitySession(platform));
        Assert.False(platform.Registered);
        Assert.Equal(0, platform.TargetCreations);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Cloud_identity_and_pending_outbox_survive_without_source_credential_deletion()
    {
        var platform = Platform;
        await SeedAsync();
        var profile = await PairAsync(platform, pending: true);
        using var session = new WindowsContinuitySession(platform);
        await PrepareAsync(session, platform);
        Assert.Equal(profile, new JsonPersonalCloudConnectionStore(session.Data.ConnectionPath).Load());
        Assert.Equal(platform.Legacy.Values[CloudKey(profile)], platform.Target.Values[CloudKey(profile)]);
        Assert.Equal(new[] { CloudKey(profile) }, platform.Legacy.Reads);
        using var db = new SqliteConnection($"Data Source={session.Data.Runtime.DatabasePath};Mode=ReadOnly;Pooling=False");
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT canonical_json FROM personal_cloud_outbox WHERE acknowledged_at IS NULL";
        Assert.Equal("synthetic-pending-intent", await command.ExecuteScalarAsync());
        command.CommandText = "SELECT next_device_sequence FROM personal_cloud_device_state";
        Assert.Equal(4L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Wrong_or_missing_cloud_identity_blocks_before_settings_apply_and_keeps_pending_work()
    {
        var platform = Platform;
        await SeedAsync();
        var profile = await PairAsync(platform, pending: true);
        platform.Legacy.Values[CloudKey(profile)] = CloudCredential(profile, device: "other-device");
        using var session = new WindowsContinuitySession(platform);
        await Assert.ThrowsAsync<WindowsContinuityException>(() => PrepareAsync(session, platform));
        Assert.False(File.Exists(session.SettingsPath));
        Assert.Empty(platform.Target.Values);
        Assert.True(File.Exists(Path.Combine(Archive, "original.db")));
    }

    [Fact]
    public async Task Pending_cloud_operations_without_connection_never_become_an_unpaired_local_workspace()
    {
        var platform = Platform;
        await SeedAsync();
        await PairAsync(platform, pending: true);
        File.Delete(Path.Combine(_root, "personal-cloud-connection.json"));
        using var session = new WindowsContinuitySession(platform);
        await Assert.ThrowsAsync<WindowsContinuityException>(() => PrepareAsync(session, platform));
        Assert.False(File.Exists(session.SettingsPath));
        Assert.Equal(0, platform.LegacyReads);
    }

    [Fact]
    public async Task Completed_transition_does_not_resurrect_cloud_pairing_after_disconnect()
    {
        var platform = Platform;
        await SeedAsync();
        var profile = await PairAsync(platform, pending: false);
        using var session = new WindowsContinuitySession(platform);
        await PrepareAsync(session, platform);
        var key = CloudKey(profile);
        platform.Target.Values.Remove(key);
        File.Delete(session.Data.ConnectionPath);
        var reads = platform.LegacyReads;
        var writes = platform.Target.Writes;
        await PrepareAsync(session, platform);
        Assert.False(platform.Target.Values.ContainsKey(key));
        Assert.Equal(reads, platform.LegacyReads);
        Assert.Equal(writes, platform.Target.Writes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("mismatch")]
    public async Task Completed_transition_with_invalid_identity_still_requires_recovery_and_retains_intent(string state)
    {
        var platform = Platform;
        await SeedAsync();
        var profile = await PairAsync(platform, pending: true);
        using var session = new WindowsContinuitySession(platform);
        await PrepareAsync(session, platform);
        await new DesktopPreferencesStore(session.SettingsPath).UpdateAsync(value => value with { AutomaticSyncEnabled = true });
        if (state == "missing") platform.Target.Values.Remove(CloudKey(profile));
        else platform.Target.Values[CloudKey(profile)] = state == "corrupt" ? "synthetic-invalid-json" : CloudCredential(profile, "other-device");
        var credentials = platform.Target.Values.ToArray();
        var writes = platform.Target.Writes;
        var reads = platform.LegacyReads;
        var database = await File.ReadAllBytesAsync(session.Data.Runtime.DatabasePath);
        await PrepareAsync(session, platform);
        Assert.Equal(reads, platform.LegacyReads);
        Assert.Equal(writes, platform.Target.Writes);
        Assert.Equal(credentials, platform.Target.Values.ToArray());
        var preferences = new DesktopPreferencesStore(session.SettingsPath).Load();
        Assert.True(preferences.WindowsCloudRecoveryRequired);
        Assert.False(preferences.AutomaticSyncEnabled);
        Assert.Contains(preferences.WindowsContinuityNotices, value => value.Contains("Local tasks and pending operations remain", StringComparison.Ordinal));
        Assert.Equal(database, await File.ReadAllBytesAsync(session.Data.Runtime.DatabasePath));
    }

    [Theory]
    [InlineData("com", true)]
    [InlineData("com", false)]
    [InlineData("access", true)]
    [InlineData("access", false)]
    [InlineData("cancel", true)]
    [InlineData("cancel", false)]
    public async Task Repeated_access_failure_or_cancellation_does_not_persist_recovery_or_change_user_sync_choice(string kind, bool syncEnabled)
    {
        var platform = Platform;
        await SeedAsync();
        await PairAsync(platform, pending: true);
        using var session = new WindowsContinuitySession(platform);
        await PrepareAsync(session, platform);
        var preferences = new DesktopPreferencesStore(session.SettingsPath);
        await preferences.UpdateAsync(value => value with { AutomaticSyncEnabled = syncEnabled });
        var savedSettings = await File.ReadAllBytesAsync(session.SettingsPath);
        var savedConnection = await File.ReadAllBytesAsync(session.Data.ConnectionPath);
        var savedDatabase = await File.ReadAllBytesAsync(session.Data.Runtime.DatabasePath);
        var savedReceipt = await File.ReadAllBytesAsync(Path.Combine(Archive, "complete.json"));
        var credentials = platform.Target.Values.ToArray();
        var writes = platform.Target.Writes;
        var legacyReads = platform.LegacyReads;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            platform.Target.ReadFailure = kind switch
            {
                "com" => new System.Runtime.InteropServices.COMException("Synthetic vault unavailable", unchecked((int)0x800706BA)),
                "access" => new UnauthorizedAccessException("Synthetic credential access unavailable"),
                _ => new OperationCanceledException(new CancellationToken(true)),
            };
            var failure = await Record.ExceptionAsync(() => PrepareAsync(session, platform));
            if (kind == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(failure);
            else { Assert.Null(failure); Assert.True(session.CloudAccessInhibited); }
            Assert.Equal(savedSettings, await File.ReadAllBytesAsync(session.SettingsPath));
            Assert.False(preferences.Load().WindowsCloudRecoveryRequired);
            Assert.Equal(syncEnabled, preferences.Load().AutomaticSyncEnabled);
        }
        platform.Target.ReadFailure = null;
        await PrepareAsync(session, platform);
        Assert.Equal(savedSettings, await File.ReadAllBytesAsync(session.SettingsPath));
        Assert.Equal(savedConnection, await File.ReadAllBytesAsync(session.Data.ConnectionPath));
        Assert.Equal(savedDatabase, await File.ReadAllBytesAsync(session.Data.Runtime.DatabasePath));
        Assert.Equal(savedReceipt, await File.ReadAllBytesAsync(Path.Combine(Archive, "complete.json")));
        Assert.Equal(credentials, platform.Target.Values.ToArray());
        Assert.Equal(writes, platform.Target.Writes);
        Assert.Equal(legacyReads, platform.LegacyReads);
    }

    [Fact]
    public async Task Completed_disconnect_with_pending_intent_pauses_sync_without_restoring_source_identity()
    {
        var platform = Platform;
        await SeedAsync();
        var profile = await PairAsync(platform, pending: true);
        using var session = new WindowsContinuitySession(platform);
        await PrepareAsync(session, platform);
        File.Delete(session.Data.ConnectionPath);
        platform.Target.Values.Remove(CloudKey(profile));
        var reads = platform.LegacyReads;
        await PrepareAsync(session, platform);
        Assert.True(new DesktopPreferencesStore(session.SettingsPath).Load().WindowsCloudRecoveryRequired);
        Assert.Equal(reads, platform.LegacyReads);
        Assert.Empty(platform.Target.Values);
        using var db = new SqliteConnection($"Data Source={session.Data.Runtime.DatabasePath};Mode=ReadOnly;Pooling=False");
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT canonical_json FROM personal_cloud_outbox WHERE acknowledged_at IS NULL";
        Assert.Equal("synthetic-pending-intent", await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("serviceOrigin", true)]
    [InlineData("instanceId", true)]
    [InlineData("deviceId", true)]
    [InlineData("artifactVersion", true)]
    public async Task Corrupt_completed_connection_can_be_repaired_explicitly_with_original_retained(string? nullField, bool pending)
    {
        var platform = Platform;
        await SeedAsync();
        var profile = await PairAsync(platform, pending);
        using var session = new WindowsContinuitySession(platform);
        await PrepareAsync(session, platform);
        var invalid = "synthetic-invalid-json";
        if (nullField is not null)
        {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(session.Data.ConnectionPath))!.AsObject();
            json[nullField] = null;
            invalid = json.ToJsonString();
        }
        await File.WriteAllTextAsync(session.Data.ConnectionPath, invalid);
        await PrepareAsync(session, platform);
        Assert.True(new DesktopPreferencesStore(session.SettingsPath).Load().WindowsCloudRecoveryRequired);
        Assert.False(new DesktopPreferencesStore(session.SettingsPath).Load().AutomaticSyncEnabled);
        Assert.True(File.Exists(session.SettingsPath));
        Assert.Equal(invalid, await File.ReadAllTextAsync(session.Data.ConnectionPath));
        var recovery = new WindowsRecoveryConnectionStore(session.Data.ConnectionPath);
        Assert.Null(recovery.Load());
        await recovery.SaveAsync(profile);
        var retained = Directory.GetFiles(_root, "connection-recovery-*.json");
        Assert.Single(retained);
        Assert.Equal(invalid, await File.ReadAllTextAsync(retained[0]));
        var reads = platform.LegacyReads;
        await PrepareAsync(session, platform);
        Assert.False(new DesktopPreferencesStore(session.SettingsPath).Load().WindowsCloudRecoveryRequired);
        Assert.DoesNotContain(WindowsContinuityCoordinator.CloudRecoveryNotice, new DesktopPreferencesStore(session.SettingsPath).Load().WindowsContinuityNotices);
        Assert.Equal(reads, platform.LegacyReads);
        using var db = new SqliteConnection($"Data Source={session.Data.Runtime.DatabasePath};Mode=ReadOnly;Pooling=False");
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM personal_cloud_outbox WHERE acknowledged_at IS NULL";
        Assert.Equal(pending ? 1L : 0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Disposed_source_session_cannot_prepare_or_release_a_new_registration()
    {
        var platform = Platform;
        var first = new WindowsContinuitySession(platform);
        first.Dispose();
        using var next = new WindowsContinuitySession(platform);
        first.Dispose();
        Assert.True(platform.Registered);
        await Assert.ThrowsAsync<WindowsContinuityException>(() => PrepareAsync(first, platform));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Protected_cache_failure_is_explicit_reauth_and_is_not_retried_after_completed_disconnect()
    {
        var platform = Platform;
        await SeedAsync("{\"MicrosoftToDoAccount\":{\"HomeAccountId\":\"account-test\",\"Username\":\"test@example.invalid\"}}");
        await File.WriteAllBytesAsync(Path.Combine(_root, "msal-cache.bin"), [1, 2, 3]);
        platform.DecryptFailure = true;
        using var session = new WindowsContinuitySession(platform);
        await PrepareAsync(session, platform);
        var preferences = JsonSerializer.Deserialize<DesktopPreferences>(await File.ReadAllTextAsync(session.SettingsPath))!;
        Assert.Contains(preferences.WindowsContinuityNotices, value => value.Contains("Microsoft To Do cache was not imported", StringComparison.Ordinal));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(_root, "msal-cache.bin")));
        await new DesktopPreferencesStore(session.SettingsPath).SaveAsync(preferences with { MicrosoftToDoAccount = null });
        var decrypts = platform.Decryptions;
        await PrepareAsync(session, platform);
        Assert.Equal(decrypts, platform.Decryptions);
        Assert.Null(new DesktopPreferencesStore(session.SettingsPath).Load().MicrosoftToDoAccount);
    }

    [Fact]
    public async Task Invalid_or_other_client_cache_never_silently_imports_a_selected_account()
    {
        var target = new RecordingStore();
        var notices = await WindowsCredentialContinuity.TransferMicrosoftAsync(Encoding.UTF8.GetBytes("{}"),
            new() { MicrosoftToDoAccount = new("missing-account", "test@example.invalid") }, ClientId, "common", bytes => bytes.ToArray(), target, default);
        Assert.Single(notices);
        Assert.Empty(target.Values);
    }

    [Fact]
    public async Task Unsupported_cache_bytes_produce_reauth_notice_without_source_replacement()
    {
        var target = new RecordingStore();
        var source = Encoding.UTF8.GetBytes("synthetic-invalid-cache");
        var notices = await WindowsCredentialContinuity.TransferMicrosoftAsync(source,
            new() { MicrosoftToDoAccount = new("account-test", "test@example.invalid") }, ClientId, "common", bytes => bytes.ToArray(), target, default);
        Assert.Single(notices);
        Assert.Equal("synthetic-invalid-cache", Encoding.UTF8.GetString(source));
        Assert.Empty(target.Values);
    }

    [Fact]
    public async Task Cache_roundtrip_uses_msal_serialization_and_matches_application_account_offline()
    {
        var target = new RecordingStore();
        var cache = SyntheticMsalCache();
        var preferences = new DesktopPreferences { MicrosoftToDoAccount = new("uid.utid", "test@example.invalid") };
        var notices = await WindowsCredentialContinuity.TransferMicrosoftAsync(cache, preferences, ClientId, "common", bytes => bytes.ToArray(), target, default);
        Assert.Empty(notices);
        Assert.True(target.Values.ContainsKey("microsoft-todo-msal-cache"));
        var wrongClient = new RecordingStore();
        var mismatch = await WindowsCredentialContinuity.TransferMicrosoftAsync(cache, preferences, "11111111-2222-3333-4444-555555555555", "common", bytes => bytes.ToArray(), wrongClient, default);
        Assert.Single(mismatch);
        Assert.Empty(wrongClient.Values);
    }

    [Fact]
    public async Task Corrupt_completion_or_retained_original_never_resets_to_default_settings()
    {
        var platform = Platform;
        await SeedAsync();
        using var session = new WindowsContinuitySession(platform);
        await PrepareAsync(session, platform);
        var applied = await File.ReadAllBytesAsync(session.SettingsPath);
        await File.WriteAllTextAsync(Path.Combine(Archive, "settings.json"), "{}");
        await Assert.ThrowsAsync<WindowsContinuityException>(() => PrepareAsync(session, platform));
        Assert.Equal(applied, await File.ReadAllBytesAsync(session.SettingsPath));
    }

    [Fact]
    public async Task Cancelled_preparation_does_not_apply_settings()
    {
        var platform = Platform;
        await SeedAsync();
        using var session = new WindowsContinuitySession(platform);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _coordinator.PrepareAsync(session, platform, ClientId, "common", new(true)));
        Assert.False(File.Exists(session.SettingsPath));
        Assert.Empty(platform.Target.Values);
    }

    private Task PrepareAsync(WindowsContinuitySession session, FakePlatform platform) => _coordinator.PrepareAsync(session, platform, ClientId, "common");

    private async Task<string> SeedAsync(string original = "{\"Theme\":\"Dark\",\"AutoSyncEnabled\":true,\"OneDriveBackupEnabled\":true}")
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), original);
        using var db = new SqliteConnection($"Data Source={Path.Combine(_root, "openza-tasks.db")};Pooling=False");
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE tasks(id TEXT PRIMARY KEY,title TEXT); INSERT INTO tasks VALUES('task-test','Synthetic retained task');
            CREATE TABLE personal_cloud_device_state(instance_id TEXT,device_id TEXT,next_device_sequence INTEGER);
            CREATE TABLE personal_cloud_outbox(instance_id TEXT,device_id TEXT,canonical_json TEXT,acknowledged_at INTEGER);
            CREATE TABLE personal_cloud_checkpoints(instance_id TEXT,device_id TEXT,last_change_seq INTEGER);
            """;
        await command.ExecuteNonQueryAsync();
        return original;
    }

    private async Task<PersonalCloudConnectionProfile> PairAsync(FakePlatform platform, bool pending)
    {
        var profile = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new("https://example.invalid/"), InstanceId = "instance-test", DeviceId = "device-test",
            ArtifactVersion = "synthetic", ConnectedAt = DateTimeOffset.UtcNow,
        };
        await new JsonPersonalCloudConnectionStore(Path.Combine(_root, "personal-cloud-connection.json")).SaveAsync(profile);
        platform.Legacy.Values[CloudKey(profile)] = CloudCredential(profile);
        using var db = new SqliteConnection($"Data Source={Path.Combine(_root, "openza-tasks.db")};Pooling=False");
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO personal_cloud_device_state VALUES('instance-test','device-test',4); INSERT INTO personal_cloud_checkpoints VALUES('instance-test','device-test',8);";
        if (pending) command.CommandText += "INSERT INTO personal_cloud_outbox VALUES('instance-test','device-test','synthetic-pending-intent',NULL);";
        await command.ExecuteNonQueryAsync();
        return profile;
    }

    private static string CloudKey(PersonalCloudConnectionProfile profile) => "personal-cloud-device-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ServiceOrigin.AbsoluteUri)));
    private static string CloudCredential(PersonalCloudConnectionProfile profile, string? device = null) => JsonSerializer.Serialize(new
    { serviceOrigin = profile.ServiceOrigin.AbsoluteUri, instanceId = profile.InstanceId, deviceId = device ?? profile.DeviceId, refreshCredential = new string('s', 32) });
    private static byte[] SyntheticMsalCache() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        Account = new Dictionary<string, object> { ["uid.utid-login.microsoftonline.com-utid"] = new
        { home_account_id = "uid.utid", environment = "login.microsoftonline.com", realm = "utid", local_account_id = "uid", username = "test@example.invalid", authority_type = "MSSTS" } },
        RefreshToken = new Dictionary<string, object> { [$"uid.utid-login.microsoftonline.com-refreshtoken-{ClientId}--"] = new
        { home_account_id = "uid.utid", environment = "login.microsoftonline.com", credential_type = "RefreshToken", client_id = ClientId, secret = "synthetic-refresh-value" } },
        AccessToken = new { }, IdToken = new { }, AppMetadata = new { },
    }));

    private sealed class FakePlatform(string root) : IWindowsContinuityPlatform
    {
        public string Name { get; set; } = "Openza.OpenzaTasks.Dev";
        public string Publisher { get; set; } = "CN=Openza";
        public bool SourceBusy { get; set; }
        public bool Registered { get; private set; }
        public bool DecryptFailure { get; set; }
        public int Decryptions { get; private set; }
        public int TargetCreations { get; private set; }
        public int LegacyReads => Legacy.Reads.Count;
        public RecordingStore Target { get; } = new();
        public RecordingStore Legacy { get; } = new();
        public WindowsPackageEvidence GetCurrentPackage() => new(Name, Publisher, Name + "_synthetic", root);
        public IDisposable AcquireSourceExclusion()
        {
            if (SourceBusy || Registered) throw new WindowsContinuityException("Synthetic old-host writer still owns registration.");
            Registered = true;
            return new Registration(() => Registered = false);
        }
        public ICredentialStore CreatePackageCredentialStore(WindowsPackageEvidence package) { TargetCreations++; return Target; }
        public ICredentialStore OpenLegacyCloudCredentialReader() => Legacy;
        public byte[] UnprotectMicrosoftCache(byte[] protectedBytes)
        {
            Decryptions++;
            if (DecryptFailure) throw new CryptographicException("Synthetic DPAPI failure.");
            return protectedBytes.ToArray();
        }
    }
    private sealed class Registration(Action release) : IDisposable { public void Dispose() => release(); }
    private sealed class SyntheticInterruption : Exception;
    private sealed class RecordingStore : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = [];
        public int Writes { get; private set; }
        public Exception? ReadFailure { get; set; }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads.Add(key);
            if (ReadFailure is not null) throw ReadFailure;
            return Task.FromResult(Values.GetValueOrDefault(key));
        }
        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        { Writes++; Values[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No source credential deletion is permitted.");
    }

    public void Dispose() => TestDirectory.Delete(_root);
}
