using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudProvisioningTests
{
    private static readonly CloudflareOAuthConfiguration Configuration = new(
        "openza-test-client",
        new Uri("http://127.0.0.1:48192/callback"),
        ["account-settings.read", "d1.write", "workers-scripts.write"]);

    [Fact]
    public void Pkce_authorization_uses_S256_state_and_no_client_secret()
    {
        var client = new CloudflareOAuthClient(new HttpClient(), Configuration);
        var first = client.BeginAuthorization();
        var second = client.BeginAuthorization();

        Assert.StartsWith("https://dash.cloudflare.com/oauth2/auth?", first.AuthorizationUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("response_type=code", first.AuthorizationUri.Query, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", first.AuthorizationUri.Query, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A48192%2Fcallback", first.AuthorizationUri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", first.AuthorizationUri.Query, StringComparison.Ordinal);
        Assert.NotEqual(first.State, second.State);
        Assert.NotEqual(first.CodeVerifier, second.CodeVerifier);
        Assert.DoesNotContain('=', first.CodeVerifier);
    }

    [Fact]
    public async Task OAuth_exchange_rejects_wrong_state_before_sending_token_request()
    {
        var handler = new RecordingHandler();
        var client = new CloudflareOAuthClient(new HttpClient(handler), Configuration);

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.AuthorizeAsync(
            new CallbackBroker((_, redirect) => new Uri($"{redirect}?code=temporary-code&state=wrong"))));

        Assert.Equal("oauth_state_mismatch", error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Coordinator_resumes_after_failure_pairs_first_desktop_and_revokes_each_temporary_token()
    {
        var oauthHandler = new RecordingHandler(
            Json(HttpStatusCode.OK, """{"access_token":"temporary-oauth-token-one","token_type":"Bearer","expires_in":600,"scope":"account-settings.read d1.write workers-scripts.write"}"""),
            Json(HttpStatusCode.OK, "{}"),
            Json(HttpStatusCode.OK, """{"access_token":"temporary-oauth-token-two","token_type":"Bearer","expires_in":600,"scope":"account-settings.read d1.write workers-scripts.write"}"""),
            Json(HttpStatusCode.OK, "{}"));
        var oauth = new CloudflareOAuthClient(new HttpClient(oauthHandler), Configuration);
        var identityHandler = new RecordingHandler(Json(HttpStatusCode.Created, """
            {"protocolVersion":1,"instanceId":"instance_personal","deviceId":"device_1234567890","refreshCredential":"refresh-secret-value-1234567890","capabilities":["desktop_sync","provider_bridge"],"refreshExpiresAt":"2099-01-01T00:00:00Z"}
            """));
        var credentials = new RecordingCredentialStore();
        var stateStore = new MemoryStateStore();
        var gateway = new RecordingGateway { FailFirstDeployment = true };
        var coordinator = new PersonalCloudProvisioningCoordinator(
            oauth,
            new CallbackBroker((authorization, redirect) => CallbackFor(authorization, redirect)),
            gateway,
            stateStore,
            new PersonalCloudDeviceIdentityClient(new HttpClient(identityHandler), credentials));
        var firstProgress = new RecordingProgress();
        var resumedProgress = new RecordingProgress();

        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => coordinator.ConnectAsync("1.2.3", progress: firstProgress));
        Assert.NotNull(stateStore.Value);
        Assert.Equal(PersonalCloudProvisioningStage.DatabaseReady, stateStore.Value!.Stage);
        Assert.Equal("deployment_failed", stateStore.Value.LastErrorCode);

        var result = await coordinator.ConnectAsync("1.2.3", progress: resumedProgress);

        Assert.Equal(new Uri("https://openza-personal-test.workers.dev/"), result.ServiceOrigin);
        Assert.Equal("instance_personal", result.InstanceId);
        Assert.Equal("device_1234567890", result.DeviceId);
        Assert.Equal(1, gateway.ResolveCalls);
        Assert.Equal(1, gateway.DatabaseCalls);
        Assert.Equal(2, gateway.DeployCalls);
        Assert.Equal(1, gateway.MigrationCalls);
        Assert.Equal(1, gateway.HealthCalls);
        Assert.Equal(1, gateway.PairingCalls);
        Assert.Equal(1, gateway.OwnerBootstrapCalls);
        Assert.Equal(PersonalCloudProvisioningStage.Complete, stateStore.Value!.Stage);
        await coordinator.ConfirmConnectionSavedAsync(new PersonalCloudConnectionProfile
        {
            ServiceOrigin = result.ServiceOrigin,
            InstanceId = result.InstanceId!, DeviceId = result.DeviceId!, ArtifactVersion = result.ArtifactVersion,
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        Assert.Null(stateStore.Value);
        Assert.Single(credentials.Values);
        Assert.DoesNotContain("temporary-oauth-token", credentials.Values.Single().Value, StringComparison.Ordinal);
        Assert.DoesNotContain("pairing-grant", credentials.Values.Single().Value, StringComparison.Ordinal);
        Assert.Equal(2, oauthHandler.Requests.Count(request => request.RequestUri == CloudflareOAuthConfiguration.TokenEndpoint));
        Assert.Equal(2, oauthHandler.Requests.Count(request => request.RequestUri == CloudflareOAuthConfiguration.RevocationEndpoint));
        Assert.All(oauthHandler.Bodies.Where((_, index) => index % 2 == 0), body => Assert.DoesNotContain("client_secret", body, StringComparison.Ordinal));
        Assert.Equal(
        [
            PersonalCloudSetupStep.AwaitingAuthorization,
            PersonalCloudSetupStep.ResolvingAccount,
            PersonalCloudSetupStep.CreatingDatabase,
            PersonalCloudSetupStep.DeployingApplication,
        ], firstProgress.Steps);
        Assert.Equal(
        [
            PersonalCloudSetupStep.AwaitingAuthorization,
            PersonalCloudSetupStep.DeployingApplication,
            PersonalCloudSetupStep.PreparingDatabase,
            PersonalCloudSetupStep.VerifyingConnection,
            PersonalCloudSetupStep.PairingDesktop,
            PersonalCloudSetupStep.CreatingOwnerAccess,
        ], resumedProgress.Steps);
    }

    [Fact]
    public async Task Profile_save_failure_keeps_completed_resource_identity_and_retry_reuses_it()
    {
        var oauthHandler = new RecordingHandler(
            Json(HttpStatusCode.OK, """{"access_token":"temporary-oauth-token-one","token_type":"Bearer","expires_in":600,"scope":"account-settings.read d1.write workers-scripts.write"}"""),
            Json(HttpStatusCode.OK, "{}"),
            Json(HttpStatusCode.OK, """{"access_token":"temporary-oauth-token-two","token_type":"Bearer","expires_in":600,"scope":"account-settings.read d1.write workers-scripts.write"}"""),
            Json(HttpStatusCode.OK, "{}"));
        var identity = new PersonalCloudDeviceIdentityClient(new HttpClient(new RecordingHandler(Json(HttpStatusCode.Created,
            """{"protocolVersion":1,"instanceId":"instance_personal","deviceId":"device_1234567890","refreshCredential":"refresh-secret-value-1234567890","capabilities":["desktop_sync"],"refreshExpiresAt":"2099-01-01T00:00:00Z"}"""))), new RecordingCredentialStore());
        var state = new MemoryStateStore();
        var gateway = new RecordingGateway();
        var coordinator = new PersonalCloudProvisioningCoordinator(
            new CloudflareOAuthClient(new HttpClient(oauthHandler), Configuration),
            new CallbackBroker(CallbackFor), gateway, state, identity);
        var connections = new FailFirstConnectionStore();
        var service = new PersonalCloudSetupService(coordinator, "1.2.3", connectionStore: connections);

        await Assert.ThrowsAsync<IOException>(() => service.ConnectAsync());
        Assert.Null(service.CurrentConnection);
        Assert.Equal(PersonalCloudProvisioningStage.Complete, state.Value!.Stage);
        var attemptId = state.Value.AttemptId;
        var result = await service.ConnectAsync();

        Assert.Equal(result.ServiceOrigin, service.CurrentConnection!.ServiceOrigin);
        Assert.Equal(connections.Load(), service.CurrentConnection);
        Assert.Null(state.Value);
        Assert.Equal(1, gateway.DatabaseCalls);
        Assert.Equal(1, gateway.DeployCalls);
        Assert.Equal(1, gateway.PairingCalls);
        Assert.Equal(2, gateway.OwnerBootstrapCalls);
        Assert.StartsWith("pc_", attemptId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_checkpoint_contains_only_non_secret_resume_state_and_clears_after_completion()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openza-personal-cloud-state-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "provisioning.json");
        try
        {
            var store = new JsonPersonalCloudProvisioningStateStore(path);
            var state = new PersonalCloudProvisioningState
            {
                AttemptId = "pc_attempt_1234567890",
                ArtifactVersion = "1.2.3",
                Stage = PersonalCloudProvisioningStage.DatabaseReady,
                AccountId = "account_1234567890",
                DatabaseId = "database_1234567890",
            };

            await store.SaveAsync(state);

            var raw = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("accessToken", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("refreshCredential", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pairing", raw, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(state, await store.LoadAsync());

            await store.ClearAsync();
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Loopback_broker_accepts_only_the_registered_callback_and_returns_control_to_Openza()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var redirect = new Uri($"http://127.0.0.1:{port}/cloudflare/callback");
        Task<HttpResponseMessage>? browserRequest = null;
        var broker = new LoopbackCloudflareAuthorizationBroker((authorization, _) =>
        {
            var state = authorization.Query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.TrimStart('?').Split('=', 2))
                .Single(parts => Uri.UnescapeDataString(parts[0]) == "state")[1];
            browserRequest = Task.Run(() => new HttpClient().GetAsync($"{redirect}?code=temporary-code&state={state}"));
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(5));
        var authorizationUri = new Uri("https://dash.cloudflare.com/oauth2/auth?state=expected-state");

        var callback = await broker.AuthorizeAsync(authorizationUri, redirect);
        using var response = await browserRequest!;

        Assert.Equal("temporary-code", callback.Query.Split('&')[0].Split('=')[1]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Return to Openza", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cloudflare_management_client_pages_accounts_and_creates_only_a_missing_named_database()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, """{"success":true,"result":[{"id":"account_one","name":"One"}],"result_info":{"total_pages":2}}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":[{"id":"account_two","name":"Two"}],"result_info":{"total_pages":2}}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":[],"result_info":{"total_pages":1}}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":{"uuid":"database_uuid","name":"openza-personal-pc123"}}"""));
        var client = new CloudflareManagementClient(new HttpClient(handler));

        var accounts = await client.ListAccountsAsync("temporary-oauth-token");
        var database = await client.EnsureDatabaseAsync("temporary-oauth-token", accounts[0].Id, "openza-personal-pc123");

        Assert.Equal(["account_one", "account_two"], accounts.Select(account => account.Id));
        Assert.Equal("database_uuid", database.Id);
        Assert.Equal(HttpMethod.Post, handler.Requests[^1].Method);
        Assert.Equal("Bearer", handler.Requests[^1].Headers.Authorization!.Scheme);
        Assert.Equal("temporary-oauth-token", handler.Requests[^1].Headers.Authorization!.Parameter);
        Assert.Contains("\"name\":\"openza-personal-pc123\"", handler.Bodies[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cloudflare_management_client_rejects_ambiguous_resources_and_hides_remote_error_details()
    {
        var ambiguous = new CloudflareManagementClient(new HttpClient(new RecordingHandler(Json(HttpStatusCode.OK, """
            {"success":true,"result":[{"uuid":"database_one","name":"openza-personal-pc123"},{"uuid":"database_two","name":"openza-personal-pc123"}],"result_info":{"total_pages":1}}
            """))));
        var conflict = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => ambiguous.EnsureDatabaseAsync(
            "temporary-oauth-token", "account_one", "openza-personal-pc123"));
        Assert.Equal("resource_ambiguous", conflict.Code);

        var rejected = new CloudflareManagementClient(new HttpClient(new RecordingHandler(Json(HttpStatusCode.Forbidden, """
            {"success":false,"errors":[{"message":"private account detail"}]}
            """))));
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => rejected.ListAccountsAsync("temporary-oauth-token"));
        Assert.Equal("cloudflare_api_failed", error.Code);
        Assert.DoesNotContain("private account detail", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cloudflare_management_client_exports_and_idempotently_removes_personal_cloud_resources()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, """
                {"success":true,"result":{"at_bookmark":"bookmark-one","status":"complete","result":{"filename":"openza-personal.sql","signed_url":"https://downloads.example/openza-personal.sql"}}}
                """),
            Json(HttpStatusCode.OK, ""),
            Json(HttpStatusCode.OK, """{"success":true,"result":{}}"""));
        var client = new CloudflareManagementClient(new HttpClient(handler));

        var export = await client.ExportDatabaseAsync("temporary-oauth-token", "account_one", "database_one");
        await client.DeleteWorkerAsync("temporary-oauth-token", "account_one", "openza-personal-test");
        await client.DeleteDatabaseAsync("temporary-oauth-token", "account_one", "database_one");

        Assert.Equal("openza-personal.sql", export.FileName);
        Assert.Equal(new Uri("https://downloads.example/openza-personal.sql"), export.DownloadUri);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.EndsWith("/d1/database/database_one/export", handler.Requests[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.EndsWith("/workers/scripts/openza-personal-test", handler.Requests[1].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Delete, handler.Requests[2].Method);
        Assert.EndsWith("/d1/database/database_one", handler.Requests[2].RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_verification_requires_the_exact_artifact_version()
    {
        var success = new PersonalCloudHealthClient(new HttpClient(new RecordingHandler(Json(HttpStatusCode.OK, """
            {"status":"ok","protocolVersion":1,"product":"tasks","artifactVersion":"1.2.3","schemaVersion":17}
            """))));
        await success.VerifyAsync(new Uri("https://openza-personal-test.workers.dev/"), "1.2.3");

        var mismatch = new PersonalCloudHealthClient(new HttpClient(new RecordingHandler(Json(HttpStatusCode.OK, """
            {"status":"ok","protocolVersion":1,"product":"tasks","artifactVersion":"1.2.2","schemaVersion":17}
            """))));
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => mismatch.VerifyAsync(
            new Uri("https://openza-personal-test.workers.dev/"), "1.2.3"));
        Assert.Equal("health_mismatch", error.Code);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    public async Task Health_verification_accepts_compatible_schema_migrations(int schemaVersion)
    {
        var client = new PersonalCloudHealthClient(new HttpClient(new RecordingHandler(Json(HttpStatusCode.OK,
            $$"""{"status":"ok","protocolVersion":1,"product":"tasks","artifactVersion":"1.2.3","schemaVersion":{{schemaVersion}}}"""))));

        var health = await client.InspectAsync(new Uri("https://openza-personal-test.workers.dev/"));

        Assert.Equal(schemaVersion, health.SchemaVersion);
        await new PersonalCloudHealthClient(new HttpClient(new RecordingHandler(Json(HttpStatusCode.OK,
            $$"""{"status":"ok","protocolVersion":1,"product":"tasks","artifactVersion":"1.2.3","schemaVersion":{{schemaVersion}}}"""))))
            .VerifyAsync(new Uri("https://openza-personal-test.workers.dev/"), "1.2.3");
    }

    [Theory]
    [InlineData(16, 1, "tasks")]
    [InlineData(21, 1, "tasks")]
    [InlineData(18, 2, "tasks")]
    [InlineData(18, 1, "reader")]
    public async Task Health_verification_rejects_incompatible_schema_protocol_or_product(
        int schemaVersion, int protocolVersion, string product)
    {
        var client = new PersonalCloudHealthClient(new HttpClient(new RecordingHandler(Json(HttpStatusCode.OK,
            $$"""{"status":"ok","protocolVersion":{{protocolVersion}},"product":"{{product}}","artifactVersion":"1.2.3","schemaVersion":{{schemaVersion}}}"""))));

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.InspectAsync(
            new Uri("https://openza-personal-test.workers.dev/")));

        Assert.Equal("health_mismatch", error.Code);
    }

    [Fact]
    public async Task Provisioning_gateway_composes_resources_migrations_health_and_one_time_pairing_safely()
    {
        var managementHandler = new RecordingHandler(
            Json(HttpStatusCode.OK, """{"success":true,"result":[{"id":"account_one","name":"One"}],"result_info":{"total_pages":1}}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":[],"result_info":{"total_pages":1}}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":{"uuid":"database_one","name":"openza-personal-attempt12345"}}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":[]}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":[{"results":[]}]}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":[]}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":[]}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":[]}"""));
        var deployment = new RecordingDeploymentClient();
        var gateway = new CloudflareProvisioningGateway(
            new CloudflareManagementClient(new HttpClient(managementHandler)),
            deployment,
            new MigrationBundle("1.2.3", [new PersonalCloudMigration(
                "0001_test.sql",
                new string('a', 64),
                "CREATE TABLE provisioning_test(id TEXT PRIMARY KEY);")]),
            new PersonalCloudHealthClient(new HttpClient(new RecordingHandler(Json(HttpStatusCode.OK, """
                {"status":"ok","protocolVersion":1,"product":"tasks","artifactVersion":"1.2.3","schemaVersion":17}
                """)))),
            "Linux desktop",
            providerBridge: true);

        var account = await gateway.ResolveAccountAsync("temporary-oauth-token");
        var database = await gateway.EnsureDatabaseAsync("temporary-oauth-token", account.AccountId, "pc_attempt_1234567890");
        var application = await gateway.DeployApplicationAsync(
            "temporary-oauth-token", account.AccountId, database.DatabaseId, "pc_attempt_1234567890", "1.2.3");
        await gateway.ApplyMigrationsAsync("temporary-oauth-token", account.AccountId, database.DatabaseId, "1.2.3");
        await gateway.VerifyHealthAsync(application.ServiceOrigin, "1.2.3");
        var pairing = await gateway.CreateInitialPairingAsync(
            "temporary-oauth-token", account.AccountId, database.DatabaseId, application.ServiceOrigin);
        var ownerBootstrap = await gateway.CreateOwnerBootstrapAsync(
            "temporary-oauth-token", account.AccountId, database.DatabaseId, application.ServiceOrigin);

        Assert.Equal("openza-personal-attempt12345", deployment.ResourceName);
        Assert.Equal("database_one", deployment.DatabaseId);
        Assert.Equal("Linux desktop", pairing.DeviceName);
        Assert.True(pairing.ProviderBridge);
        Assert.StartsWith("pair-", pairing.Grant, StringComparison.Ordinal);
        var pairingBody = managementHandler.Bodies[^2];
        Assert.Contains("INSERT INTO pairing_grants", pairingBody, StringComparison.Ordinal);
        Assert.DoesNotContain(pairing.Grant, pairingBody, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(pairing.Grant))), pairingBody, StringComparison.Ordinal);
        var bootstrapBody = managementHandler.Bodies[^1];
        var bootstrapGrant = Uri.UnescapeDataString(ownerBootstrap.Fragment.Split('=', 2)[1]);
        Assert.Equal("#setup", ownerBootstrap.Fragment.Split('=', 2)[0]);
        Assert.DoesNotContain(bootstrapGrant, bootstrapBody, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(bootstrapGrant))), bootstrapBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Artifact_deployment_uploads_requested_assets_worker_bindings_and_enables_the_account_subdomain()
    {
        var index = Encoding.UTF8.GetBytes("<!doctype html><title>Openza</title>");
        var styles = Encoding.UTF8.GetBytes("body{color:#123456}");
        var indexHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(index).AsSpan(0, 16));
        var stylesHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(styles).AsSpan(0, 16));
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, "{\"success\":true,\"result\":{\"buckets\":[[\"" + indexHash + "\"],[\"" + stylesHash + "\"]],\"jwt\":\"asset-upload-token\"}}"),
            Json(HttpStatusCode.OK, """{"success":true,"result":{}}"""),
            Json(HttpStatusCode.Created, """{"success":true,"result":{"jwt":"asset-completion-token"}}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":{"id":"openza-personal-attempt12345"}}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":{"enabled":true,"previews_enabled":false}}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":{"subdomain":"personal-example"}}"""));
        var client = new CloudflareArtifactDeploymentClient(
            new HttpClient(handler),
            new PersonalCloudDeploymentArtifact(
                "1.2.3",
                new DateOnly(2026, 9, 13),
                Encoding.UTF8.GetBytes("export default {fetch(){return new Response('ok')}}"),
                [
                    new PersonalCloudStaticAsset("/index.html", "text/html", index),
                    new PersonalCloudStaticAsset("/styles.css", "text/css", styles),
                ]));

        var deployed = await client.DeployAsync(
            "temporary-oauth-token", "account_one", "database_one",
            "openza-personal-attempt12345", "pc_attempt_1234567890", "1.2.3");

        Assert.Equal(new Uri("https://openza-personal-attempt12345.personal-example.workers.dev/"), deployed.ServiceOrigin);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal("asset-upload-token", handler.Requests[1].Headers.Authorization!.Parameter);
        Assert.Equal("asset-upload-token", handler.Requests[2].Headers.Authorization!.Parameter);
        Assert.Equal("temporary-oauth-token", handler.Requests[3].Headers.Authorization!.Parameter);
        Assert.Contains(indexHash, handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(index), handler.Bodies[1], StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(styles), handler.Bodies[2], StringComparison.Ordinal);
        Assert.Contains("application/javascript+module", handler.Bodies[3], StringComparison.Ordinal);
        Assert.Contains("\"database_id\":\"database_one\"", handler.Bodies[3], StringComparison.Ordinal);
        Assert.Contains("\"ARTIFACT_VERSION\"", handler.Bodies[3], StringComparison.Ordinal);
        Assert.Contains("asset-completion-token", handler.Bodies[3], StringComparison.Ordinal);
        Assert.Contains("\"not_found_handling\":\"single-page-application\"", handler.Bodies[3], StringComparison.Ordinal);
        Assert.DoesNotContain("temporary-oauth-token", handler.Bodies[3], StringComparison.Ordinal);
        Assert.Contains("\"enabled\":true", handler.Bodies[4], StringComparison.Ordinal);
    }

    [Fact]
    public void Artifact_directory_verifies_every_file_before_loading_deployment_and_migrations()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openza-personal-cloud-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "assets"));
        Directory.CreateDirectory(Path.Combine(directory, "migrations"));
        try
        {
            var worker = Encoding.UTF8.GetBytes("export default {fetch(){return new Response('ok')}}");
            var asset = Encoding.UTF8.GetBytes("<!doctype html><title>Openza</title>");
            var migration = Encoding.UTF8.GetBytes("CREATE TABLE example(id TEXT PRIMARY KEY);");
            File.WriteAllBytes(Path.Combine(directory, "worker.js"), worker);
            File.WriteAllBytes(Path.Combine(directory, "assets", "index.html"), asset);
            File.WriteAllBytes(Path.Combine(directory, "migrations", "0001_example.sql"), migration);
            File.WriteAllText(Path.Combine(directory, "artifact-manifest.json"), JsonSerializer.Serialize(new
            {
                formatVersion = 1,
                artifactVersion = "1.2.3",
                compatibilityDate = "2026-09-13",
                worker = ManifestFile("worker.js", worker),
                assets = new[] { new
                {
                    file = "assets/index.html", size = asset.LongLength, sha256 = Sha256(asset),
                    path = "/index.html", contentType = "text/html; charset=utf-8",
                } },
                migrations = new[] { ManifestFile("migrations/0001_example.sql", migration) },
            }));

            var loaded = PersonalCloudArtifactDirectory.Load(directory);

            Assert.Equal("1.2.3", loaded.Deployment.Version);
            Assert.Equal("0001_example.sql", loaded.Migrations.Migrations.Single().Name);
            Assert.Equal(Sha256(migration), loaded.Migrations.Migrations.Single().Sha256);

            File.AppendAllText(Path.Combine(directory, "worker.js"), "tampered");
            var error = Assert.Throws<PersonalCloudK0ContractException>(() => PersonalCloudArtifactDirectory.Load(directory));
            Assert.Equal("artifact_invalid", error.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Migration_journal_skips_an_already_applied_matching_artifact_on_resume()
    {
        var checksum = new string('a', 64);
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, """{"success":true,"result":[]}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":[{"results":[]}]}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":[]}"""),
            Json(HttpStatusCode.OK, """{"success":true,"result":[]}"""),
            Json(HttpStatusCode.OK, $"{{\"success\":true,\"result\":[{{\"results\":[{{\"sha256\":\"{checksum}\"}}]}}]}}"));
        var gateway = new CloudflareProvisioningGateway(
            new CloudflareManagementClient(new HttpClient(handler)),
            new RecordingDeploymentClient(),
            new MigrationBundle("1.2.3", [new PersonalCloudMigration(
                "0001_test.sql", checksum, "CREATE TABLE provisioning_test(id TEXT PRIMARY KEY);")]),
            new PersonalCloudHealthClient(new HttpClient()),
            "Linux desktop",
            providerBridge: true);

        await gateway.ApplyMigrationsAsync("temporary-oauth-token", "account_one", "database_one", "1.2.3");
        await gateway.ApplyMigrationsAsync("temporary-oauth-token", "account_one", "database_one", "1.2.3");

        Assert.Equal(5, handler.Requests.Count);
        Assert.Single(handler.Bodies, body => body.Contains("CREATE TABLE provisioning_test", StringComparison.Ordinal));
        Assert.Equal(2, handler.Bodies.Count(body => body.Contains("SELECT sha256", StringComparison.Ordinal)));
    }

    private static Uri CallbackFor(Uri authorization, Uri redirect)
    {
        var state = authorization.Query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.TrimStart('?').Split('=', 2))
            .Single(parts => Uri.UnescapeDataString(parts[0]) == "state")[1];
        return new Uri($"{redirect}?code=temporary-code&state={state}");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static object ManifestFile(string file, byte[] content) => new { file, size = content.LongLength, sha256 = Sha256(content) };
    private static string Sha256(byte[] content) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

    private sealed class CallbackBroker(Func<Uri, Uri, Uri> callback) : ICloudflareAuthorizationBroker
    {
        public Task<Uri> AuthorizeAsync(Uri authorizationUri, Uri redirectUri, CancellationToken cancellationToken = default) =>
            Task.FromResult(callback(authorizationUri, redirectUri));
    }

    private sealed class RecordingProgress : IProgress<PersonalCloudSetupProgress>
    {
        public List<PersonalCloudSetupStep> Steps { get; } = [];
        public void Report(PersonalCloudSetupProgress value) => Steps.Add(value.Step);
    }

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.Dequeue();
        }
    }

    private sealed class RecordingCredentialStore : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default) { Values[key] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) { Values.Remove(key); return Task.CompletedTask; }
    }

    private sealed class RecordingDeploymentClient : ICloudflareArtifactDeploymentClient
    {
        public string? DatabaseId { get; private set; }
        public string? ResourceName { get; private set; }

        public Task<CloudflareProvisioningApplication> DeployAsync(
            string accessToken,
            string accountId,
            string databaseId,
            string resourceName,
            string attemptId,
            string artifactVersion,
            CancellationToken cancellationToken = default)
        {
            DatabaseId = databaseId;
            ResourceName = resourceName;
            return Task.FromResult(new CloudflareProvisioningApplication(
                resourceName,
                new Uri($"https://{resourceName}.workers.dev/")));
        }
    }

    private sealed record MigrationBundle(string ArtifactVersion, IReadOnlyList<PersonalCloudMigration> Migrations) : IPersonalCloudMigrationBundle;

    private sealed class MemoryStateStore : IPersonalCloudProvisioningStateStore
    {
        public PersonalCloudProvisioningState? Value { get; private set; }
        public Task<PersonalCloudProvisioningState?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(PersonalCloudProvisioningState state, CancellationToken cancellationToken = default) { Value = state; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken cancellationToken = default) { Value = null; return Task.CompletedTask; }
    }

    private sealed class FailFirstConnectionStore : IPersonalCloudConnectionStore
    {
        private bool _fail = true;
        private PersonalCloudConnectionProfile? _value;
        public PersonalCloudConnectionProfile? Load() => _value;
        public Task SaveAsync(PersonalCloudConnectionProfile connection, CancellationToken cancellationToken = default)
        {
            if (_fail) { _fail = false; throw new IOException("Injected profile save failure."); }
            _value = connection;
            return Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken cancellationToken = default) { _value = null; return Task.CompletedTask; }
    }

    private sealed class RecordingGateway : ICloudflareProvisioningGateway
    {
        public bool FailFirstDeployment { get; init; }
        public int ResolveCalls { get; private set; }
        public int DatabaseCalls { get; private set; }
        public int DeployCalls { get; private set; }
        public int MigrationCalls { get; private set; }
        public int HealthCalls { get; private set; }
        public int PairingCalls { get; private set; }
        public int OwnerBootstrapCalls { get; private set; }

        public Task<CloudflareProvisioningAccount> ResolveAccountAsync(string accessToken, CancellationToken cancellationToken = default)
        { ResolveCalls++; return Task.FromResult(new CloudflareProvisioningAccount("account_1234567890")); }
        public Task<CloudflareProvisioningDatabase> EnsureDatabaseAsync(string accessToken, string accountId, string attemptId, CancellationToken cancellationToken = default)
        { DatabaseCalls++; return Task.FromResult(new CloudflareProvisioningDatabase("database_1234567890")); }
        public Task<CloudflareProvisioningApplication> DeployApplicationAsync(string accessToken, string accountId, string databaseId, string attemptId, string artifactVersion, CancellationToken cancellationToken = default)
        {
            DeployCalls++;
            if (FailFirstDeployment && DeployCalls == 1) throw new PersonalCloudK0ContractException("deployment_failed", "Injected deployment failure.");
            return Task.FromResult(new CloudflareProvisioningApplication("openza-personal-test", new Uri("https://openza-personal-test.workers.dev/")));
        }
        public Task ApplyMigrationsAsync(string accessToken, string accountId, string databaseId, string artifactVersion, CancellationToken cancellationToken = default)
        { MigrationCalls++; return Task.CompletedTask; }
        public Task VerifyHealthAsync(Uri serviceOrigin, string artifactVersion, CancellationToken cancellationToken = default)
        { HealthCalls++; return Task.CompletedTask; }
        public Task<PersonalCloudInitialPairing> CreateInitialPairingAsync(string accessToken, string accountId, string databaseId, Uri serviceOrigin, CancellationToken cancellationToken = default)
        { PairingCalls++; return Task.FromResult(new PersonalCloudInitialPairing("pairing-grant-value-1234567890", "Linux desktop", true)); }
        public Task<Uri> CreateOwnerBootstrapAsync(string accessToken, string accountId, string databaseId, Uri serviceOrigin, CancellationToken cancellationToken = default)
        { OwnerBootstrapCalls++; return Task.FromResult(new Uri(serviceOrigin, "#setup=owner-bootstrap-grant-value")); }
    }
}
