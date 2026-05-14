using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Sync;

public sealed class MicrosoftToDoProvider(HttpClient httpClient, string accessToken, string providerConnectionId = "mstodo_default") : ISyncProvider
{
    private const string BaseUrl = "https://graph.microsoft.com/v1.0";
    public string IntegrationId => IntegrationIds.MicrosoftToDo;
    public string ProviderConnectionId => providerConnectionId;

    public async Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var lists = await FetchListsAsync(cancellationToken).ConfigureAwait(false);
        var categoryLabels = await FetchCategoriesAsync(cancellationToken).ConfigureAwait(false);
        var categories = categoryLabels
            .GroupBy(label => label.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var tasks = new List<TaskItem>();
        foreach (var list in lists)
        {
            if (list.ExternalId is not null)
            {
                tasks.AddRange(await FetchTasksAsync(list.ExternalId, categories, cancellationToken).ConfigureAwait(false));
            }
        }

        return new ProviderSnapshot(tasks, lists, categoryLabels);
    }

    public async Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default)
    {
        var (listId, taskId) = ExtractProviderIds(completion);
        var status = completion.Completed ? "completed" : "notStarted";
        using var request = CreateRequest(HttpMethod.Patch, $"/me/todo/lists/{listId}/tasks/{taskId}");
        request.Content = JsonContent.Create(new { status });
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<IReadOnlyList<ProjectItem>> FetchListsAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "/me/todo/lists");
        using var document = await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("value", out var value))
        {
            return [];
        }

        return value.EnumerateArray().Select(list =>
        {
            var id = GetString(list, "id") ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var wellKnown = GetString(list, "wellknownListName");
            return new ProjectItem
            {
                Id = $"mstodo_{id}",
                ExternalId = id,
                IntegrationId = IntegrationIds.MicrosoftToDo,
                ProviderConnectionId = providerConnectionId,
                Name = GetString(list, "displayName") ?? "Microsoft To Do",
                Color = wellKnown == "flaggedEmails" ? "#ef4444" : "#3b82f6",
                IsFavorite = GetBool(list, "isOwner"),
                ProviderMetadataJson = list.GetRawText(),
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }).ToList();
    }

    private async Task<IReadOnlyList<LabelItem>> FetchCategoriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/me/outlook/masterCategories");
            using var document = await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("value", out var value))
            {
                return [];
            }

            return value.EnumerateArray()
                .Select(category =>
                {
                    var name = GetString(category, "displayName") ?? "Category";
                    return new LabelItem
                    {
                        Id = BuildCategoryId(name),
                        ExternalId = name,
                        IntegrationId = IntegrationIds.MicrosoftToDo,
                        ProviderConnectionId = providerConnectionId,
                        Name = name,
                        Color = OutlookCategoryColorToHex(GetString(category, "color")),
                        ProviderMetadataJson = category.GetRawText(),
                        CreatedAt = DateTimeOffset.UtcNow,
                    };
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<TaskItem>> FetchTasksAsync(string listId, IReadOnlyDictionary<string, LabelItem> categories, CancellationToken cancellationToken)
    {
        var tasks = new List<TaskItem>();
        string? nextPath = $"/me/todo/lists/{listId}/tasks?$top=100";
        while (nextPath is not null)
        {
            using var request = CreateRequest(HttpMethod.Get, nextPath);
            using var document = await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("value", out var value))
            {
                tasks.AddRange(value.EnumerateArray().Select(task => MapTask(task, listId, categories, providerConnectionId)));
            }

            nextPath = document.RootElement.TryGetProperty("@odata.nextLink", out var next)
                ? next.GetString()?.Replace(BaseUrl, string.Empty, StringComparison.Ordinal)
                : null;
        }

        return tasks;
    }

    private static TaskItem MapTask(JsonElement task, string listId, IReadOnlyDictionary<string, LabelItem> categories, string providerConnectionId)
    {
        var id = GetString(task, "id") ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var due = ParseGraphDate(task, "dueDateTime");
        var status = GetString(task, "status");
        return new TaskItem
        {
            Id = $"mstodo_{id}",
            ExternalId = id,
            IntegrationId = IntegrationIds.MicrosoftToDo,
            ProviderConnectionId = providerConnectionId,
            Title = GetString(task, "title") ?? string.Empty,
            Description = TryGetNestedString(task, "body", "content"),
            ProjectId = $"mstodo_{listId}",
            Priority = GetString(task, "importance") switch
            {
                "high" => 1,
                "low" => 4,
                _ => 2,
            },
            Status = status switch
            {
                "completed" => TaskItemStatus.Completed,
                _ => TaskItemStatus.None,
            },
            DeadlineOn = TaskDateValues.FromDateTimeOffset(due),
            DeadlineAt = HasSpecificTime(due) ? due : null,
            CreatedAt = ParseDate(GetString(task, "createdDateTime")) ?? DateTimeOffset.UtcNow,
            CompletedAt = ParseGraphDate(task, "completedDateTime"),
            Labels = MapCategories(task, categories, providerConnectionId),
            ProviderMetadataJson = JsonSerializer.Serialize(new
            {
                msToDo = new { id, listId, synced_at = DateTimeOffset.UtcNow },
            }),
        };
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string pathOrUrl)
    {
        var uri = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : $"{BaseUrl}{pathOrUrl}";
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static (string ListId, string TaskId) ExtractProviderIds(PendingCompletion completion)
    {
        if (completion.ProviderTaskId.Contains('|', StringComparison.Ordinal))
        {
            var parts = completion.ProviderTaskId.Split('|', 2);
            return (parts[0], parts[1]);
        }

        return ("tasks", completion.ProviderTaskId);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? TryGetNestedString(JsonElement element, string property, string nestedProperty) =>
        element.TryGetProperty(property, out var value) ? GetString(value, nestedProperty) : null;

    private static DateTimeOffset? ParseGraphDate(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return ParseDate(GetString(value, "dateTime"));
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private static bool HasSpecificTime(DateTimeOffset? value) =>
        value is not null && (value.Value.Hour != 0 || value.Value.Minute != 0 || value.Value.Second != 0);

    private static IReadOnlyList<LabelItem> MapCategories(JsonElement task, IReadOnlyDictionary<string, LabelItem> categories, string providerConnectionId)
    {
        if (!task.TryGetProperty("categories", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Select(category => category.GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => categories.TryGetValue(name!, out var label)
                ? label
                : new LabelItem
                {
                    Id = BuildCategoryId(name!),
                    ExternalId = name,
                    IntegrationId = IntegrationIds.MicrosoftToDo,
                    ProviderConnectionId = providerConnectionId,
                    Name = name!,
                    Color = "#0078D4",
                    CreatedAt = DateTimeOffset.UtcNow,
                })
            .ToList();
    }

    private static string BuildCategoryId(string name) =>
        $"mstodo_category_{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(name)).ToLowerInvariant()}";

    private static string OutlookCategoryColorToHex(string? presetColor) => presetColor switch
    {
        "preset0" => "#FF1A1A",
        "preset1" => "#FF8C00",
        "preset2" => "#F4C430",
        "preset3" => "#32CD32",
        "preset4" => "#00CED1",
        "preset5" => "#0078D4",
        "preset6" => "#8A2BE2",
        "preset7" => "#FF69B4",
        "preset8" => "#B8860B",
        "preset9" => "#4682B4",
        "preset10" => "#DC143C",
        "preset11" => "#FF4500",
        "preset12" => "#DAA520",
        "preset13" => "#228B22",
        "preset14" => "#008B8B",
        "preset15" => "#0000CD",
        "preset16" => "#4B0082",
        "preset17" => "#DC143C",
        "preset18" => "#556B2F",
        "preset19" => "#2F4F4F",
        "preset20" => "#8B0000",
        "preset21" => "#FF6347",
        "preset22" => "#BDB76B",
        "preset23" => "#006400",
        "preset24" => "#5F9EA0",
        _ => "#0078D4",
    };
}
