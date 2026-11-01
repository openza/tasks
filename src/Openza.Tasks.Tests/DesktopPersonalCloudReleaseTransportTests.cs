#if DEBUG
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.Services;

namespace Openza.Tasks.Tests;

[CollectionDefinition("Personal Cloud proxy isolation", DisableParallelization = true)]
public class PersonalCloudProxyIsolationCollection { }

[Collection("Personal Cloud proxy isolation")]
public class DesktopPersonalCloudReleaseTransportTests
{
    [Fact]
    public async Task Dev_transport_bypasses_the_configured_default_proxy()
    {
        var original = HttpClient.DefaultProxy;
        var proxy = new RecordingProxy();
        // This collection cannot overlap other tests while the process-wide
        // proxy is replaced. Restore it even when an assertion fails.
        HttpClient.DefaultProxy = proxy;
        try
        {
            await ServeOnceAsync("direct loopback", null, async root =>
            {
                using var http = Client(Options(root));
                Assert.Equal("direct loopback", await http.GetStringAsync(root));
                Assert.Equal(0, proxy.Calls);
            });
        }
        finally { HttpClient.DefaultProxy = original; }
    }

    private sealed class RecordingProxy : IWebProxy
    {
        public int Calls { get; private set; }
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) { Calls++; return new Uri("http://127.0.0.1:1/"); }
        public bool IsBypassed(Uri host) { Calls++; return false; }
    }

    [Fact]
    public async Task A_local_feed_advertising_remote_metadata_is_rejected()
    {
        const string listing = """
            [{"draft":false,"assets":[
              {"name":"cloud-release.json","browser_download_url":"https://remote.invalid/cloud-release.json"},
              {"name":"cloud-release.json.sig","browser_download_url":"https://remote.invalid/cloud-release.json.sig"}
            ]}]
            """;
        await ServeOnceAsync(listing, null, async root =>
        {
            var options = Options(root);
            using var http = Client(options);
            var source = new GitHubPersonalCloudReleaseSource(http, options);
            var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => source.CheckAsync(null));
            Assert.Equal("release_invalid", error.Code);
            Assert.Contains("must use loopback", error.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("repos/openza/personal-cloud/releases?page=2")]
    [InlineData("repos/openza/personal-cloud/releases/tags/cloud-v0.1.0")]
    [InlineData("assets/cloud-release.json")]
    [InlineData("assets/cloud-release.json.sig")]
    [InlineData("assets/openza-personal-cloud-0.1.0.zip")]
    public async Task Every_release_request_rejects_remote_hosts(string path)
    {
        using var http = Client(Options(new Uri("http://127.0.0.1/")));
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => http.GetAsync($"https://remote.invalid/{path}"));
        Assert.Equal("release_invalid", error.Code);
        Assert.Contains("must use loopback", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("repos/openza/personal-cloud/releases?page=2")]
    [InlineData("repos/openza/personal-cloud/releases/tags/cloud-v0.1.0")]
    [InlineData("assets/cloud-release.json")]
    [InlineData("assets/cloud-release.json.sig")]
    [InlineData("assets/openza-personal-cloud-0.1.0.zip")]
    public async Task Every_release_request_rejects_remote_redirects_and_accepts_direct_loopback(string path)
    {
        await ServeOnceAsync("", "https://remote.invalid/escaped", async root =>
        {
            using var http = Client(Options(root));
            var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => http.GetAsync(new Uri(root, path)));
            Assert.Equal("release_invalid", error.Code);
            Assert.Contains("does not allow redirects", error.Message, StringComparison.Ordinal);
        });
        await ServeOnceAsync("local fixture", null, async root =>
        {
            using var http = Client(Options(root));
            Assert.Equal("local fixture", await http.GetStringAsync(new Uri(root, path)));
        });
    }

    private static PersonalCloudReleaseFeedOptions Options(Uri root) => new()
    {
        Repository = "openza/personal-cloud",
        PublicKeysPem = [],
        DesktopVersion = new Version(1, 3, 0),
        CacheDirectory = Path.GetTempPath(), // CheckAsync and transport tests do not write a cache.
        ApiRoot = root,
    };

    private static HttpClient Client(PersonalCloudReleaseFeedOptions options) =>
        (HttpClient)typeof(DesktopPersonalCloudSetup).GetMethod("ReleaseHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [options])!;

    // A real HTTP redirect exercises HttpClientHandler's redirect configuration,
    // rather than a mock transport that never follows redirects in the first place.
    private static async Task ServeOnceAsync(string body, string? redirect, Func<Uri, Task> action)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var root = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = RespondAsync();
        try { await action(root).WaitAsync(deadline.Token); }
        finally
        {
            deadline.Cancel();
            listener.Stop();
            try { await server; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }

        async Task RespondAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
            var content = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {(redirect is null ? "200 OK" : "302 Found")}\r\n" +
                (redirect is null ? "" : $"Location: {redirect}\r\n") +
                $"Content-Length: {content.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, deadline.Token);
            await stream.WriteAsync(content, deadline.Token);
        }
    }
}
#endif
