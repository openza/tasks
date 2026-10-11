using System.Net;
using System.Security.Cryptography;
using System.Text;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudAccessTokenCacheTests
{
    [Fact]
    public async Task Concurrent_calls_reuse_authority_and_expiry_identity_refresh_and_invalidation_remint()
    {
        var clock = new TestClock(); var handler = new TokenHandler(clock); var credentials = new InMemoryCredentialStore();
        var origin = new Uri("https://token-cache.example.test/");
        var key = $"personal-cloud-device-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin.AbsoluteUri))).ToLowerInvariant()}";
        async Task Save(string device, string refresh) => await credentials.SaveAsync(key, PersonalCloudK0Codec.Serialize(new
        { serviceOrigin = origin.AbsoluteUri, instanceId = "instance-cache", deviceId = device, refreshCredential = refresh }));
        await Save("device-cache-a", "publicfake-refresh-cache-value-a");
        var identity = new PersonalCloudDeviceIdentityClient(new HttpClient(handler), credentials, clock);
        var accesses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => identity.IssueAccessTokenAsync(origin)));
        Assert.Equal(1, handler.Calls); Assert.All(accesses, value => Assert.Equal(accesses[0].AccessToken, value.AccessToken));
        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(31));
        var renewed = await identity.IssueAccessTokenAsync(origin); Assert.Equal(2, handler.Calls); Assert.NotEqual(accesses[0].AccessToken, renewed.AccessToken);
        await identity.InvalidateAccessTokenAsync(origin); await identity.IssueAccessTokenAsync(origin); Assert.Equal(3, handler.Calls);
        await Save("device-cache-a", "publicfake-refresh-cache-value-b");
        await identity.IssueAccessTokenAsync(origin); Assert.Equal(4, handler.Calls);
        await Save("device-cache-b", "publicfake-refresh-cache-value-b");
        Assert.Equal("device-cache-b", (await identity.IssueAccessTokenAsync(origin)).DeviceId); Assert.Equal(5, handler.Calls);
        await identity.ForgetAsync(origin);
        Assert.Equal("unauthorized", (await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => identity.IssueAccessTokenAsync(origin))).Code);
        Assert.Equal(5, handler.Calls);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
    private sealed class TokenHandler(TestClock clock) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/auth/device/token", request.RequestUri!.AbsolutePath); Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(PersonalCloudK0Codec.Serialize(new
                { protocolVersion = 1, accessToken = $"publicfake-access-token-cache-{Calls}", expiresAt = clock.GetUtcNow().AddMinutes(10), capabilities = new[] { "desktop_sync", "provider_bridge" } }), Encoding.UTF8, "application/json"),
            });
        }
    }
}
