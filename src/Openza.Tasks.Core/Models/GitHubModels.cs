using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.Models;

public sealed record GitHubRepositoryInfo(string Owner, string Name, string FullName, bool IsPrivate, bool HasIssues, bool IsArchived);

public sealed record GitHubLabelInfo(string Name, string Color);

public sealed record GitHubUserInfo(string Login);

public sealed record GitHubIssueCreateRequest(
    string Owner,
    string Repository,
    string Title,
    string Body,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Assignees);

public sealed record GitHubIssueCreateResult(
    long Id,
    int Number,
    string Owner,
    string Repository,
    string Title,
    string Url,
    string State,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Assignees)
{
    public string ExternalId => $"{Owner}/{Repository}#{Number}";
    public string DisplayName => ExternalId;
}

public sealed record GitHubIssueStatusResult(bool Exists, bool Unavailable, string? Error);

public sealed record GitHubTokenValidationResult(bool Success, string? Username, string? Error, IReadOnlySet<string> Scopes);

public sealed record GitHubConnectionSettings
{
    public string Username { get; init; } = string.Empty;
    public string DefaultRepositoryFullName { get; init; } = string.Empty;
    public DateTimeOffset? ConnectedAt { get; init; }
    public string LastStatus { get; init; } = string.Empty;
}

public sealed record GitHubDeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; init; } = string.Empty;

    [JsonPropertyName("user_code")]
    public string UserCode { get; init; } = string.Empty;

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; init; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("interval")]
    public int Interval { get; init; } = 5;
}

public sealed record GitHubTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }
}
