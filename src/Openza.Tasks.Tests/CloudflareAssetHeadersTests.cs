using System.Net;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class CloudflareAssetHeadersTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("/*\n  X-Content-Type-Options: nosniff\n  Content-Security-Policy: default-src 'self'; frame-ancestors 'none'\n")]
    [InlineData("# Comments and detach rules are parsed by Cloudflare\r\n/*\r\n  X-Test: first\r\n  X-Test: second\r\n/image/*\r\n  ! X-Test\r\n")]
    public async Task Headers_are_metadata_only_and_existing_artifacts_remain_compatible(string? headers)
    {
        var index = Encoding.UTF8.GetBytes("<!doctype html><title>Openza</title>");
        var indexHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(index).AsSpan(0, 16));
        var handler = new RecordingHandler(indexHash);
        var assets = new List<PersonalCloudStaticAsset> { new("/index.html", "text/html", index) };
        if (headers is not null) assets.Add(new("/_headers", "text/plain", Encoding.UTF8.GetBytes(headers)));
        var client = CreateClient(handler, assets);

        await DeployAsync(client);

        Assert.Equal(5, handler.Bodies.Count);
        using var manifest = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Single(manifest.RootElement.GetProperty("manifest").EnumerateObject());
        Assert.False(manifest.RootElement.GetProperty("manifest").TryGetProperty("/_headers", out _));
        Assert.Contains(Convert.ToBase64String(index), handler.Bodies[1]);
        if (headers is not null)
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(headers)), handler.Bodies[1]);
        Assert.NotNull(handler.Metadata);
        using var metadata = JsonDocument.Parse(handler.Metadata!);
        var config = metadata.RootElement.GetProperty("assets").GetProperty("config");
        Assert.Equal("single-page-application", config.GetProperty("not_found_handling").GetString());
        if (headers is null) Assert.False(config.TryGetProperty("_headers", out _));
        else Assert.Equal(headers, config.GetProperty("_headers").GetString());
    }

    [Theory]
    [InlineData("invalid-utf8")]
    [InlineData("duplicate")]
    [InlineData("headers-only")]
    public async Task Invalid_headers_artifacts_fail_before_any_network_request(string scenario)
    {
        var handler = new RecordingHandler("unused");
        var assets = new List<PersonalCloudStaticAsset>();
        if (scenario != "headers-only") assets.Add(new("/index.html", "text/html", [1]));
        assets.Add(new("/_headers", "text/plain", scenario == "invalid-utf8" ? [0xc3, 0x28] : Encoding.UTF8.GetBytes("/*\n  X-Test: value\n")));
        if (scenario == "duplicate") assets.Add(assets.Last());

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => DeployAsync(CreateClient(handler, assets)));

        Assert.Equal("artifact_invalid", error.Code);
        Assert.Empty(handler.Bodies);
    }

    private static CloudflareArtifactDeploymentClient CreateClient(HttpMessageHandler handler, IReadOnlyList<PersonalCloudStaticAsset> assets) =>
        new(new HttpClient(handler), new("1.2.3", new DateOnly(2026, 9, 13), Encoding.UTF8.GetBytes("export default {}"), assets));

    private static Task<CloudflareProvisioningApplication> DeployAsync(CloudflareArtifactDeploymentClient client) =>
        client.DeployAsync("temporary-test-token", "account_test", "database_test", "openza-test", "attempt_test", "1.2.3");

    private sealed class RecordingHandler(string hash) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public string? Metadata { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            if (request.Method == HttpMethod.Put)
            {
                var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
                Metadata = await multipart.Single(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "metadata")
                    .ReadAsStringAsync(cancellationToken);
            }
            var response = Bodies.Count switch
            {
                1 => JsonSerializer.Serialize(new { success = true, result = new { buckets = new[] { new[] { hash } }, jwt = "test-upload-jwt" } }),
                2 => """{"success":true,"result":{"jwt":"test-completion-jwt"}}""",
                3 => """{"success":true,"result":{}}""",
                4 => """{"success":true,"result":{"enabled":true}}""",
                5 => """{"success":true,"result":{"subdomain":"example"}}""",
                _ => throw new InvalidOperationException("Unexpected deployment request"),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
