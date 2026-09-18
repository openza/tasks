using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed class LoopbackCloudflareAuthorizationBroker(
    Func<Uri, CancellationToken, Task> openBrowser,
    TimeSpan? timeout = null) : ICloudflareAuthorizationBroker
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(5);

    public async Task<Uri> AuthorizeAsync(
        Uri authorizationUri,
        Uri redirectUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        if (!redirectUri.IsAbsoluteUri || redirectUri.Scheme != Uri.UriSchemeHttp || !redirectUri.IsLoopback ||
            redirectUri.Port is <= 0 or > 65535 || string.IsNullOrEmpty(redirectUri.AbsolutePath))
            throw new PersonalCloudK0ContractException("invalid_request", "Desktop Cloudflare authorization requires a fixed loopback callback.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var address = redirectUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.Parse(redirectUri.Host);
        var listener = new TcpListener(address, redirectUri.Port);
        try
        {
            listener.Start(1);
            await openBrowser(authorizationUri, deadline.Token).ConfigureAwait(false);
            using var client = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            if (requestLine is null || requestLine.Length > 4096)
                throw Invalid("The Cloudflare callback request is invalid.");
            var parts = requestLine.Split(' ', 3);
            if (parts.Length != 3 || parts[0] != "GET" || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                throw Invalid("The Cloudflare callback request is invalid.");
            for (var index = 0; index < 64; index++)
            {
                var header = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                if (header is null || header.Length > 8192) throw Invalid("The Cloudflare callback headers are invalid.");
                if (header.Length == 0) break;
                if (index == 63) throw Invalid("The Cloudflare callback contains too many headers.");
            }

            if (!Uri.TryCreate(redirectUri, parts[1], out var callback) ||
                callback.GetLeftPart(UriPartial.Path) != redirectUri.GetLeftPart(UriPartial.Path))
                throw Invalid("The Cloudflare callback address does not match Openza.");

            var html = "<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width'><title>Openza Personal Cloud</title><style>body{font:16px system-ui;margin:10vh auto;max-width:36rem;padding:2rem;color:#17251d}h1{font-size:1.6rem}</style><h1>Return to Openza</h1><p>Cloudflare authorization has returned to the desktop app. You can close this tab.</p>";
            var payload = Encoding.UTF8.GetBytes(html);
            var headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\nContent-Length: " + payload.Length + "\r\n\r\n");
            await stream.WriteAsync(headers, deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            return callback;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PersonalCloudK0ContractException("oauth_timeout", "Cloudflare authorization timed out.");
        }
        catch (SocketException exception)
        {
            throw new PersonalCloudK0ContractException("oauth_callback_unavailable", "Openza could not open the local Cloudflare callback.", exception);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static PersonalCloudK0ContractException Invalid(string message) => new("invalid_response", message);
}
