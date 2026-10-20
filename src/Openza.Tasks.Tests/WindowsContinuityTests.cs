using System.Text.Json;
using Openza.Tasks.Application.Continuity;
using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Desktop.Services;

namespace Openza.Tasks.Tests;

public sealed class WindowsContinuityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"openza-continuity-{Guid.NewGuid():N}");
    private WindowsPackageDataContext DevContext => WindowsPackageDataContext.FromLocalFolder(
        OpenzaChannel.Dev, "Openza.OpenzaTasks.Dev", Path.Combine(_directory, "LocalState"));

    [Theory]
    [InlineData(OpenzaChannel.Dev, "Openza.OpenzaTasks.Dev", "tasks-dev")]
    [InlineData(OpenzaChannel.Preview, "Openza.OpenzaTasks.Preview", "tasks-preview")]
    [InlineData(OpenzaChannel.Production, "Openza.OpenzaTasks", "tasks")]
    public void Explicit_package_layout_preserves_localstate_without_creating_or_opening_it(
        OpenzaChannel channel, string name, string credentialNamespace)
    {
        var context = WindowsPackageDataContext.FromLocalFolder(channel, name, Path.Combine(_directory, "LocalState"));
        Assert.Equal(Path.Combine(_directory, "LocalState", "openza-tasks.db"), context.Runtime.DatabasePath);
        Assert.Equal(Path.Combine(_directory, "LocalState", "settings.json"), context.Runtime.SettingsPath);
        Assert.Equal(Path.Combine(_directory, "LocalState", "restore-points"), context.Runtime.RestorePointDirectory);
        Assert.Equal(Path.Combine(_directory, "LocalState", "personal-cloud-provisioning.json"), context.ProvisioningPath);
        Assert.Equal(Path.Combine(_directory, "LocalState", "personal-cloud-connection.json"), context.ConnectionPath);
        Assert.Equal(Path.Combine(_directory, "LocalState", "msal-cache.bin"), context.MicrosoftCachePath);
        Assert.Equal(credentialNamespace, context.Runtime.CredentialNamespace);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData(OpenzaChannel.Dev, "Openza.OpenzaTasks")]
    [InlineData(OpenzaChannel.Dev, "Openza.OpenzaTasks.Preview")]
    [InlineData(OpenzaChannel.Production, "Openza.OpenzaTasks.Dev")]
    [InlineData(OpenzaChannel.Preview, "Openza.OpenzaTasks")]
    public void Package_identity_channel_mismatch_cannot_select_data(OpenzaChannel channel, string name) =>
        Assert.Throws<ArgumentException>(() => WindowsPackageDataContext.FromLocalFolder(channel, name, _directory));

    [Fact]
    public void Relative_localfolder_and_unknown_channel_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => WindowsPackageDataContext.FromLocalFolder(OpenzaChannel.Dev, "Openza.OpenzaTasks.Dev", "relative"));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPackageDataContext.FromLocalFolder((OpenzaChannel)99, "Openza.OpenzaTasks", _directory));
    }

    [Fact]
    public async Task Settings_conversion_maps_semantics_retains_original_and_survives_desktop_save()
    {
        const string original = """
            {
              "Theme":"Dark", "LastView":"next", "LastSpaceId":"space_example",
              "WindowWidth":1000, "WindowHeight":700, "WindowIsMaximized":false,
              "AutoBackupEnabled":false, "AutoSyncEnabled":false, "ShowGetStarted":false,
              "OneDriveBackupEnabled":true, "OneDriveBackupEncryptionEnabled":true,
              "MicrosoftToDoAccount":{"HomeAccountId":"synthetic-account", "Username":"sample@example.invalid", "ConnectedAt":"2026-10-18T00:00:00Z"},
              "OneDriveBackupAccount":{"HomeAccountId":"synthetic-backup", "Username":"backup@example.invalid"},
              "TaskViewSettings":{"space_example|next|all":{"SortMode":"Title", "SortDirection":"Descending", "GroupMode":"Source", "Priority":3,"RepeatScope":"Only","LabelId":"label_sample"}},
              "TaskGroupModes":{"next":"Date", "today":"Priority"},
              "ProjectSortSettings":{"space:space_example":{"Mode":1,"Descending":true,"FavoritesFirst":false}},
              "AutoSyncIntervalMinutes":8, "FutureSetting":{"KeepThis":true}
            }
            """;
        var conversion = WinUiSettingsConverter.Convert(original);
        var result = conversion.Preferences;
        Assert.Equal(original, conversion.OriginalSettingsJson);
        Assert.Equal("Dark", result.Theme);
        Assert.Equal("NextActions", result.LastView);
        Assert.Equal("space_example", result.SelectedSpaceId);
        Assert.True(result.HasSelectedSpace);
        Assert.Equal(1000, result.WindowWidth);
        Assert.Equal(700, result.WindowHeight);
        Assert.False(result.WindowMaximized);
        Assert.False(result.AutomaticSyncEnabled);
        Assert.False(result.AutomaticRestorePointsEnabled);
        Assert.False(result.ShowGetStarted);
        Assert.True(result.OneDriveEnabled);
        Assert.True(result.OneDriveEncrypted);
        Assert.Equal("synthetic-account", result.MicrosoftToDoAccount!.HomeAccountId);
        Assert.Equal("synthetic-backup", result.OneDriveAccount!.HomeAccountId);
        Assert.True(conversion.RequiresMicrosoftAuthenticationValidation);
        Assert.Contains("AutoSyncIntervalMinutes", conversion.RetainedFields);
        Assert.Contains("FutureSetting", conversion.RetainedFields);
        var view = result.TaskViewSettings["space_example|next|all"];
        Assert.Equal(3, view.SortIndex);
        Assert.Equal(1, view.SortDirectionIndex);
        Assert.Equal(6, view.GroupIndex); // Explicit view record wins over legacy fallback.
        Assert.Equal(3, view.PriorityFilterIndex);
        Assert.Equal(2, view.RepeatFilterIndex);
        Assert.Equal("label_sample", view.LabelFilterId);
        Assert.Equal(4, result.TaskViewSettings["space_example|today|all"].GroupIndex);
        Assert.True(result.ProjectSortSettings["space:space_example"].Descending);
        Assert.False(result.ProjectSortSettings["space:space_example"].FavoritesFirst);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(WinUiSettingsConverter.Convert(original).Preferences));

        var path = Path.Combine(_directory, "converted-fixture.json");
        await new DesktopPreferencesStore(path).SaveAsync(result);
        var roundTrip = JsonSerializer.Deserialize<DesktopPreferences>(await File.ReadAllTextAsync(path))!;
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(roundTrip));
        Assert.Equal(original, conversion.OriginalSettingsJson);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"MicrosoftToDoAccount\":{\"HomeAccountId\":\"\"}}")]
    public void Default_settings_do_not_invent_a_selection_or_connected_account(string json)
    {
        var conversion = WinUiSettingsConverter.Convert(json);
        Assert.False(conversion.Preferences.HasSelectedSpace);
        Assert.Null(conversion.Preferences.SelectedSpaceId);
        Assert.Null(conversion.Preferences.MicrosoftToDoAccount);
        Assert.Equal("Inbox", conversion.Preferences.LastView);
        Assert.False(conversion.RequiresMicrosoftAuthenticationValidation);
        Assert.Equal(json, conversion.OriginalSettingsJson);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Date")]
    public void Legacy_tasks_grouping_cannot_replace_project_default_when_shared_record_is_absent(string group)
    {
        var original = JsonSerializer.Serialize(new
        {
            LastSpaceId = "space_example", LastView = "tasks",
            TaskGroupModes = new Dictionary<string, string> { ["tasks"] = group },
        });
        var conversion = WinUiSettingsConverter.Convert(original);
        Assert.Equal("Open", conversion.Preferences.LastView);
        Assert.False(conversion.Preferences.TaskViewSettings.ContainsKey("space_example|tasks|all"));
        Assert.Equal(new[] { "TaskGroupModes.tasks" }, conversion.RetainedFields);
        Assert.Equal(original, conversion.OriginalSettingsJson);
    }

    [Fact]
    public void Explicit_shared_tasks_grouping_is_preserved_instead_of_legacy_fallback()
    {
        const string original = """
            {"LastSpaceId":"space_example","TaskGroupModes":{"tasks":"None"},
             "TaskViewSettings":{"space_example|tasks|all":{"GroupMode":"Date"}}}
            """;
        var conversion = WinUiSettingsConverter.Convert(original);
        Assert.Equal(1, conversion.Preferences.TaskViewSettings["space_example|tasks|all"].GroupIndex);
        Assert.Contains("TaskGroupModes.tasks", conversion.RetainedFields);
        Assert.Equal(original, conversion.OriginalSettingsJson);
    }

    [Fact]
    public void Unsupported_members_inside_mapped_records_are_reported_with_their_paths()
    {
        const string original = """
            {"TaskViewSettings":{"space_example|next|all":{"SortMode":"Title","FutureTaskOption":{"Density":"compact"}}},
             "ProjectSortSettings":{"space:space_example":{"Mode":1,"FutureProjectOption":false}}}
            """;
        var conversion = WinUiSettingsConverter.Convert(original);
        Assert.Equal(3, conversion.Preferences.TaskViewSettings["space_example|next|all"].SortIndex);
        Assert.Equal(Openza.Tasks.Core.Data.ProjectSortMode.Name, conversion.Preferences.ProjectSortSettings["space:space_example"].Mode);
        Assert.Equal(new[]
        {
            "TaskViewSettings[\"space_example|next|all\"][\"FutureTaskOption\"]",
            "ProjectSortSettings[\"space:space_example\"][\"FutureProjectOption\"]",
        }, conversion.RetainedFields);
        Assert.Equal(original, conversion.OriginalSettingsJson);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"Theme\":\"Dark\",\"Theme\":\"Light\"}")]
    [InlineData("{\"Theme\":\"unsupported\"}")]
    [InlineData("{\"WindowWidth\":-1}")]
    [InlineData("{\"WindowHeight\":null}")]
    [InlineData("{\"LastView\":\"future-view\"}")]
    [InlineData("{\"TaskViewSettings\":null}")]
    [InlineData("{\"ProjectSortSettings\":{\"space:sample\":{\"Mode\":999}}}")]
    [InlineData("{\"TaskViewSettings\":{\"space|next|all\":{\"SortMode\":\"999\"}}}")]
    [InlineData("{\"TaskViewSettings\":{\"space|next|all\":{\"SortMode\":\"Future\"}}}")]
    [InlineData("{\"TaskViewSettings\":{\"space|next|all\":{\"Priority\":8}}}")]
    [InlineData("{\"TaskViewSettings\":{\"space|next|all\":{\"DateScope\":\"Future\"}}}")]
    [InlineData("{\"Future\":{\"Repeated\":1,\"Repeated\":2}}")]
    public void Ambiguous_corrupt_or_unrepresentable_settings_fail_without_defaults_or_writes(string json)
    {
        Assert.Throws<InvalidDataException>(() => WinUiSettingsConverter.Convert(json));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task Credential_copy_is_allowlisted_verifies_and_keeps_legacy_values_on_retry()
    {
        var source = new TestCredentials();
        source.Values["todoist.accessToken"] = "synthetic-todoist";
        source.Values["onedrive.backup.passphrase"] = "synthetic-passphrase";
        source.Values["github.accessToken"] = "synthetic-github";
        source.Values["unrelated-key"] = "must-not-be-read";
        var target = new TestCredentials();
        var transfer = new WindowsDevCredentialTransfer(DevContext, source, target);
        Assert.Equal(new WindowsCredentialTransferResult(3, 0, 0), await transfer.CopyAsync());
        Assert.Equal("synthetic-todoist", target.Values["todoist-token"]);
        Assert.Equal("synthetic-passphrase", target.Values["onedrive-backup-passphrase"]);
        Assert.Equal("synthetic-github", target.Values["github.accessToken"]);
        Assert.Equal(4, source.Values.Count);
        Assert.DoesNotContain("unrelated-key", source.Reads);
        Assert.Equal(new WindowsCredentialTransferResult(0, 3, 0), await transfer.CopyAsync());
        Assert.Equal(3, target.Writes);
    }

    [Fact]
    public async Task Later_key_conflict_prevents_all_writes_and_leaves_both_stores_unchanged()
    {
        var source = new TestCredentials();
        source.Values["todoist.accessToken"] = "synthetic-source";
        source.Values["github.accessToken"] = "synthetic-source-github";
        var target = new TestCredentials();
        target.Values["github.accessToken"] = "synthetic-existing-github";
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsDevCredentialTransfer(DevContext, source, target).CopyAsync());
        Assert.Equal(0, target.Writes);
        Assert.Single(target.Values);
        Assert.Equal(2, source.Values.Count);
    }

    [Fact]
    public async Task Interrupted_copy_retains_source_and_retry_completes_only_missing_keys()
    {
        var source = new TestCredentials();
        source.Values["todoist.accessToken"] = "synthetic-first";
        source.Values["onedrive.backup.passphrase"] = "synthetic-second";
        var target = new TestCredentials { FailWriteNumber = 2 };
        var transfer = new WindowsDevCredentialTransfer(DevContext, source, target);
        await Assert.ThrowsAsync<IOException>(() => transfer.CopyAsync());
        Assert.Equal("synthetic-first", target.Values["todoist-token"]);
        Assert.Equal(2, source.Values.Count);
        target.FailWriteNumber = null;
        Assert.Equal(new WindowsCredentialTransferResult(1, 1, 1), await transfer.CopyAsync());
    }

    [Fact]
    public async Task Destination_verification_failure_never_removes_legacy_credential()
    {
        var source = new TestCredentials();
        source.Values["todoist.accessToken"] = "synthetic-value";
        var target = new TestCredentials { CorruptWrites = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsDevCredentialTransfer(DevContext, source, target).CopyAsync());
        Assert.DoesNotContain("synthetic-value", error.Message);
        Assert.Equal("synthetic-value", source.Values["todoist.accessToken"]);
    }

    [Fact]
    public async Task Cancelled_or_failed_preflight_cannot_write_credentials()
    {
        var source = new TestCredentials { FailRead = true };
        var target = new TestCredentials();
        var transfer = new WindowsDevCredentialTransfer(DevContext, source, target);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer.CopyAsync(new CancellationToken(true)));
        Assert.Empty(source.Reads);
        await Assert.ThrowsAsync<IOException>(() => transfer.CopyAsync());
        Assert.Equal(0, target.Writes);
    }

    [Theory]
    [InlineData(OpenzaChannel.Production, "Openza.OpenzaTasks")]
    [InlineData(OpenzaChannel.Preview, "Openza.OpenzaTasks.Preview")]
    public void Transfer_rejects_live_lanes_before_any_store_access(OpenzaChannel channel, string package)
    {
        var context = WindowsPackageDataContext.FromLocalFolder(channel, package, _directory);
        var source = new TestCredentials();
        var target = new TestCredentials();
        Assert.Throws<InvalidOperationException>(() => new WindowsDevCredentialTransfer(context, source, target));
        Assert.Empty(source.Reads);
        Assert.Empty(target.Reads);
    }

    [Fact]
    public void Transfer_requires_distinct_stores()
    {
        var store = new TestCredentials();
        Assert.Throws<ArgumentException>(() => new WindowsDevCredentialTransfer(DevContext, store, store));
        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task Missing_source_values_do_not_clear_existing_destination_credentials()
    {
        var source = new TestCredentials();
        var target = new TestCredentials();
        target.Values["todoist-token"] = "synthetic-existing";
        var result = await new WindowsDevCredentialTransfer(DevContext, source, target).CopyAsync();
        Assert.Equal(new WindowsCredentialTransferResult(0, 0, 3), result);
        Assert.Equal("synthetic-existing", target.Values["todoist-token"]);
        Assert.Equal(0, target.Writes);
    }

    [Fact]
    public async Task Destination_change_detected_after_preflight_is_not_overwritten()
    {
        var source = new TestCredentials();
        source.Values["todoist.accessToken"] = "synthetic-source";
        var target = new TestCredentials { ChangeOnReadNumber = 4 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsDevCredentialTransfer(DevContext, source, target).CopyAsync());
        Assert.Equal("synthetic-concurrent", target.Values["todoist-token"]);
        Assert.Equal(0, target.Writes);
    }

    private sealed class TestCredentials : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = [];
        public int Writes { get; private set; }
        public int? FailWriteNumber { get; set; }
        public bool CorruptWrites { get; init; }
        public bool FailRead { get; init; }
        public int? ChangeOnReadNumber { get; init; }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads.Add(key);
            if (FailRead) throw new IOException("Synthetic read failure.");
            if (Reads.Count == ChangeOnReadNumber) Values[key] = "synthetic-concurrent";
            return Task.FromResult(Values.GetValueOrDefault(key));
        }
        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            if (Writes == FailWriteNumber) throw new IOException("Synthetic interrupted write.");
            Values[key] = CorruptWrites ? "synthetic-corruption" : value;
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Continuity must never remove source credentials.");
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
