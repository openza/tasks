using System.Net;
using System.Text;
using Openza.Tasks.Core.PersonalCloud;

const string defaultClientId = "ab8248060e7dc03bb9ccf7f71c179e45";
var clientId = args.Length switch
{
    0 => defaultClientId,
    2 when args[0] == "--client-id" && !string.IsNullOrWhiteSpace(args[1]) => args[1],
    _ => throw new ArgumentException("Usage: dotnet run --project Openza.Tasks.PersonalCloud.OAuthProbe.csproj [--client-id <public-client-id>]")
};

var redirectUri = new Uri("http://127.0.0.1:48192/cloudflare/callback");
var expectedScopes = new[] { "account-settings.read", "d1.write", "workers-scripts.write" };
var configuration = new CloudflareOAuthConfiguration(clientId, redirectUri, expectedScopes);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var oauth = new CloudflareOAuthClient(http, configuration);
CloudflareOAuthAccess? access = null;

try
{
    access = await oauth.AuthorizeAsync(new ConsoleLoopbackBroker(redirectUri));
    var granted = (access.GrantedScope ?? string.Empty)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.Ordinal);
    if (expectedScopes.Any(scope => !granted.Contains(scope)))
        throw new InvalidOperationException("Cloudflare did not grant every required Personal Cloud scope.");

    var accounts = await new CloudflareManagementClient(http).ListAccountsAsync(access.AccessToken);
    if (accounts.Count == 0)
        throw new InvalidOperationException("Cloudflare authorization did not expose an account.");

    Console.WriteLine($"PASS: PKCE exchange succeeded, all {expectedScopes.Length} required scopes were granted, and {accounts.Count} account(s) were readable.");
}
finally
{
    if (access is not null)
    {
        await oauth.RevokeAsync(access.AccessToken);
        Console.WriteLine("PASS: temporary Cloudflare authorization was revoked.");
    }
}

sealed class ConsoleLoopbackBroker(Uri redirectUri) : ICloudflareAuthorizationBroker
{
    public async Task<Uri> AuthorizeAsync(Uri authorizationUri, Uri expectedRedirectUri, CancellationToken cancellationToken = default)
    {
        if (expectedRedirectUri != redirectUri)
            throw new InvalidOperationException("The OAuth callback does not match the registered loopback redirect.");

        using var listener = new HttpListener();
        listener.Prefixes.Add($"{redirectUri.Scheme}://{redirectUri.Host}:{redirectUri.Port}/");
        listener.Start();
        Console.WriteLine("Open this Cloudflare authorization URL in a browser:");
        Console.WriteLine(authorizationUri.AbsoluteUri);

        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
        var callback = context.Request.Url ?? throw new InvalidOperationException("Cloudflare returned an invalid callback URL.");
        var response = Encoding.UTF8.GetBytes("Openza verified the Cloudflare connection. You can close this tab.");
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = response.Length;
        await context.Response.OutputStream.WriteAsync(response, cancellationToken);
        context.Response.Close();
        return callback;
    }
}
