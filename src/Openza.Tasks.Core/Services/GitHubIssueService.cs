using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Services;

public sealed class GitHubIssueService(HttpClient httpClient)
{
    public const string DefaultConnectionId = "github_default";
    public const string TokenKey = "github.accessToken";
    public const string OAuthScopes = "repo read:user read:org";

    private const string ClientIdEnvironmentVariable = "OPENZA_TASKS_GITHUB_CLIENT_ID";
    private const string ApiBaseUrl = "https://api.github.com";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";
    private const string GitHubApiVersion = "2022-11-28";
    private const string UserAgent = "Openza-Tasks";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string? ResolveClientId()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
                }
                catch
                {
                    return [];
                }
            })
            .FirstOrDefault(attribute => attribute.Key == "GitHubClientId" || attribute.Key == "OpenzaTasksGitHubClientId")
            ?.Value;
    }

    public async Task<GitHubTokenValidationResult> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new GitHubTokenValidationResult(false, null, "Token is empty.", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        using var request = CreateRequest(HttpMethod.Get, $"{ApiBaseUrl}/user", token);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var scopes = ParseScopes(response.Headers);
        if (!response.IsSuccessStatusCode)
        {
            return new GitHubTokenValidationResult(false, null, $"GitHub rejected the token ({(int)response.StatusCode}).", scopes);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var username = document.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null;
        var canCreatePrivateIssues = scopes.Contains("repo");
        var canCreatePublicIssues = scopes.Contains("public_repo") || scopes.Contains("repo");
        if (scopes.Count > 0 && !canCreatePrivateIssues && !canCreatePublicIssues)
        {
            return new GitHubTokenValidationResult(false, username, "Token does not include access to create issues.", scopes);
        }

        return new GitHubTokenValidationResult(true, username, null, scopes);
    }

    public async Task<IReadOnlyList<GitHubRepositoryInfo>> GetRepositoriesAsync(string token, CancellationToken cancellationToken = default)
    {
        var repositories = new Dictionary<string, GitHubRepositoryInfo>(StringComparer.OrdinalIgnoreCase);
        await AddRepositoriesAsync(
            repositories,
            token,
            $"{ApiBaseUrl}/user/repos?affiliation=owner,collaborator,organization_member&sort=updated&per_page=100",
            cancellationToken).ConfigureAwait(false);

        foreach (var organization in await GetOrganizationsAsync(token, cancellationToken).ConfigureAwait(false))
        {
            await AddRepositoriesAsync(
                repositories,
                token,
                $"{ApiBaseUrl}/orgs/{Uri.EscapeDataString(organization)}/repos?type=all&sort=updated&per_page=100",
                cancellationToken).ConfigureAwait(false);
        }

        return repositories.Values
            .Where(repository => repository.HasIssues && !repository.IsArchived)
            .OrderBy(repository => repository.Owner, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(repository => repository.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task AddRepositoriesAsync(Dictionary<string, GitHubRepositoryInfo> repositories, string token, string initialUrl, CancellationToken cancellationToken)
    {
        string? url = initialUrl;
        while (!string.IsNullOrWhiteSpace(url))
        {
            using var request = CreateRequest(HttpMethod.Get, url, token);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "load repositories", cancellationToken).ConfigureAwait(false);
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (ReadRepository(item) is not { } repository)
                {
                    continue;
                }

                repositories[repository.FullName] = repository;
            }

            url = NextLink(response.Headers);
        }
    }

    private static GitHubRepositoryInfo? ReadRepository(JsonElement item)
    {
        var fullName = item.TryGetProperty("full_name", out var fullNameElement)
            ? fullNameElement.GetString() ?? string.Empty
            : string.Empty;
        var parts = fullName.Split('/', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        return new GitHubRepositoryInfo(
            parts[0],
            parts[1],
            fullName,
            item.TryGetProperty("private", out var isPrivate) && isPrivate.GetBoolean(),
            item.TryGetProperty("has_issues", out var hasIssues) && hasIssues.GetBoolean(),
            item.TryGetProperty("archived", out var archived) && archived.GetBoolean());
    }

    private async Task<IReadOnlyList<string>> GetOrganizationsAsync(string token, CancellationToken cancellationToken)
    {
        var organizations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await AddOrganizationsAsync(token, $"{ApiBaseUrl}/user/orgs?per_page=100", organizations, nestedOrg: false, cancellationToken).ConfigureAwait(false);
        await AddOrganizationsAsync(token, $"{ApiBaseUrl}/user/memberships/orgs?state=active&per_page=100", organizations, nestedOrg: true, cancellationToken).ConfigureAwait(false);
        return organizations.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async Task AddOrganizationsAsync(string token, string initialUrl, HashSet<string> organizations, bool nestedOrg, CancellationToken cancellationToken)
    {
        string? url = initialUrl;
        while (!string.IsNullOrWhiteSpace(url))
        {
            using var request = CreateRequest(HttpMethod.Get, url, token);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "load organizations", cancellationToken).ConfigureAwait(false);
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var organization = nestedOrg && item.TryGetProperty("organization", out var nested)
                    ? nested
                    : item;
                var login = organization.TryGetProperty("login", out var loginElement)
                    ? loginElement.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(login))
                {
                    organizations.Add(login);
                }
            }

            url = NextLink(response.Headers);
        }
    }

    public async Task<IReadOnlyList<GitHubLabelInfo>> GetLabelsAsync(string token, string owner, string repository, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{ApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/labels?per_page=100", token);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "load labels", cancellationToken).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement
            .EnumerateArray()
            .Select(item => new GitHubLabelInfo(
                item.GetProperty("name").GetString() ?? string.Empty,
                item.TryGetProperty("color", out var color) ? color.GetString() ?? string.Empty : string.Empty))
            .Where(label => !string.IsNullOrWhiteSpace(label.Name))
            .OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<GitHubIssueCreateResult> CreateIssueAsync(string token, GitHubIssueCreateRequest issue, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            title = issue.Title,
            body = issue.Body,
            labels = issue.Labels,
            assignees = issue.Assignees,
        }, JsonOptions);

        using var request = CreateRequest(
            HttpMethod.Post,
            $"{ApiBaseUrl}/repos/{Uri.EscapeDataString(issue.Owner)}/{Uri.EscapeDataString(issue.Repository)}/issues",
            token);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "create issue", cancellationToken).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var labels = root.TryGetProperty("labels", out var labelArray)
            ? labelArray.EnumerateArray()
                .Select(label => label.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList()
            : [];
        var assignees = root.TryGetProperty("assignees", out var assigneeArray)
            ? assigneeArray.EnumerateArray()
                .Select(assignee => assignee.TryGetProperty("login", out var login) ? login.GetString() : null)
                .Where(login => !string.IsNullOrWhiteSpace(login))
                .Select(login => login!)
                .ToList()
            : [];

        return new GitHubIssueCreateResult(
            root.GetProperty("id").GetInt64(),
            root.GetProperty("number").GetInt32(),
            issue.Owner,
            issue.Repository,
            root.GetProperty("title").GetString() ?? issue.Title,
            root.GetProperty("html_url").GetString() ?? string.Empty,
            root.TryGetProperty("state", out var state) ? state.GetString() ?? "open" : "open",
            labels,
            assignees);
    }

    public async Task<GitHubIssueStatusResult> GetIssueStatusAsync(string token, string owner, string repository, int number, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"{ApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/issues/{number}",
            token);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return new GitHubIssueStatusResult(true, false, null);
        }

        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        {
            return new GitHubIssueStatusResult(false, true, null);
        }

        return new GitHubIssueStatusResult(false, false, $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    public async Task<GitHubDeviceCodeResponse> RequestDeviceCodeAsync(string clientId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl);
        AddCommonHeaders(request);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = OAuthScopes,
        });
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "start GitHub sign-in", cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<GitHubDeviceCodeResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> PollForDeviceTokenAsync(string clientId, GitHubDeviceCodeResponse deviceCode, CancellationToken cancellationToken = default)
    {
        var interval = Math.Max(deviceCode.Interval, 5);
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            AddCommonHeaders(request);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["device_code"] = deviceCode.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            });
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var tokenResponse = await ReadJsonAsync<GitHubTokenResponse>(response, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                return tokenResponse.AccessToken;
            }

            interval = tokenResponse.Error == "slow_down" ? interval + 5 : interval;
            if (tokenResponse.Error is "expired_token" or "access_denied")
            {
                throw new InvalidOperationException(tokenResponse.ErrorDescription ?? "GitHub sign-in was cancelled.");
            }

            if (tokenResponse.Error != "authorization_pending" && tokenResponse.Error != "slow_down")
            {
                throw new InvalidOperationException(tokenResponse.ErrorDescription ?? "GitHub sign-in failed.");
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    public static string BuildIssueBody(TaskItem task, ProjectItem? project)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(task.Notes))
        {
            lines.Add(task.Notes.Trim());
            lines.Add(string.Empty);
        }

        lines.Add("Openza task");
        if (!string.IsNullOrWhiteSpace(project?.Name))
        {
            lines.Add($"Project: {project.Name}");
        }

        lines.Add($"Priority: {PriorityName(task.Priority)}");
        if (task.PlannedOn is not null)
        {
            lines.Add($"Planned: {task.PlannedOn:yyyy-MM-dd}");
        }

        if (task.DeadlineOn is not null)
        {
            lines.Add($"Deadline: {task.DeadlineOn:yyyy-MM-dd}");
        }

        if (!string.IsNullOrWhiteSpace(task.SourceUrl))
        {
            lines.Add($"Source: {task.SourceUrl}");
        }

        lines.Add(string.Empty);
        lines.Add("Created from Openza Tasks.");
        return string.Join(Environment.NewLine, lines);
    }

    public static GitHubConnectionSettings ReadSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new GitHubConnectionSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<GitHubConnectionSettings>(json, JsonOptions) ?? new GitHubConnectionSettings();
        }
        catch
        {
            return new GitHubConnectionSettings();
        }
    }

    public static string WriteSettings(GitHubConnectionSettings settings) => JsonSerializer.Serialize(settings, JsonOptions);

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        AddCommonHeaders(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static void AddCommonHeaders(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", GitHubApiVersion);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitHub returned an empty response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException($"Could not {operation}: {(int)response.StatusCode} {response.ReasonPhrase}. {text}");
    }

    private static HashSet<string> ParseScopes(HttpResponseHeaders headers)
    {
        var scopes = headers.TryGetValues("X-OAuth-Scopes", out var values)
            ? string.Join(",", values)
            : string.Empty;
        return scopes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? NextLink(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var link in string.Join(",", values).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = link.Split(';', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && parts.Any(part => string.Equals(part, "rel=\"next\"", StringComparison.OrdinalIgnoreCase)))
            {
                return parts[0].Trim('<', '>');
            }
        }

        return null;
    }

    private static string PriorityName(int priority) => priority switch
    {
        1 => "Urgent",
        2 => "High",
        4 => "Low",
        _ => "Normal",
    };
}
