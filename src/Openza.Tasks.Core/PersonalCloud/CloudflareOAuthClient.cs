using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed record CloudflareOAuthConfiguration(
    string ClientId,
    Uri RedirectUri,
    IReadOnlyList<string> Scopes)
{
    public static readonly Uri AuthorizationEndpoint = new("https://dash.cloudflare.com/oauth2/auth");
    public static readonly Uri TokenEndpoint = new("https://dash.cloudflare.com/oauth2/token");
    public static readonly Uri RevocationEndpoint = new("https://dash.cloudflare.com/oauth2/revoke");

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 256)
            throw Invalid("Cloudflare OAuth client ID is not configured.");
        if (!RedirectUri.IsAbsoluteUri ||
            (RedirectUri.Scheme != Uri.UriSchemeHttps && !(RedirectUri.Scheme == Uri.UriSchemeHttp && RedirectUri.IsLoopback)) ||
            !string.IsNullOrEmpty(RedirectUri.Fragment))
            throw Invalid("Cloudflare OAuth redirect URI must use HTTPS or a loopback development address.");
        if (Scopes.Count == 0 || Scopes.Count > 32 || Scopes.Any(scope =>
                string.IsNullOrWhiteSpace(scope) || scope.Length > 128 || scope.Any(char.IsWhiteSpace)))
            throw Invalid("Cloudflare OAuth scopes are not configured correctly.");
    }

    private static PersonalCloudK0ContractException Invalid(string message) => new("invalid_request", message);
}

public sealed record CloudflareOAuthRequest(Uri AuthorizationUri, string State, string CodeVerifier);

public sealed record CloudflareOAuthAccess(string AccessToken, DateTimeOffset ExpiresAt, string? GrantedScope);

public interface ICloudflareAuthorizationBroker
{
    Task<Uri> AuthorizeAsync(Uri authorizationUri, Uri redirectUri, CancellationToken cancellationToken = default);
}

public sealed class CloudflareOAuthClient(HttpClient http, CloudflareOAuthConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public CloudflareOAuthRequest BeginAuthorization()
    {
        configuration.Validate();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = configuration.ClientId,
            ["redirect_uri"] = configuration.RedirectUri.AbsoluteUri,
            ["scope"] = string.Join(' ', configuration.Scopes),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        return new CloudflareOAuthRequest(AppendQuery(CloudflareOAuthConfiguration.AuthorizationEndpoint, query), state, verifier);
    }

    public async Task<CloudflareOAuthAccess> AuthorizeAsync(
        ICloudflareAuthorizationBroker broker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        var request = BeginAuthorization();
        var callback = await broker.AuthorizeAsync(request.AuthorizationUri, configuration.RedirectUri, cancellationToken)
            .ConfigureAwait(false);
        var code = ValidateCallback(callback, request.State);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = configuration.ClientId,
            ["code"] = code,
            ["redirect_uri"] = configuration.RedirectUri.AbsoluteUri,
            ["code_verifier"] = request.CodeVerifier,
        });
        using var response = await http.PostAsync(CloudflareOAuthConfiguration.TokenEndpoint, content, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PersonalCloudK0ContractException("oauth_exchange_failed", "Cloudflare authorization could not be completed.");
        TokenResponse body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, cancellationToken).ConfigureAwait(false)
                ?? throw Invalid("Cloudflare returned an empty OAuth response.");
        }
        catch (JsonException exception)
        {
            throw new PersonalCloudK0ContractException("invalid_response", "Cloudflare returned an invalid OAuth response.", exception);
        }
        if (!string.Equals(body.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(body.AccessToken) || body.AccessToken.Length > 4096 || body.ExpiresIn is <= 0 or > 86400)
            throw Invalid("Cloudflare returned invalid temporary authorization.");
        return new CloudflareOAuthAccess(body.AccessToken, DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn), body.Scope);
    }

    public async Task RevokeAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, CloudflareOAuthConfiguration.RevocationEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = accessToken,
                ["client_id"] = configuration.ClientId,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PersonalCloudK0ContractException("oauth_revoke_failed", "Cloudflare authorization could not be discarded.");
    }

    internal static string ValidateCallback(Uri callback, string expectedState)
    {
        if (!callback.IsAbsoluteUri) throw Invalid("Cloudflare returned an invalid OAuth callback.");
        var values = ParseQuery(callback.Query);
        if (!values.TryGetValue("state", out var state) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
            throw new PersonalCloudK0ContractException("oauth_state_mismatch", "Cloudflare authorization did not match this setup attempt.");
        if (values.TryGetValue("error", out _))
            throw new PersonalCloudK0ContractException("oauth_denied", "Cloudflare authorization was cancelled or denied.");
        if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code) || code.Length > 2048)
            throw Invalid("Cloudflare did not return an authorization code.");
        return code;
    }

    private static Uri AppendQuery(Uri endpoint, IReadOnlyDictionary<string, string> values) =>
        new($"{endpoint}?{string.Join('&', values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))}");

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = Uri.UnescapeDataString((parts.Length == 2 ? parts[1] : string.Empty).Replace('+', ' '));
            if (!values.TryAdd(key, value)) throw Invalid("Cloudflare returned a duplicate OAuth parameter.");
        }
        return values;
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static PersonalCloudK0ContractException Invalid(string message) => new("invalid_response", message);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope);
}
