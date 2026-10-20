using System.Net;
using System.Text;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudDeviceIdentityDispatchTests : IDisposable
{
    private static readonly Uri Origin = new("https://identity-dispatch.example.test/");
    private const string Instance = "instance_identity_synthetic", Device = "device_identity_synthetic";
    private const string Refresh = "publicfake-refresh-identity-value-12345";
    private const string Grant = "publicfake-pairing-grant-identity-12345";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-identity-dispatch", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private PersonalCloudDispatchPolicy Policy() => new(_directory, clock: _clock, resumeJitter: () => TimeSpan.Zero);

    private static async Task<InMemoryCredentialStore> CredentialsAsync(bool tokenRequest)
    {
        var credentials = new InMemoryCredentialStore();
        if (tokenRequest)
            await credentials.SaveAsync(PersonalCloudDeviceIdentityClient.CredentialKey(Origin), PersonalCloudK0Codec.Serialize(new
            { serviceOrigin = Origin.AbsoluteUri, instanceId = Instance, deviceId = Device, refreshCredential = Refresh }));
        return credentials;
    }

    private static async Task InvokeAsync(PersonalCloudDeviceIdentityClient identity, bool tokenRequest, CancellationToken cancellationToken = default)
    {
        if (tokenRequest) await identity.IssueAccessTokenAsync(Origin, cancellationToken);
        else await identity.PairAsync(Origin, Grant, " Synthetic desktop ", true, cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_identity_timeout_persists_backoff_and_attempt_across_restart(bool tokenRequest)
    {
        var credentials = await CredentialsAsync(tokenRequest);
        var originalCredential = await credentials.GetAsync(PersonalCloudDeviceIdentityClient.CredentialKey(Origin));
        var calls = 0;
        var linkedTokenCancelled = false;
        using var http = PersonalCloudDispatchHttp.Create(Policy(), new Handler(async (_, token) =>
        {
            calls++;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { linkedTokenCancelled = token.IsCancellationRequested; throw; }
            return new(HttpStatusCode.OK);
        }));
        http.Timeout = TimeSpan.FromMilliseconds(200);
        var identity = new PersonalCloudDeviceIdentityClient(http, credentials, _clock);
        var failure = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => InvokeAsync(identity, tokenRequest));
        Assert.True(linkedTokenCancelled);
        Assert.Equal("cloud_retry_backoff", failure.Code);
        var durable = await Policy().GetStatusAsync(Origin);
        Assert.Equal("cloud_retry_backoff", durable.Code);
        Assert.True(durable.IsDurable);
        Assert.Equal(1, durable.AttemptedRequests);
        Assert.Equal(0, durable.AttemptedEnvelopes);
        Assert.Equal(0, durable.ReservedWriteUnits);
        Assert.Equal("cloud_retry_backoff", (await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => InvokeAsync(identity, tokenRequest))).Code);
        Assert.Equal(1, calls); // The actual identity caller cannot immediately issue another request.
        Assert.Equal(1, (await Policy().GetStatusAsync(Origin)).AttemptedRequests);
        Assert.Equal(originalCredential, await credentials.GetAsync(PersonalCloudDeviceIdentityClient.CredentialKey(Origin)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_identity_caller_cancellation_preserves_attempt_without_backoff_or_credential_change(bool tokenRequest)
    {
        var credentials = await CredentialsAsync(tokenRequest);
        var originalCredential = await credentials.GetAsync(PersonalCloudDeviceIdentityClient.CredentialKey(Origin));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = PersonalCloudDispatchHttp.Create(Policy(), new Handler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK);
        }));
        http.Timeout = TimeSpan.FromSeconds(10);
        var identity = new PersonalCloudDeviceIdentityClient(http, credentials, _clock);
        using var caller = new CancellationTokenSource();
        var pending = InvokeAsync(identity, tokenRequest, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var durable = await Policy().GetStatusAsync(Origin);
        Assert.False(durable.IsPaused);
        Assert.True(durable.IsDurable);
        Assert.Equal(1, durable.AttemptedRequests);
        Assert.Equal(0, durable.AttemptedEnvelopes);
        Assert.Equal(0, durable.ReservedWriteUnits);
        Assert.Equal(originalCredential, await credentials.GetAsync(PersonalCloudDeviceIdentityClient.CredentialKey(Origin)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Identity_JSON_payload_and_response_disposal_are_preserved(bool tokenRequest)
    {
        var credentials = await CredentialsAsync(tokenRequest);
        HttpRequestMessage? captured = null;
        string? payload = null;
        var responseBody = tokenRequest
            ? """{"protocolVersion":1,"accessToken":"publicfake-access-identity-value-12345678","expiresAt":"2099-01-01T00:00:00Z","capabilities":["desktop_sync","provider_bridge"]}"""
            : $$"""{"protocolVersion":1,"instanceId":"{{Instance}}","deviceId":"{{Device}}","refreshCredential":"{{Refresh}}","refreshExpiresAt":"2099-01-01T00:00:00Z","capabilities":["desktop_sync","provider_bridge"]}""";
        var content = new TrackedContent(responseBody);
        using var http = PersonalCloudDispatchHttp.Create(Policy(), new Handler(async (request, token) =>
        {
            captured = request;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(tokenRequest ? "/auth/device/token" : "/auth/pair", request.RequestUri!.AbsolutePath);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("utf-8", request.Content.Headers.ContentType.CharSet);
            payload = await request.Content.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = content };
        }));
        await InvokeAsync(new(http, credentials, _clock), tokenRequest);
        Assert.Equal(tokenRequest
            ? $$"""{"deviceId":"{{Device}}","refreshCredential":"{{Refresh}}"}"""
            : $$"""{"grant":"{{Grant}}","deviceName":"Synthetic desktop","capabilities":["desktop_sync","provider_bridge"]}""", payload);
        Assert.True(content.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => captured!.Content!.ReadAsStringAsync());
        Assert.False((await Policy().GetStatusAsync(Origin)).IsPaused);
        Assert.Equal(1, (await Policy().GetStatusAsync(Origin)).AttemptedRequests);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class TrackedContent(string value) : StringContent(value, Encoding.UTF8, "application/json")
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    }
    public void Dispose() => TestDirectory.Delete(_directory);
}
