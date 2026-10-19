using System.Reflection;
using Avalonia.Controls;
using Openza.Tasks.Application.Sync;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.Services.Continuity;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed partial class WindowsContinuityIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Actual_session_composition_blocks_both_sync_paths_before_default_or_injected_auth_and_recovers_on_restart(bool com, bool defaultAuth)
    {
        await fixture.Session.Dispatch(async () =>
        {
            var (platform, profile, store, preferences) = await PrepareCompletedCompositionAsync();
            var paths = new[] { Path.Combine(_root, "settings.avalonia.json"), Path.Combine(_root, "personal-cloud-connection.json"),
                Path.Combine(_root, "personal-cloud-provisioning.json"), store.DatabasePath, Path.Combine(Archive, "complete.json") };
            var originals = await Task.WhenAll(paths.Select(path => File.ReadAllBytesAsync(path)));
            var probe = new CompositionProbe();
            var cloud = new CompositionCloud(profile, probe);
            using (var inhibited = new WindowsContinuitySession(platform))
            {
                platform.Target.ReadFailure = com ? new System.Runtime.InteropServices.COMException("Synthetic vault outage") : new UnauthorizedAccessException("Synthetic vault outage");
                await PrepareAsync(inhibited, platform);
                Assert.True(inhibited.CloudAccessInhibited);
                for (var i = 0; i < paths.Length; i++) Assert.Equal(originals[i], await File.ReadAllBytesAsync(paths[i]));
                var reads = platform.Target.Reads.Count;
                var writes = platform.Target.Writes;
                var legacyReads = platform.LegacyReads;
                // Pass the unguarded vault and cloud deliberately. The actual session must resolve their guarded replacements.
                var vm = CreateCompositionViewModel(store, preferences, platform, inhibited, cloud, probe, defaultAuth);
                AssertCompositionIdentity(vm, inhibited, cloud, defaultAuth);
                var window = new Openza.Tasks.Desktop.Shell.MainWindow(vm);
                try
                {
                    window.Show();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    while (vm.SelectedNavigation is null || vm.IsBusy || !vm.StatusMessage.Contains("Credential access", StringComparison.Ordinal)) await Task.Delay(10, deadline.Token);
                    Assert.True(window.FindControl<Button>("SettingsNavigationButton")!.IsEnabled);
                    Assert.True(vm.AutomaticSyncEnabled);
                    Assert.False(preferences.Load().WindowsCloudRecoveryRequired);
                    await vm.SelectTaskAsync(Assert.Single(vm.Tasks));
                    var before = (await store.GetTaskAsync("retained-composed"))!;
                    var pending = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(profile.InstanceId, profile.DeviceId));
                    var checkpoint = await store.GetPersonalCloudCheckpointAsync(profile.InstanceId, profile.DeviceId);
                    var settings = await File.ReadAllBytesAsync(paths[0]);
                    var lastSync = vm.LastSyncResult;
                    vm.DetailTitle = "Unsaved local draft during inhibited sync";
                    await vm.RunTodoistSyncAsync();
                    await vm.RunAutomaticTodoistSyncAsync();
                    Assert.Equal(lastSync, vm.LastSyncResult); // Neither path entered sync orchestration/auth, including the real default auth object.
                    Assert.Equal(WindowsContinuitySession.CloudAccessNotice, vm.StatusMessage);
                    Assert.Equivalent(before, await store.GetTaskAsync(before.Id), strict: true); // Guard precedes save, too.
                    Assert.Equal(pending, Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(profile.InstanceId, profile.DeviceId)));
                    Assert.Equal(settings, await File.ReadAllBytesAsync(paths[0]));
                    Assert.Equal(0, probe.AuthRequests);
                    Assert.Equal(0, probe.ProviderFactories);
                    Assert.Equal(0, probe.ProviderRequests);
                    Assert.Equal(0, probe.CloudRequests);
                    Assert.Equal(0, probe.BridgeRequests);
                    Assert.Equal(0, probe.ProgressReads);
                    Assert.Equal(reads, platform.Target.Reads.Count);
                    Assert.Equal(writes, platform.Target.Writes);
                    Assert.Equal(legacyReads, platform.LegacyReads);

                    // The extracted resolver itself also holds session credentials rather than the raw constructor vault.
                    var resolver = CompositionField<IConnectedSyncProviderResolver>(CompositionField<ConnectedSyncApplicationService>(vm));
                    Assert.Null(await resolver.ResolveAsync(IntegrationIds.Todoist));
                    Assert.Equal(reads, platform.Target.Reads.Count);
                    Assert.True(await vm.SaveSelectedAsync());
                    Assert.Equal("Unsaved local draft during inhibited sync", (await store.GetTaskAsync(before.Id))!.Title);
                    var after = await store.GetPendingPersonalCloudOutboxAsync(profile.InstanceId, profile.DeviceId);
                    Assert.Equal(2, after.Count);
                    Assert.Contains(pending, after);
                    Assert.Equal(checkpoint, await store.GetPersonalCloudCheckpointAsync(profile.InstanceId, profile.DeviceId));
                    Assert.Equal(settings, await File.ReadAllBytesAsync(paths[0]));
                    for (var i = 1; i < paths.Length; i++)
                        if (paths[i] != store.DatabasePath) Assert.Equal(originals[i], await File.ReadAllBytesAsync(paths[i]));
                    Assert.True(preferences.Load().AutomaticSyncEnabled);
                    Assert.False(preferences.Load().WindowsCloudRecoveryRequired);
                    platform.Target.ReadFailure = null;
                    await PrepareAsync(inhibited, platform);
                    Assert.True(inhibited.CloudAccessInhibited); // Same-session retries never lift the barrier.
                }
                finally { window.Close(); await Task.Delay(100); }
            }

            using var healthy = new WindowsContinuitySession(platform);
            await PrepareAsync(healthy, platform);
            Assert.False(healthy.CloudAccessInhibited);
            Assert.True(preferences.Load().AutomaticSyncEnabled);
            Assert.False(preferences.Load().WindowsCloudRecoveryRequired);
            var healthyProbe = new CompositionProbe();
            var healthyCloud = new CompositionCloud(profile, healthyProbe);
            var ready = CreateCompositionViewModel(store, preferences, platform, healthy, healthyCloud, healthyProbe, defaultAuth: false);
            await ready.InitializeAsync();
            var retained = (await store.GetTaskAsync("retained-composed"))!;
            var retainedPending = await store.GetPendingPersonalCloudOutboxAsync(profile.InstanceId, profile.DeviceId);
            var healthySettings = await File.ReadAllBytesAsync(paths[0]);
            await ready.RunTodoistSyncAsync();
            await ready.RunAutomaticTodoistSyncAsync();
            Assert.Equal(2, healthyProbe.AuthRequests);
            Assert.Equal(4, healthyProbe.ProviderFactories);
            Assert.Equal(4, healthyProbe.ProviderRequests);
            Assert.Equal(4, healthyProbe.CloudRequests);
            Assert.Equal(2, healthyProbe.BridgeRequests);
            Assert.Equivalent(retained, await store.GetTaskAsync(retained.Id), strict: true);
            Assert.Equal(retainedPending, await store.GetPendingPersonalCloudOutboxAsync(profile.InstanceId, profile.DeviceId));
            Assert.Equal(healthySettings, await File.ReadAllBytesAsync(paths[0]));
            Assert.Equal(originals[1], await File.ReadAllBytesAsync(paths[1]));
            Assert.True(preferences.Load().AutomaticSyncEnabled);
            Assert.False(preferences.Load().WindowsCloudRecoveryRequired);
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("unauthorized", true)]
    [InlineData("health_mismatch", false)]
    public async Task Extracted_sync_retains_only_the_existing_unauthorized_identity_recovery_policy(string code, bool recoveryRequired)
    {
        await fixture.Session.Dispatch(async () =>
        {
            var (platform, profile, store, preferences) = await PrepareCompletedCompositionAsync();
            using var session = new WindowsContinuitySession(platform);
            await PrepareAsync(session, platform);
            var probe = new CompositionProbe();
            var cloud = new CompositionCloud(profile, probe) { ErrorCode = code };
            var vm = CreateCompositionViewModel(store, preferences, platform, session, cloud, probe, defaultAuth: false);
            await vm.InitializeAsync();
            var pairing = await File.ReadAllBytesAsync(Path.Combine(_root, "personal-cloud-connection.json"));
            var pending = await store.GetPendingPersonalCloudOutboxAsync(profile.InstanceId, profile.DeviceId);
            await vm.RunTodoistSyncAsync();
            Assert.Equal(recoveryRequired, preferences.Load().WindowsCloudRecoveryRequired);
            Assert.Equal(!recoveryRequired, preferences.Load().AutomaticSyncEnabled);
            Assert.Equal(!recoveryRequired, vm.AutomaticSyncEnabled);
            Assert.Equal(1, probe.CloudRequests);
            Assert.Equal(pairing, await File.ReadAllBytesAsync(Path.Combine(_root, "personal-cloud-connection.json")));
            Assert.Equal(pending, await store.GetPendingPersonalCloudOutboxAsync(profile.InstanceId, profile.DeviceId));
            return true;
        }, CancellationToken.None);
    }

    private async Task<(FakePlatform Platform, PersonalCloudConnectionProfile Profile, SqliteTaskStore Store, DesktopPreferencesStore Preferences)> PrepareCompletedCompositionAsync()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "{}");
        var store = new SqliteTaskStore(Path.Combine(_root, "openza-tasks.db"));
        await store.InitializeAsync();
        var platform = Platform;
        var profile = new PersonalCloudConnectionProfile { ServiceOrigin = new("https://example.invalid/"), InstanceId = "instance-composed",
            DeviceId = "device-composed", ArtifactVersion = "synthetic", ConnectedAt = DateTimeOffset.Parse("2026-10-19T00:00:00Z") };
        await new JsonPersonalCloudConnectionStore(Path.Combine(_root, "personal-cloud-connection.json")).SaveAsync(profile);
        platform.Legacy.Values[CloudKey(profile)] = CloudCredential(profile);
        await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem { Id = "retained-composed", Title = "Synthetic composed task" }, profile.InstanceId, profile.DeviceId);
        using (var initial = new WindowsContinuitySession(platform)) await PrepareAsync(initial, platform);
        platform.Target.Values["todoist-token"] = "synthetic-token";
        await File.WriteAllTextAsync(Path.Combine(_root, "personal-cloud-provisioning.json"), "{}");
        var preferences = new DesktopPreferencesStore(Path.Combine(_root, "settings.avalonia.json"));
        await preferences.UpdateAsync(value => value with { AutomaticSyncEnabled = true, AutomaticRestorePointsEnabled = false,
            ShowGetStarted = false, LastView = "Open", HasSelectedSpace = true, SelectedSpaceId = SpaceIds.Default,
            MicrosoftToDoAccount = new("synthetic-account", "synthetic@example.invalid") });
        return (platform, profile, store, preferences);
    }

    private static MainWindowViewModel CreateCompositionViewModel(SqliteTaskStore store, DesktopPreferencesStore preferences, FakePlatform platform,
        WindowsContinuitySession session, CompositionCloud cloud, CompositionProbe probe, bool defaultAuth) =>
        new(store, platform.Target, preferencesStore: preferences, continuitySession: session, personalCloudSetup: cloud,
            microsoftAuth: defaultAuth ? null : new CompositionAuth(probe),
            todoistSyncProviderFactory: _ => { probe.ProviderFactories++; return new CompositionProvider(IntegrationIds.Todoist, probe); },
            microsoftSyncProviderFactory: _ => { probe.ProviderFactories++; return new CompositionProvider(IntegrationIds.MicrosoftToDo, probe); });

    private static void AssertCompositionIdentity(MainWindowViewModel vm, WindowsContinuitySession session, CompositionCloud raw, bool defaultAuth)
    {
        Assert.Same(session.AppCredentials, CompositionField<ICredentialStore>(vm));
        var connected = CompositionField<ConnectedSyncApplicationService>(vm);
        var resolver = CompositionField<IConnectedSyncProviderResolver>(connected);
        Assert.Same(session.AppCredentials, CompositionField<ICredentialStore>(resolver));
        Assert.Same(CompositionField<IDesktopMicrosoftAuthService>(vm), CompositionField<IDesktopMicrosoftAuthService>(resolver));
        Assert.NotSame(raw, CompositionField<IPersonalCloudSetupService>(vm));
        Assert.Same(CompositionField<IPersonalCloudSetupService>(vm), CompositionField<IPersonalCloudSetupService>(connected));
        if (defaultAuth)
        {
            var auth = Assert.IsType<DesktopMicrosoftAuthService>(CompositionField<IDesktopMicrosoftAuthService>(vm));
            Assert.Same(session.AppCredentials, CompositionField<ICredentialStore>(auth));
        }
    }

    private static T CompositionField<T>(object instance) => (T)instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(field => field.FieldType == typeof(T)).GetValue(instance)!;

    private sealed class CompositionProbe
    {
        public int AuthRequests, ProviderFactories, ProviderRequests, CloudRequests, BridgeRequests, ProgressReads;
    }
    private sealed class CompositionAuth(CompositionProbe probe) : IDesktopMicrosoftAuthService
    {
        public Task<string?> GetTokenAsync(string feature, MicrosoftAccount? account, CancellationToken cancellationToken = default)
        { probe.AuthRequests++; return Task.FromResult<string?>("synthetic-microsoft-token"); }
        public Task<MicrosoftAccess> ConnectAsync(string feature, Func<SignInCode, Task> showCode, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected interactive sign-in");
        public Task DisconnectAsync(string feature) => throw new InvalidOperationException("Unexpected disconnect");
    }
    private sealed class CompositionProvider(string integrationId, CompositionProbe probe) : ISyncProvider
    {
        public string IntegrationId => integrationId;
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
        { probe.ProviderRequests++; return Task.FromResult(new ProviderSnapshot([], [], [])); }
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected provider write");
    }
    private sealed class CompositionCloud(PersonalCloudConnectionProfile profile, CompositionProbe probe)
        : IPersonalCloudSetupService, IPersonalCloudProviderBridgeService, IPersonalCloudInitialSyncProgressService
    {
        public string? ErrorCode { get; init; }
        public bool IsConfigured => true;
        public PersonalCloudConnectionProfile? CurrentConnection => profile;
        public event Action<PersonalCloudInitialSyncProgress>? InitialSyncProgressChanged { add { } remove { } }
        public Task<PersonalCloudInitialSyncProgress?> GetInitialSyncProgressAsync(CancellationToken cancellationToken = default)
        { probe.ProgressReads++; return Task.FromResult<PersonalCloudInitialSyncProgress?>(null); }
        public Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default)
        {
            probe.CloudRequests++;
            if (ErrorCode is { } code) throw new PersonalCloudK0ContractException(code, "Synthetic cloud rejection");
            return Task.FromResult(new PersonalCloudSyncResult(0, 0, 0));
        }
        public Task<PersonalCloudProviderBridgeResult> RunProviderBridgeAsync(IReadOnlyList<PersonalCloudConfiguredProvider> providers, CancellationToken cancellationToken = default)
        { probe.BridgeRequests++; return Task.FromResult(new PersonalCloudProviderBridgeResult(0, 0, 0, 0)); }
        public Task RetryProviderCommandAsync(string taskId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected retry");
        public Task OpenAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected cloud operation");
        public Task<PersonalCloudProvisioningResult> ConnectAsync(CancellationToken cancellationToken = default, IProgress<PersonalCloudSetupProgress>? progress = null) => throw new InvalidOperationException("Unexpected cloud operation");
        public Task<PersonalCloudConnectionProfile> PairExistingAsync(Uri serviceOrigin, string grant, string deviceName, bool providerBridge, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected cloud operation");
        public Task<CloudflareD1Export> ExportAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected cloud operation");
        public Task<PersonalCloudUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected cloud operation");
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected cloud operation");
        public Task RemoveAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected cloud operation");
    }
}
