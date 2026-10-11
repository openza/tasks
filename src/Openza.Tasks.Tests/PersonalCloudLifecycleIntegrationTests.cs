using System.Net;
using System.Text;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudLifecycleIntegrationTests
{
    [Fact]
    public async Task Backup_returns_the_export_after_revoking_authorization_without_opening_it_or_changing_local_state()
    {
        using var f = await Fixture.CreateAsync();
        var result = await f.Service.ExportAsync();
        Assert.Equal(new Uri("https://downloads.example.test/backup.sql"), result.DownloadUri);
        Assert.Empty(f.Opened);
        Assert.Equal(new[] { "oauth-token", "accounts", "databases", "export", "oauth-revoke" }, f.Events);
        await f.AssertPreservedAsync();
    }

    [Fact]
    public async Task Update_targets_the_connected_worker_migrates_checks_health_then_persists_version()
    {
        using var f = await Fixture.CreateAsync();
        await f.Service.UpgradeAsync();
        Assert.Equal(new[] { "oauth-token", "accounts", "databases", "deploy", "journal", "checksum", "migrate", "health", "oauth-revoke" }, f.Events);
        Assert.Equal("openza-personal-test", f.Deployment.ResourceName);
        Assert.Equal("database_test", f.Deployment.DatabaseId);
        Assert.Equal("2.0.0", f.Connections.Load()!.ArtifactVersion);
        Assert.Equal("2.0.0", f.Service.CurrentConnection!.ArtifactVersion);
        Assert.NotNull(await f.Tasks.GetTaskAsync("local_task"));
    }

    [Fact]
    public async Task Update_origin_mismatch_stops_before_migrations_and_keeps_old_profile()
    {
        using var f = await Fixture.CreateAsync();
        f.Deployment.Origin = new Uri("https://openza-personal-other.example.workers.dev/");
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => f.Service.UpgradeAsync());
        Assert.Equal("resource_mismatch", error.Code);
        Assert.DoesNotContain("journal", f.Events);
        Assert.Equal("oauth-revoke", f.Events[^1]);
        await f.AssertPreservedAsync();
    }

    [Fact]
    public async Task Failed_update_resumes_without_reapplying_a_journaled_migration()
    {
        using var f = await Fixture.CreateAsync();
        f.Http.FailHealthOnce = true;
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => f.Service.UpgradeAsync());
        await f.AssertPreservedAsync();
        await f.Service.UpgradeAsync();
        Assert.Equal(1, f.Events.Count(x => x == "migrate"));
        Assert.Equal(2, f.Events.Count(x => x == "oauth-revoke"));
        Assert.Equal("2.0.0", f.Connections.Load()!.ArtifactVersion);
    }

    [Theory]
    [InlineData("none", "resource_not_found")]
    [InlineData("ambiguous", "resource_ambiguous")]
    [InlineData("account-ambiguous", "account_selection_required")]
    public async Task Backup_refuses_unmatched_or_ambiguous_resources_and_revokes_authorization(string resources, string code)
    {
        using var f = await Fixture.CreateAsync();
        f.Http.Resources = resources;
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => f.Service.ExportAsync());
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("export", f.Events);
        Assert.Equal("oauth-revoke", f.Events[^1]);
        await f.AssertPreservedAsync();
    }

    [Fact]
    public async Task Cancellation_before_consent_sends_no_requests_or_changes()
    {
        using var f = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ExportAsync(cancellation.Token));
        Assert.Empty(f.Events);
        await f.AssertPreservedAsync();
    }

    [Fact]
    public async Task Cancellation_during_backup_still_revokes_with_an_uncancelled_token()
    {
        using var f = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        f.Http.CancelOnExport = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ExportAsync(cancellation.Token));
        Assert.Equal("oauth-revoke", f.Events[^1]);
        Assert.False(f.Http.RevocationWasCancelled);
        Assert.Empty(f.Opened);
        await f.AssertPreservedAsync();
    }

    [Fact]
    public async Task Backup_revocation_failure_is_reported_and_does_not_open_the_download()
    {
        using var f = await Fixture.CreateAsync();
        f.Http.FailOAuthRevoke = true;
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => f.Service.ExportAsync());
        Assert.Equal("oauth_revoke_failed", error.Code);
        Assert.Empty(f.Opened);
        await f.AssertPreservedAsync();
    }

    [Fact]
    public async Task Disconnect_revokes_only_this_device_and_preserves_local_tasks()
    {
        using var f = await Fixture.CreateAsync();
        await f.Service.DisconnectAsync();
        Assert.Equal(new[] { "device-token", "device-revoke" }, f.Events);
        Assert.Null(f.Connections.Load());
        Assert.Null(f.Service.CurrentConnection);
        Assert.NotNull(await f.Tasks.GetTaskAsync("local_task"));
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => f.Identity.GetStoredDeviceAsync(Fixture.Origin));
    }

    [Fact]
    public async Task Failed_disconnect_preserves_credentials_profile_and_tasks_for_retry()
    {
        using var f = await Fixture.CreateAsync();
        f.Http.FailDeviceRevoke = true;
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => f.Service.DisconnectAsync());
        await f.AssertPreservedAsync();
        Assert.NotNull(await f.Identity.GetStoredDeviceAsync(Fixture.Origin));
        f.Http.FailDeviceRevoke = false;
        await f.Service.DisconnectAsync();
        Assert.Null(f.Connections.Load());
    }

    [Fact]
    public async Task Partial_removal_keeps_local_profile_and_retries_missing_worker_safely()
    {
        using var f = await Fixture.CreateAsync();
        f.Http.FailDatabaseDeleteOnce = true;
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => f.Service.RemoveAsync());
        await f.AssertPreservedAsync();
        Assert.NotNull(await f.Identity.GetStoredDeviceAsync(Fixture.Origin));
        await f.Service.RemoveAsync();
        Assert.Equal(2, f.Events.Count(x => x == "worker-delete"));
        Assert.Equal(2, f.Events.Count(x => x == "database-delete"));
        Assert.Equal(2, f.Events.Count(x => x == "oauth-revoke"));
        Assert.Null(f.Connections.Load());
        Assert.Null(f.Service.CurrentConnection);
        Assert.NotNull(await f.Tasks.GetTaskAsync("local_task"));
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => f.Identity.GetStoredDeviceAsync(Fixture.Origin));
    }

    [Fact]
    public async Task Removal_rejects_ambiguous_database_before_deleting_anything()
    {
        using var f = await Fixture.CreateAsync();
        f.Http.Resources = "ambiguous";
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => f.Service.RemoveAsync());
        Assert.DoesNotContain("worker-delete", f.Events);
        Assert.DoesNotContain("database-delete", f.Events);
        Assert.Equal("oauth-revoke", f.Events[^1]);
        await f.AssertPreservedAsync();
    }

    [Fact]
    public async Task Local_pairing_with_real_json_store_survives_reload()
    {
        using var f = await Fixture.CreateAsync();
        var local = new Uri("http://localhost:8787/");
        await f.Service.PairExistingAsync(local, "synthetic-pair-code-1234567890", "Test desktop", false);
        Assert.Equal(local, f.Connections.Load()!.ServiceOrigin);
        Assert.Equal(local, (await f.Identity.GetStoredDeviceAsync(local)).ServiceOrigin);
        Assert.Equal(Fixture.Origin, (await f.Identity.GetStoredDeviceAsync(Fixture.Origin)).ServiceOrigin);
        Assert.DoesNotContain("synthetic-refresh", await File.ReadAllTextAsync(f.ConnectionPath), StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly Uri Origin = new("https://openza-personal-test.example.workers.dev/");
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"openza-lifecycle-{Guid.NewGuid():N}");
        internal List<string> Events { get; } = [];
        internal List<Uri> Opened { get; } = [];
        internal FakeHttp Http { get; }
        internal FakeDeployment Deployment { get; }
        internal SqliteTaskStore Tasks { get; }
        internal JsonPersonalCloudConnectionStore Connections { get; }
        internal PersonalCloudDeviceIdentityClient Identity { get; }
        internal PersonalCloudSetupService Service { get; }
        internal string ConnectionPath => Path.Combine(_directory, "connection.json");
        private Fixture()
        {
            Http = new FakeHttp(Events);
            var http = new HttpClient(Http);
            Deployment = new FakeDeployment(Events);
            Tasks = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
            Connections = new JsonPersonalCloudConnectionStore(ConnectionPath);
            Identity = new PersonalCloudDeviceIdentityClient(http, new InMemoryCredentialStore());
            var oauth = new CloudflareOAuthClient(http, new CloudflareOAuthConfiguration("synthetic-public-client", new Uri("http://127.0.0.1:48192/callback"), ["account-settings.read", "d1.write", "workers-scripts.write"]));
            var management = new CloudflareManagementClient(http);
            var health = new PersonalCloudHealthClient(http);
            var gateway = new CloudflareProvisioningGateway(management, Deployment, new Bundle(), health, "Test desktop", false);
            var coordinator = new PersonalCloudManagementCoordinator(oauth, new Broker(), management, Deployment, gateway, health, "2.0.0");
            Service = new PersonalCloudSetupService(null!, "2.0.0", (uri, _) => { Opened.Add(uri); return Task.CompletedTask; }, Connections, Identity, http, Tasks, coordinator);
        }
        internal static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            await f.Tasks.InitializeAsync();
            await f.Tasks.UpsertTaskAsync(new TaskItem { Id = "local_task", Title = "Synthetic local task" });
            f.Http.HealthVersion = "1.0.0";
            await f.Service.PairExistingAsync(Origin, "synthetic-pair-code-1234567890", "Test desktop", false);
            f.Http.HealthVersion = "2.0.0";
            f.Events.Clear();
            return f;
        }
        internal async Task AssertPreservedAsync()
        {
            Assert.Equal("1.0.0", Connections.Load()!.ArtifactVersion);
            Assert.Equal(Connections.Load(), Service.CurrentConnection);
            Assert.NotNull(await Tasks.GetTaskAsync("local_task"));
        }
        public void Dispose() => TestDirectory.Delete(_directory);
    }

    private sealed class Broker : ICloudflareAuthorizationBroker
    {
        public Task<Uri> AuthorizeAsync(Uri authorizationUri, Uri redirectUri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = authorizationUri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).Single(x => x[0] == "state")[1];
            return Task.FromResult(new Uri($"{redirectUri}?code=synthetic-code&state={state}"));
        }
    }
    private sealed class Bundle : IPersonalCloudMigrationBundle
    {
        public string ArtifactVersion => "2.0.0";
        public IReadOnlyList<PersonalCloudMigration> Migrations => [new("0001_synthetic.sql", new string('a', 64), "CREATE TABLE synthetic_test(id TEXT);")];
    }
    private sealed class FakeDeployment(List<string> events) : ICloudflareArtifactDeploymentClient
    {
        public Uri Origin { get; set; } = Fixture.Origin;
        public string? ResourceName { get; private set; }
        public string? DatabaseId { get; private set; }
        public Task<CloudflareProvisioningApplication> DeployAsync(string accessToken, string accountId, string databaseId, string resourceName, string attemptId, string artifactVersion, CancellationToken cancellationToken = default)
        {
            events.Add("deploy"); ResourceName = resourceName; DatabaseId = databaseId;
            return Task.FromResult(new CloudflareProvisioningApplication(resourceName, Origin));
        }
    }
    private sealed class FakeHttp(List<string> events) : HttpMessageHandler
    {
        public string Resources { get; set; } = "normal";
        public string HealthVersion { get; set; } = "2.0.0";
        public bool FailHealthOnce { get; set; }
        public bool FailDatabaseDeleteOnce { get; set; }
        public bool FailDeviceRevoke { get; set; }
        public bool FailOAuthRevoke { get; set; }
        public bool RevocationWasCancelled { get; private set; }
        public CancellationTokenSource? CancelOnExport { get; set; }
        private bool _workerDeleted;
        private bool _migrationApplied;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/oauth2/token") return Reply("oauth-token", """{"access_token":"synthetic-temporary-token-1234567890","token_type":"Bearer","expires_in":600,"scope":"account-settings.read d1.write workers-scripts.write"}""");
            if (path == "/oauth2/revoke") { RevocationWasCancelled = cancellationToken.IsCancellationRequested; return Reply("oauth-revoke", "{}", FailOAuthRevoke ? HttpStatusCode.BadRequest : HttpStatusCode.OK); }
            cancellationToken.ThrowIfCancellationRequested();
            if (path == "/auth/pair") return Reply("pair", """{"protocolVersion":1,"instanceId":"instance_test","deviceId":"device_test","refreshCredential":"synthetic-refresh-1234567890","capabilities":["desktop_sync"],"refreshExpiresAt":"2099-01-01T00:00:00Z"}""");
            if (path == "/auth/device/token") return Reply("device-token", """{"protocolVersion":1,"accessToken":"synthetic-access-1234567890","capabilities":["desktop_sync"],"expiresAt":"2099-01-01T00:00:00Z"}""");
            if (path == "/auth/device/revoke") return Reply("device-revoke", """{"protocolVersion":1,"outcome":"revoked","deviceId":"device_test"}""", FailDeviceRevoke ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
            if (path == "/health") { var fail = FailHealthOnce; FailHealthOnce = false; return Reply("health", $"{{\"status\":\"ok\",\"protocolVersion\":1,\"product\":\"tasks\",\"artifactVersion\":\"{HealthVersion}\",\"schemaVersion\":17}}", fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK); }
            if (path == "/client/v4/accounts") return Reply("accounts", Resources == "account-ambiguous" ? """{"success":true,"result":[{"id":"account_test","name":"Test"},{"id":"account_other","name":"Other"}]}""" : """{"success":true,"result":[{"id":"account_test","name":"Test"}]}""");
            Assert.StartsWith("/client/v4/accounts/account_test/", path, StringComparison.Ordinal);
            if (path.EndsWith("/d1/database", StringComparison.Ordinal)) return Reply("databases", Resources switch { "none" => """{"success":true,"result":[]}""", "ambiguous" => """{"success":true,"result":[{"uuid":"database_test","name":"openza-personal-test"},{"uuid":"database_other","name":"openza-personal-test"}]}""", _ => """{"success":true,"result":[{"uuid":"database_test","name":"openza-personal-test"},{"uuid":"database_unrelated","name":"unrelated"}]}""" });
            if (path.EndsWith("/export", StringComparison.Ordinal)) { Assert.EndsWith("/database_test/export", path, StringComparison.Ordinal); if (CancelOnExport is not null) { events.Add("export"); CancelOnExport.Cancel(); throw new OperationCanceledException(cancellationToken); } return Reply("export", """{"success":true,"result":{"status":"complete","result":{"filename":"backup.sql","signed_url":"https://downloads.example.test/backup.sql"}}}"""); }
            if (request.Method == HttpMethod.Delete && path.EndsWith("/workers/scripts/openza-personal-test", StringComparison.Ordinal)) { var already = _workerDeleted; _workerDeleted = true; return Reply("worker-delete", """{"success":true}""", already ? HttpStatusCode.NotFound : HttpStatusCode.OK); }
            if (request.Method == HttpMethod.Delete && path.EndsWith("/d1/database/database_test", StringComparison.Ordinal)) { var fail = FailDatabaseDeleteOnce; FailDatabaseDeleteOnce = false; return Reply("database-delete", """{"success":true}""", fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK); }
            if (path.EndsWith("/database_test/query", StringComparison.Ordinal)) { var sql = await request.Content!.ReadAsStringAsync(cancellationToken); if (sql.Contains("SELECT sha256", StringComparison.Ordinal)) return Reply("checksum", _migrationApplied ? $"{{\"success\":true,\"result\":[{{\"results\":[{{\"sha256\":\"{new string('a', 64)}\"}}]}}]}}" : """{"success":true,"result":[{"results":[]}]}"""); if (sql.Contains("CREATE TABLE synthetic_test", StringComparison.Ordinal)) { _migrationApplied = true; return Reply("migrate", """{"success":true,"result":[]}"""); } return Reply("journal", """{"success":true,"result":[]}"""); }
            throw new InvalidOperationException($"Unexpected fake request {request.Method} {path}");
        }
        private HttpResponseMessage Reply(string name, string body, HttpStatusCode status = HttpStatusCode.OK)
        { events.Add(name); return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }; }
    }
}
