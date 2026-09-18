using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed class DesktopPersonalCloudSettingsTests
{
    [Fact]
    public void Connect_action_stays_disabled_when_cloudflare_is_not_configured()
    {
        var vm = CreateViewModel(null);

        Assert.False(vm.CanConnectPersonalCloud);
        Assert.Equal("Not connected", vm.PersonalCloudStatusText);
    }

    [Fact]
    public async Task Successful_permanent_setup_reports_the_paired_instance()
    {
        var service = new StubSetupService(new PersonalCloudProvisioningResult(
            new Uri("https://openza-personal-test.workers.dev/"),
            "openza-personal-test",
            "database-test",
            "1.2.3",
            new Uri("https://openza-personal-test.workers.dev/#setup=owner-bootstrap-grant")));
        var vm = CreateViewModel(service);

        await vm.ConnectPersonalCloudAsync();

        Assert.True(vm.CanConnectPersonalCloud);
        Assert.Equal("Connected", vm.PersonalCloudStatusText);
        Assert.Contains("openza-personal-test.workers.dev", vm.PersonalCloudSummaryText, StringComparison.Ordinal);
        Assert.Equal(1, service.ConnectCalls);
    }

    [Fact]
    public async Task Manual_and_automatic_sync_include_connected_personal_cloud()
    {
        var connection = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
            InstanceId = "instance-test",
            DeviceId = "device-test",
            ArtifactVersion = "1.2.3",
            ConnectedAt = DateTimeOffset.UtcNow,
        };
        var service = new StubSetupService(
            new PersonalCloudProvisioningResult(
                connection.ServiceOrigin,
                "openza-personal-test",
                "database-test",
                connection.ArtifactVersion,
                new Uri("https://openza-personal-test.workers.dev/#setup=owner-bootstrap-grant")),
            connection,
            new PersonalCloudSyncResult(2, 3, 17));
        var path = Path.Combine(Path.GetTempPath(), $"openza-personal-cloud-sync-ui-{Guid.NewGuid():N}.db");
        var store = new SqliteTaskStore(path);
        await store.InitializeAsync();
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), personalCloudSetup: service);

        await vm.RunTodoistSyncAsync();
        await vm.RunAutomaticTodoistSyncAsync();

        Assert.Equal(2, service.SyncCalls);
        Assert.Contains("Personal Cloud synced", vm.LastSyncResult, StringComparison.Ordinal);
        Assert.Contains("2 uploaded, 3 downloaded", vm.LastSyncResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_cloud_pairing_uses_the_one_time_grant_and_syncs()
    {
        var service = new StubSetupService(new PersonalCloudProvisioningResult(
            new Uri("https://openza-personal-test.workers.dev/"), "worker", "database", "1.2.3",
            new Uri("https://openza-personal-test.workers.dev/#setup=unused")));
        var vm = CreateViewModel(service);

        var connection = await vm.PairExistingPersonalCloudAsync(
            "https://openza-personal-test.workers.dev/", "pair-this-is-a-long-one-time-code", true);

        Assert.NotNull(connection);
        Assert.Equal("Connected", vm.PersonalCloudStatusText);
        Assert.Equal(1, service.PairCalls);
        Assert.Equal(1, service.SyncCalls);
        Assert.True(service.LastProviderBridge);
    }

    [Fact]
    public async Task Connected_cloud_management_exports_updates_disconnects_and_preserves_local_tasks()
    {
        var connection = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
            InstanceId = "instance-test",
            DeviceId = "device-test",
            ArtifactVersion = "1.2.3",
            ConnectedAt = DateTimeOffset.UtcNow,
        };
        var service = new StubSetupService(
            new PersonalCloudProvisioningResult(
                connection.ServiceOrigin, "openza-personal-test", "database-test", "1.2.4",
                new Uri("https://openza-personal-test.workers.dev/#setup=unused")),
            connection);
        var vm = CreateViewModel(service);

        Assert.True(await vm.ExportPersonalCloudAsync());
        Assert.True(await vm.UpgradePersonalCloudAsync());
        Assert.True(await vm.DisconnectPersonalCloudAsync());

        Assert.Equal(1, service.ExportCalls);
        Assert.Equal(1, service.UpgradeCalls);
        Assert.Equal(1, service.DisconnectCalls);
        Assert.False(vm.IsPersonalCloudConnected);
        Assert.Equal("Not connected", vm.PersonalCloudStatusText);
        Assert.Equal("Set up Personal Cloud", vm.PersonalCloudActionText);
    }

    private static MainWindowViewModel CreateViewModel(IPersonalCloudSetupService? service)
    {
        var path = Path.Combine(Path.GetTempPath(), $"openza-personal-cloud-ui-{Guid.NewGuid():N}.db");
        return new MainWindowViewModel(new SqliteTaskStore(path), new InMemoryCredentialStore(), personalCloudSetup: service);
    }

    private sealed class StubSetupService(
        PersonalCloudProvisioningResult result,
        PersonalCloudConnectionProfile? connection = null,
        PersonalCloudSyncResult? syncResult = null) : IPersonalCloudSetupService
    {
        private PersonalCloudConnectionProfile? _connection = connection;
        public bool IsConfigured => true;
        public PersonalCloudConnectionProfile? CurrentConnection => _connection;
        public Task OpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public int SyncCalls { get; private set; }
        public Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default)
        {
            SyncCalls++;
            return Task.FromResult(syncResult ?? new PersonalCloudSyncResult(0, 0, 0));
        }
        public int ExportCalls { get; private set; }
        public Task<CloudflareD1Export> ExportAsync(CancellationToken cancellationToken = default)
        { ExportCalls++; return Task.FromResult(new CloudflareD1Export("openza-personal-cloud.sql", new Uri("https://downloads.example/openza.sql"))); }
        public int UpgradeCalls { get; private set; }
        public Task<PersonalCloudUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default)
        { UpgradeCalls++; return Task.FromResult(new PersonalCloudUpgradeResult(result.ArtifactVersion)); }
        public int DisconnectCalls { get; private set; }
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        { DisconnectCalls++; _connection = null; return Task.CompletedTask; }
        public int RemoveCalls { get; private set; }
        public Task RemoveAsync(CancellationToken cancellationToken = default)
        { RemoveCalls++; _connection = null; return Task.CompletedTask; }
        public int ConnectCalls { get; private set; }
        public Task<PersonalCloudProvisioningResult> ConnectAsync(
            CancellationToken cancellationToken = default,
            IProgress<PersonalCloudSetupProgress>? progress = null)
        { ConnectCalls++; return Task.FromResult(result); }
        public int PairCalls { get; private set; }
        public bool LastProviderBridge { get; private set; }
        public Task<PersonalCloudConnectionProfile> PairExistingAsync(
            Uri serviceOrigin,
            string grant,
            string deviceName,
            bool providerBridge,
            CancellationToken cancellationToken = default)
        {
            PairCalls++;
            LastProviderBridge = providerBridge;
            _connection = new PersonalCloudConnectionProfile
            {
                ServiceOrigin = serviceOrigin,
                InstanceId = "instance-test",
                DeviceId = "device-test",
                ArtifactVersion = "1.2.3",
                ConnectedAt = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(_connection!);
        }
    }
}
