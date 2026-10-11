using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed class PersonalCloudHealthClient(HttpClient http)
{
    // Schema 18 adds parent-cycle invariants and 19 adds provider delivery
    // ownership; 20 guards ambiguous provider aliases. Unknown schemas need review.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task VerifyAsync(Uri serviceOrigin, string artifactVersion, CancellationToken cancellationToken = default)
    {
        var body = await InspectAsync(serviceOrigin, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(body.ArtifactVersion, artifactVersion, StringComparison.Ordinal))
            throw new PersonalCloudK0ContractException("health_mismatch", "The deployed Personal Cloud version does not match the requested artifact.");
    }

    public async Task<PersonalCloudHealth> InspectAsync(Uri serviceOrigin, CancellationToken cancellationToken = default)
    {
        if (!serviceOrigin.IsAbsoluteUri ||
            (serviceOrigin.Scheme != Uri.UriSchemeHttps && !(serviceOrigin.Scheme == Uri.UriSchemeHttp && serviceOrigin.IsLoopback)) ||
            !string.IsNullOrEmpty(serviceOrigin.Query) || !string.IsNullOrEmpty(serviceOrigin.Fragment))
            throw new PersonalCloudK0ContractException("invalid_request", "A secure Personal Cloud origin is required for health verification.");
        using var response = await http.GetAsync(new Uri(serviceOrigin, "/health"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PersonalCloudK0ContractException("health_failed", "The deployed Personal Cloud did not pass its health check.");
        try
        {
            var body = await response.Content.ReadFromJsonAsync<HealthResponse>(Json, cancellationToken).ConfigureAwait(false);
            if (body is null || body.Status != "ok" || body.ProtocolVersion != PersonalCloudK0Protocol.Version ||
                body.Product != PersonalCloudK0Protocol.TasksProduct || body.SchemaVersion is not (17 or 18 or 19 or 20) ||
                string.IsNullOrWhiteSpace(body.ArtifactVersion) || body.ArtifactVersion.Length > 128)
                throw new PersonalCloudK0ContractException("health_mismatch", "The deployed Personal Cloud is not compatible with this Openza build.");
            return new PersonalCloudHealth(body.ArtifactVersion, body.SchemaVersion);
        }
        catch (JsonException exception)
        {
            throw new PersonalCloudK0ContractException("invalid_response", "Personal Cloud returned an invalid health response.", exception);
        }
    }

    private sealed record HealthResponse(string Status, int ProtocolVersion, string Product, string ArtifactVersion, int SchemaVersion);
}

public sealed record PersonalCloudHealth(string ArtifactVersion, int SchemaVersion);
