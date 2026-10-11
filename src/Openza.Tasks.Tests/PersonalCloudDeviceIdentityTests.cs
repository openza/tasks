using System.Net;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudDeviceIdentityTests
{
    [Theory]
    [InlineData("https://tasks.example.test/")]
    [InlineData("http://localhost:8787/")]
    [InlineData("http://127.0.0.1:8787/")]
    [InlineData("http://[::1]:8787/")]
    public async Task Existing_cloud_pairing_verifies_health_then_saves_device_and_connection(string serviceOrigin)
    {
        var credentials = new RecordingCredentialStore();
        var connections = new RecordingConnectionStore();
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, """
                {"status":"ok","protocolVersion":1,"product":"tasks","artifactVersion":"0.1.0-preview.3","schemaVersion":17}
                """),
            Json(HttpStatusCode.Created, """
                {"protocolVersion":1,"instanceId":"instance_personal","deviceId":"device_1234567890","refreshCredential":"refresh-secret-value-1234567890","capabilities":["desktop_sync","provider_bridge"],"refreshExpiresAt":"2099-01-01T00:00:00Z"}
                """));
        var http = new HttpClient(handler);
        var store = new Openza.Tasks.Core.Data.SqliteTaskStore(Path.Combine(
            Path.GetTempPath(), $"openza-pair-existing-{Guid.NewGuid():N}.db"));
        await store.InitializeAsync();
        var service = new PersonalCloudSetupService(null!, "0.1.0-preview.3",
            connectionStore: connections,
            deviceIdentity: new PersonalCloudDeviceIdentityClient(http, credentials),
            syncHttp: http,
            taskStore: store);

        var connection = await service.PairExistingAsync(
            new Uri(serviceOrigin), "pair-one-time-secret-123456789", "Ubuntu desktop", true);

        Assert.Equal("0.1.0-preview.3", connection.ArtifactVersion);
        Assert.Equal("instance_personal", connection.InstanceId);
        Assert.Equal(connection, service.CurrentConnection);
        Assert.Equal(connection, connections.Value);
        Assert.Single(credentials.Values);
        Assert.Equal("/health", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/auth/pair", handler.Requests[1].RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("http://tasks.example.test/")]
    [InlineData("http://192.168.1.2:8787/")]
    [InlineData("http://localhost.example.test:8787/")]
    [InlineData("http://localhost:8787/?token=ignored")]
    [InlineData("http://localhost:8787/#ignored")]
    public async Task Health_rejects_remote_http_and_non_origin_addresses_before_sending(string serviceOrigin)
    {
        var handler = new QueueHandler();
        var health = new PersonalCloudHealthClient(new HttpClient(handler));
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => health.InspectAsync(new Uri(serviceOrigin)));
        Assert.Equal("invalid_request", error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Pairing_saves_only_the_refresh_credential_in_the_OS_store_and_uses_short_access_tokens()
    {
        var credentials = new RecordingCredentialStore();
        var handler = new QueueHandler(
            Json(HttpStatusCode.Created, """
                {"protocolVersion":1,"instanceId":"instance_personal","deviceId":"device_1234567890","refreshCredential":"refresh-secret-value-1234567890","capabilities":["desktop_sync","provider_bridge"],"refreshExpiresAt":"2099-01-01T00:00:00Z"}
                """),
            Json(HttpStatusCode.OK, """
                {"protocolVersion":1,"accessToken":"access-secret-value-1234567890","expiresAt":"2099-01-01T00:10:00Z","capabilities":["desktop_sync","provider_bridge"]}
                """));
        var client = new PersonalCloudDeviceIdentityClient(new HttpClient(handler), credentials);
        var origin = new Uri("https://tasks.example.test");

        var paired = await client.PairAsync(origin, "pair-one-time-secret-123456789", "Ubuntu desktop", providerBridge: true);

        Assert.Single(credentials.Values);
        Assert.DoesNotContain("pair-one-time-secret", credentials.Values.Single().Value, StringComparison.Ordinal);
        Assert.Contains("refresh-secret-value", credentials.Values.Single().Value, StringComparison.Ordinal);
        Assert.Equal("instance_personal", paired.InstanceId);
        Assert.Equal("device_1234567890", paired.DeviceId);
        Assert.Equal(paired, await client.GetStoredDeviceAsync(origin));
        var access = await client.IssueAccessTokenAsync(origin);
        Assert.Equal("instance_personal", access.InstanceId);
        Assert.Equal("device_1234567890", access.DeviceId);
        Assert.Equal("access-secret-value-1234567890", access.AccessToken);
        Assert.Equal(["desktop_sync", "provider_bridge"], access.Capabilities);
        Assert.DoesNotContain(credentials.Values, pair => pair.Value.Contains(access.AccessToken, StringComparison.Ordinal));

        Assert.Equal("/auth/pair", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"capabilities\":[\"desktop_sync\",\"provider_bridge\"]", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Equal("/auth/device/token", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Contains("refresh-secret-value", handler.Bodies[1], StringComparison.Ordinal);

        await client.ForgetAsync(origin);
        Assert.Empty(credentials.Values);
    }

    [Fact]
    public async Task Identity_client_rejects_insecure_remote_origins_and_never_returns_server_details()
    {
        var client = new PersonalCloudDeviceIdentityClient(new HttpClient(new QueueHandler()), new InMemoryCredentialStore());
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.PairAsync(
            new Uri("http://tasks.example.test"), "pair-one-time-secret-123456789", "Desktop", false));

        var rejected = new PersonalCloudDeviceIdentityClient(new HttpClient(new QueueHandler(
            Json(HttpStatusCode.Unauthorized, "{\"private\":\"database detail\"}"))), new InMemoryCredentialStore());
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => rejected.PairAsync(
            new Uri("https://tasks.example.test"), "pair-one-time-secret-123456789", "Desktop", false));
        Assert.DoesNotContain("database detail", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Identity_client_rejects_an_unknown_server_protocol_without_saving_a_credential()
    {
        var credentials = new RecordingCredentialStore();
        var client = new PersonalCloudDeviceIdentityClient(new HttpClient(new QueueHandler(
            Json(HttpStatusCode.Created, """
                {"protocolVersion":2,"instanceId":"instance_personal","deviceId":"device_1234567890","refreshCredential":"refresh-secret-value-1234567890","capabilities":["desktop_sync"],"refreshExpiresAt":"2099-01-01T00:00:00Z"}
                """))), credentials);

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.PairAsync(
            new Uri("https://tasks.example.test"), "pair-one-time-secret-123456789", "Desktop", false));

        Assert.Equal("unsupported_protocol", error.Code);
        Assert.Empty(credentials.Values);
    }

    [Fact]
    public async Task Disconnect_revokes_the_current_cloud_device_before_forgetting_its_credential()
    {
        var credentials = new RecordingCredentialStore();
        var handler = new QueueHandler(
            Json(HttpStatusCode.Created, """
                {"protocolVersion":1,"instanceId":"instance_personal","deviceId":"device_1234567890","refreshCredential":"refresh-secret-value-1234567890","capabilities":["desktop_sync"],"refreshExpiresAt":"2099-01-01T00:00:00Z"}
                """),
            Json(HttpStatusCode.OK, """
                {"protocolVersion":1,"accessToken":"access-secret-value-1234567890","expiresAt":"2099-01-01T00:10:00Z","capabilities":["desktop_sync"]}
                """),
            Json(HttpStatusCode.OK, """
                {"protocolVersion":1,"outcome":"revoked","deviceId":"device_1234567890"}
                """));
        var client = new PersonalCloudDeviceIdentityClient(new HttpClient(handler), credentials);
        var origin = new Uri("https://tasks.example.test");
        await client.PairAsync(origin, "pair-one-time-secret-123456789", "Ubuntu desktop", false);

        await client.RevokeAsync(origin);

        Assert.Empty(credentials.Values);
        Assert.Equal(["/auth/pair", "/auth/device/token", "/auth/device/revoke"],
            handler.Requests.Select(request => request.RequestUri!.AbsolutePath));
        Assert.Equal("Bearer", handler.Requests[2].Headers.Authorization!.Scheme);
        Assert.Equal("access-secret-value-1234567890", handler.Requests[2].Headers.Authorization!.Parameter);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.Dequeue();
        }
    }

    private sealed class RecordingCredentialStore : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default) { Values[key] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) { Values.TryGetValue(key, out var value); return Task.FromResult(value); }
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) { Values.Remove(key); return Task.CompletedTask; }
    }

    private sealed class RecordingConnectionStore : IPersonalCloudConnectionStore
    {
        public PersonalCloudConnectionProfile? Value { get; private set; }
        public PersonalCloudConnectionProfile? Load() => Value;
        public Task SaveAsync(PersonalCloudConnectionProfile connection, CancellationToken cancellationToken = default)
        { Value = connection; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken cancellationToken = default)
        { Value = null; return Task.CompletedTask; }
    }
}
