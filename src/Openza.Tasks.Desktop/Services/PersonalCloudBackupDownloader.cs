using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Desktop.Services;

public sealed class PersonalCloudBackupDownloader(HttpClient http)
{
    public static PersonalCloudBackupDownloader Default { get; } = new(
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }));

    public async Task SaveAsync(CloudflareD1Export export, string destination, CancellationToken cancellationToken = default)
    {
        if (!export.DownloadUri.IsAbsoluteUri || export.DownloadUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(export.DownloadUri.UserInfo))
            throw new InvalidOperationException("The backup download address is invalid.");
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(path) ?? throw new IOException("The backup destination is invalid.");
        var temporary = Path.Combine(directory, $".openza-cloud-backup-{Guid.NewGuid():N}.tmp");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, export.DownloadUri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                throw new IOException("The backup could not be downloaded. Try Download backup again.");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temporary, options))
            {
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (output.Length == 0 || response.Content.Headers.ContentLength is { } length && length != output.Length)
                    throw new IOException("The backup download was incomplete. Try Download backup again.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
