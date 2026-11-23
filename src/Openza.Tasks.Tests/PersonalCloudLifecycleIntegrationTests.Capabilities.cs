using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudLifecycleIntegrationTests
{
    [Theory]
    [InlineData("disconnect")]
    [InlineData("remove")]
    [InlineData("repair")]
    public async Task Backfill_session_reloads_connection_after_another_session_changes_it(string operation)
    {
        using var f = await Fixture.CreateAsync();
        var otherSession = CreateCapabilitySetupService(f);
        Assert.Equal(f.Service.CurrentConnection, otherSession.CurrentConnection);
        if (operation == "remove") await f.Service.RemoveAsync();
        else await f.Service.DisconnectAsync();
        if (operation == "repair")
            await f.Service.PairExistingAsync(Fixture.Origin, "synthetic-pair-code-1234567890", "Paired again", false);
        Assert.Equal(f.Service.CurrentConnection, otherSession.CurrentConnection);
    }

    [Theory]
    [InlineData("pair")]
    [InlineData("connect")]
    [InlineData("upgrade")]
    public async Task Lifecycle_records_workspace_capabilities_before_backfill_stages_exact_deadlines(string operation)
    {
        using var f = await Fixture.CreateAsync(23);
        await f.Tasks.UpsertTaskAsync(PreciseDeadlineTask());
        if (operation != "upgrade") await f.Service.DisconnectAsync();
        f.Http.HealthWorkspace = true;
        var service = operation == "connect" ? CreateCapabilitySetupService(f) : f.Service;
        if (operation == "connect") await service.ConnectAsync();
        else if (operation == "pair") await service.PairExistingAsync(Fixture.Origin, "synthetic-pair-code-1234567890", "Paired again", false);
        else await service.UpgradeAsync();
        var cloud = service.CurrentConnection!;
        Assert.True(await f.Tasks.SupportsPersonalCloudWorkspaceAsync(cloud.InstanceId));
        await f.Tasks.StageUnpublishedTasksForPersonalCloudAsync(cloud.InstanceId, cloud.DeviceId);
        await AssertPreciseDeadlineIntentAsync(f, cloud);
    }

    [Fact]
    public async Task Initial_seed_defers_unknown_capabilities_without_poisoning_the_workspace_seed()
    {
        using var f = await Fixture.CreateAsync(23);
        var cloud = f.Service.CurrentConnection!;
        await f.Tasks.UpsertTaskAsync(PreciseDeadlineTask());
        await using (var connection = new SqliteConnection($"Data Source={f.Tasks.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM personal_cloud_workspace_capabilities WHERE instance_id=@i";
            command.Parameters.AddWithValue("@i", cloud.InstanceId);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(0, await f.Tasks.StageUnpublishedTasksForPersonalCloudAsync(cloud.InstanceId, cloud.DeviceId, requireNegotiatedCapabilities: true));
        Assert.Empty(await f.Tasks.GetPendingPersonalCloudOutboxAsync(cloud.InstanceId, cloud.DeviceId));
        await f.Tasks.SavePersonalCloudWorkspaceCapabilitiesAsync(cloud.InstanceId, true, false);
        await f.Tasks.StageUnpublishedTasksForPersonalCloudAsync(cloud.InstanceId, cloud.DeviceId);
        await AssertPreciseDeadlineIntentAsync(f, cloud);
    }

    [Fact]
    public async Task Connect_health_failure_keeps_connection_unpublished_until_capabilities_can_be_saved()
    {
        using var f = await Fixture.CreateAsync(23);
        await f.Service.DisconnectAsync();
        f.Http.HealthWorkspace = true;
        f.Http.FailHealthOnce = true;
        var service = CreateCapabilitySetupService(f);
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => service.ConnectAsync());
        Assert.Null(service.CurrentConnection);
        Assert.Null(f.Connections.Load());
        await f.Tasks.EnsureNoPendingPersonalCloudIntentsAsync();
        await service.ConnectAsync();
        Assert.True(await f.Tasks.SupportsPersonalCloudWorkspaceAsync(service.CurrentConnection!.InstanceId));
    }

    private static TaskItem PreciseDeadlineTask() => new() { Id = "precise_deadline", Title = "Exact deadline",
        DeadlineAt = new DateTimeOffset(2027, 1, 2, 14, 30, 0, TimeSpan.Zero) };

    private static async Task AssertPreciseDeadlineIntentAsync(Fixture f, PersonalCloudConnectionProfile cloud)
    {
        var outbox = await f.Tasks.GetPendingPersonalCloudOutboxAsync(cloud.InstanceId, cloud.DeviceId);
        var intent = Assert.Single(outbox, entry => entry.EntityId == "precise_deadline" && entry.Operation == "task.createRich");
        Assert.DoesNotContain(outbox, entry => entry.EntityId == "precise_deadline" && entry.Operation == "task.create");
        using var document = JsonDocument.Parse(intent.CanonicalJson);
        Assert.Equal(PreciseDeadlineTask().DeadlineAt, DateTimeOffset.Parse(document.RootElement.GetProperty("payload").GetProperty("task").GetProperty("deadlineAt").GetString()!));
    }

    private static PersonalCloudSetupService CreateCapabilitySetupService(Fixture f)
    {
        var http = new HttpClient(f.Http);
        var oauth = new CloudflareOAuthClient(http, new CloudflareOAuthConfiguration("synthetic-public-client",
            new Uri("http://127.0.0.1:48192/callback"), ["account-settings.read", "d1.write", "workers-scripts.write"]));
        var coordinator = new PersonalCloudProvisioningCoordinator(oauth, new Broker(), new WorkspaceCapabilityGateway(),
            new JsonPersonalCloudProvisioningStateStore(Path.Combine(Path.GetDirectoryName(f.Tasks.DatabasePath)!, "setup.json")), f.Identity);
        return new(coordinator, "2.0.0", connectionStore: f.Connections, deviceIdentity: f.Identity, syncHttp: http, taskStore: f.Tasks);
    }

    private sealed class WorkspaceCapabilityGateway : ICloudflareProvisioningGateway
    {
        public Task<CloudflareProvisioningAccount> ResolveAccountAsync(string accessToken, CancellationToken cancellationToken = default) => Task.FromResult(new CloudflareProvisioningAccount("account_test"));
        public Task<CloudflareProvisioningDatabase> EnsureDatabaseAsync(string accessToken, string accountId, string attemptId, CancellationToken cancellationToken = default) => Task.FromResult(new CloudflareProvisioningDatabase("database_test"));
        public Task<CloudflareProvisioningApplication> DeployApplicationAsync(string accessToken, string accountId, string databaseId, string attemptId, string artifactVersion, CancellationToken cancellationToken = default) => Task.FromResult(new CloudflareProvisioningApplication("openza-personal-test", Fixture.Origin));
        public Task ApplyMigrationsAsync(string accessToken, string accountId, string databaseId, string artifactVersion, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task VerifyHealthAsync(Uri serviceOrigin, string artifactVersion, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PersonalCloudInitialPairing> CreateInitialPairingAsync(string accessToken, string accountId, string databaseId, Uri serviceOrigin, CancellationToken cancellationToken = default) => Task.FromResult(new PersonalCloudInitialPairing("synthetic-pair-code-1234567890", "Synthetic desktop", false));
        public Task<Uri> CreateOwnerBootstrapAsync(string accessToken, string accountId, string databaseId, Uri serviceOrigin, CancellationToken cancellationToken = default) => Task.FromResult(new Uri(serviceOrigin, "/bootstrap"));
    }
}
