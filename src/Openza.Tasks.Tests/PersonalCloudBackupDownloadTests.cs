using System.Net;
using System.Text;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.Services;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudBackupDownloadTests
{
    private static readonly CloudflareD1Export Export = new("backup.sql", new Uri("https://downloads.example.test/backup.sql?signature=test-only"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_download_saves_bytes_at_selected_path_with_private_permissions(bool existing)
    {
        var sql = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("INSERT INTO sample VALUES ('Title — café');")).ToArray();
        using var files = new Files();
        if (existing) File.WriteAllText(files.Target, "previous backup");
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(Export.DownloadUri, request.RequestUri);
            Assert.Null(request.Headers.Authorization);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(sql) };
        }));

        await new PersonalCloudBackupDownloader(http).SaveAsync(Export, files.Target);

        Assert.Equal(sql, await File.ReadAllBytesAsync(files.Target));
        Assert.Single(Directory.GetFiles(files.Directory));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files.Target));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Failed_or_redirected_download_does_not_replace_existing_backup(HttpStatusCode status)
    {
        using var files = new Files();
        File.WriteAllText(files.Target, "previous backup");
        using var http = new HttpClient(new Handler(_ => new(status) { Content = new StringContent("failure") }));

        await Assert.ThrowsAsync<IOException>(() => new PersonalCloudBackupDownloader(http).SaveAsync(Export, files.Target));

        Assert.Equal("previous backup", File.ReadAllText(files.Target));
        Assert.Single(Directory.GetFiles(files.Directory));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("cancelled")]
    public async Task Incomplete_or_cancelled_download_cleans_partial_file_and_preserves_destination(string scenario)
    {
        using var files = new Files();
        using var cancellation = new CancellationTokenSource();
        File.WriteAllText(files.Target, "previous backup");
        using var http = new HttpClient(new Handler(_ =>
        {
            HttpContent content = scenario == "cancelled"
                ? new StreamContent(new CancellingStream(cancellation))
                : new ByteArrayContent(scenario == "empty" ? [] : [1, 2]);
            if (scenario == "truncated") content.Headers.ContentLength = 10;
            return new(HttpStatusCode.OK) { Content = content };
        }));

        var error = await Record.ExceptionAsync(() => new PersonalCloudBackupDownloader(http).SaveAsync(Export, files.Target, cancellation.Token));

        Assert.NotNull(error);
        if (scenario == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<IOException>(error);
        Assert.Equal("previous backup", File.ReadAllText(files.Target));
        Assert.Single(Directory.GetFiles(files.Directory));
    }

    [Theory]
    [InlineData("http://downloads.example.test/backup.sql")]
    [InlineData("https://user:password@downloads.example.test/backup.sql")]
    public async Task Unsafe_download_address_is_rejected_before_network(string uri)
    {
        using var files = new Files();
        using var http = new HttpClient(new Handler(_ => throw new Exception("Unexpected network request")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PersonalCloudBackupDownloader(http).SaveAsync(Export with { DownloadUri = new Uri(uri) }, files.Target));
        Assert.Empty(Directory.GetFiles(files.Directory));
    }

    [Fact]
    public async Task Cancellation_before_download_sends_no_request_and_creates_no_file()
    {
        using var files = new Files();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = new HttpClient(new Handler(_ => throw new Exception("Unexpected network request")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PersonalCloudBackupDownloader(http).SaveAsync(Export, files.Target, cancellation.Token));
        Assert.Empty(Directory.GetFiles(files.Directory));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class CancellingStream(CancellationTokenSource cancellation) : MemoryStream([1, 2, 3])
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 1 }, cancellationToken);
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class Files : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"openza-export-{Guid.NewGuid():N}");
        public string Target => Path.Combine(Directory, "chosen.sql");
        public Files() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
