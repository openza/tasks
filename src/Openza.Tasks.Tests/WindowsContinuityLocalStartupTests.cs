using Avalonia.Controls;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.Services.Continuity;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed partial class WindowsContinuityIntegrationTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Completed_vault_outage_opens_local_window_guards_services_and_next_start_revalidates(bool com, bool automatic)
    {
        await fixture.Session.Dispatch(async () =>
        {
            Directory.CreateDirectory(_root);
            await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "{}");
            var store = new SqliteTaskStore(Path.Combine(_root, "openza-tasks.db"));
            await store.InitializeAsync();
            var platform = Platform;
            var profile = new PersonalCloudConnectionProfile
            {
                ServiceOrigin = new("https://example.invalid/"), InstanceId = "instance-local", DeviceId = "device-local",
                ArtifactVersion = "synthetic", ConnectedAt = DateTimeOffset.UtcNow,
            };
            await new JsonPersonalCloudConnectionStore(Path.Combine(_root, "personal-cloud-connection.json")).SaveAsync(profile);
            platform.Legacy.Values[CloudKey(profile)] = CloudCredential(profile);
            await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem { Id = "retained", Title = "Synthetic retained task" }, profile.InstanceId, profile.DeviceId);
            using (var initial = new WindowsContinuitySession(platform)) await PrepareAsync(initial, platform);
            await File.WriteAllTextAsync(Path.Combine(_root, "personal-cloud-provisioning.json"), "{}");
            var preferences = new DesktopPreferencesStore(Path.Combine(_root, "settings.avalonia.json"));
            await preferences.UpdateAsync(value => value with { AutomaticSyncEnabled = automatic, ShowGetStarted = false, AutomaticRestorePointsEnabled = false });
            var paths = new[] { Path.Combine(_root, "settings.avalonia.json"), Path.Combine(_root, "personal-cloud-connection.json"), Path.Combine(_root, "personal-cloud-provisioning.json"), store.DatabasePath, Path.Combine(Archive, "complete.json") };
            var before = await Task.WhenAll(paths.Select(path => File.ReadAllBytesAsync(path)));
            var credentialWrites = platform.Target.Writes;
            var raw = new CountingCloud(profile);
            using (var blocked = new WindowsContinuitySession(platform))
            {
                platform.Target.ReadFailure = com ? new System.Runtime.InteropServices.COMException("Synthetic access failure") : new UnauthorizedAccessException("Synthetic access failure");
                await PrepareAsync(blocked, platform);
                Assert.True(blocked.CloudAccessInhibited);
                for (var i = 0; i < paths.Length; i++) Assert.Equal(before[i], await File.ReadAllBytesAsync(paths[i]));
                var service = blocked.GuardCloudService(raw);
                Assert.Equal(profile, service.CurrentConnection);
                var vm = new MainWindowViewModel(store, blocked.AppCredentials, preferencesStore: preferences, personalCloudSetup: service, continuitySession: blocked);
                var window = new Openza.Tasks.Desktop.Shell.MainWindow(vm);
                try
                {
                    window.Show();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    while (vm.SelectedNavigation is null || vm.IsBusy || !vm.StatusMessage.Contains("Credential access", StringComparison.Ordinal)) await Task.Delay(10, deadline.Token);
                    Assert.True(window.FindControl<Button>("SettingsNavigationButton")!.IsEnabled);
                    Assert.Equal("Paused", vm.PersonalCloudStatusText);
                    Assert.Contains("restart Openza", vm.PersonalCloudSummaryText);
                    Assert.NotNull(await store.GetTaskAsync("retained"));
                    await vm.RunAutomaticTodoistSyncAsync();
                    vm.SetAutomaticSyncEnabled(true);
                    await vm.RunAutomaticTodoistSyncAsync();
                    await vm.RunTodoistSyncAsync();
                    await vm.UploadCloudBackupsAsync(false);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => vm.OpenPersonalCloudSettingsAsync());
                    var operations = new Func<Task>[]
                    {
                        () => service.OpenAsync(), () => service.ConnectAsync(),
                        () => service.PairExistingAsync(profile.ServiceOrigin, "synthetic", "synthetic", false),
                        () => service.SyncAsync(), () => service.ExportAsync(), () => service.UpgradeAsync(),
                        () => service.DisconnectAsync(), () => service.RemoveAsync(),
                        () => ((IPersonalCloudProviderBridgeService)service).RunProviderBridgeAsync([]),
                        () => ((IPersonalCloudProviderBridgeService)service).RetryProviderCommandAsync("retained"),
                        () => blocked.AppCredentials.SaveAsync("synthetic", "synthetic"),
                        () => blocked.AppCredentials.RemoveAsync("synthetic"),
                    };
                    foreach (var operation in operations) await Assert.ThrowsAsync<InvalidOperationException>(operation);
                    Assert.Equal(0, raw.Calls);
                    Assert.Equal(credentialWrites, platform.Target.Writes);
                    Assert.False(preferences.Load().WindowsCloudRecoveryRequired);
                    Assert.Equal(automatic, preferences.Load().AutomaticSyncEnabled);
                    vm.QuickAddTitle = "Synthetic offline task";
                    await vm.AddTaskAsync();
                    var created = Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }), task => task.Title == "Synthetic offline task");
                    Assert.Equal(created.Id, vm.SelectedTask?.Task.Id);
                    vm.DetailTitle = "Synthetic offline edit";
                    Assert.True(await vm.SaveSelectedAsync());
                    Assert.Equal("Synthetic offline edit", (await store.GetTaskAsync(created.Id))!.Title);
                    await vm.DeleteSelectedAsync();
                    Assert.Null(await store.GetTaskAsync(created.Id));
                    Assert.Contains(await store.GetPendingPersonalCloudOutboxAsync(profile.InstanceId, profile.DeviceId), intent => intent.EntityId == created.Id);
                    Assert.Contains(await store.GetPendingPersonalCloudOutboxAsync(profile.InstanceId, profile.DeviceId), intent => intent.EntityId == "retained");
                    Assert.Equal(0, raw.Calls);
                    platform.Target.ReadFailure = null;
                    await PrepareAsync(blocked, platform);
                    Assert.True(blocked.CloudAccessInhibited);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync());
                }
                finally { window.Close(); await Task.Delay(100); }
            }
            using var healthy = new WindowsContinuitySession(platform);
            await PrepareAsync(healthy, platform);
            Assert.False(healthy.CloudAccessInhibited);
            var ready = healthy.GuardCloudService(raw);
            await ready.SyncAsync();
            Assert.Equal(1, raw.Calls);
            healthy.Dispose();
            await Assert.ThrowsAsync<WindowsContinuityException>(() => ready.SyncAsync());
            Assert.Equal(1, raw.Calls);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Initial_migration_vault_failure_remains_fatal()
    {
        var platform = Platform;
        await SeedAsync();
        await PairAsync(platform, true);
        platform.Legacy.ReadFailure = new UnauthorizedAccessException("Synthetic outage");
        using var session = new WindowsContinuitySession(platform);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => PrepareAsync(session, platform));
        Assert.False(session.CloudAccessInhibited);
        Assert.False(File.Exists(session.SettingsPath));
        Assert.False(File.Exists(Path.Combine(Archive, "complete.json")));
    }

    private sealed class CountingCloud(PersonalCloudConnectionProfile profile) : IPersonalCloudSetupService, IPersonalCloudProviderBridgeService
    {
        public int Calls { get; private set; }
        public bool IsConfigured => true;
        public PersonalCloudConnectionProfile? CurrentConnection => profile;
        private Task Called() { Calls++; return Task.CompletedTask; }
        private Task<T> Called<T>() { Calls++; throw new InvalidOperationException("Unexpected synthetic service entry"); }
        public Task OpenAsync(CancellationToken cancellationToken = default) => Called();
        public Task<PersonalCloudProvisioningResult> ConnectAsync(CancellationToken cancellationToken = default, IProgress<PersonalCloudSetupProgress>? progress = null) => Called<PersonalCloudProvisioningResult>();
        public Task<PersonalCloudConnectionProfile> PairExistingAsync(Uri serviceOrigin, string grant, string deviceName, bool providerBridge, CancellationToken cancellationToken = default) => Called<PersonalCloudConnectionProfile>();
        public Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(new PersonalCloudSyncResult(0, 0, 0)); }
        public Task<CloudflareD1Export> ExportAsync(CancellationToken cancellationToken = default) => Called<CloudflareD1Export>();
        public Task<PersonalCloudUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default) => Called<PersonalCloudUpgradeResult>();
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Called();
        public Task RemoveAsync(CancellationToken cancellationToken = default) => Called();
        public Task<PersonalCloudProviderBridgeResult> RunProviderBridgeAsync(IReadOnlyList<PersonalCloudConfiguredProvider> providers, CancellationToken cancellationToken = default) => Called<PersonalCloudProviderBridgeResult>();
        public Task RetryProviderCommandAsync(string taskId, CancellationToken cancellationToken = default) => Called();
    }
}
