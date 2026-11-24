using System.Globalization;
using System.Net;
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
        var expansion = new ChecklistExpansion();
        foreach (var list in lists)
        {
            if (list.ExternalId is not null)
            {
                tasks.AddRange(await FetchTasksAsync(list.ExternalId, categories, expansion, cancellationToken).ConfigureAwait(false));
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
        var lists = await FetchCollectionAsync("/me/todo/lists", cancellationToken).ConfigureAwait(false);
        return lists.Select(list =>
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
            var categories = await FetchCollectionAsync("/me/outlook/masterCategories", cancellationToken).ConfigureAwait(false);
            return categories
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
        catch (Exception exception) when (exception is not (OperationCanceledException or MicrosoftPagingException))
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<JsonElement>> FetchCollectionAsync(string path, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        string? nextPath = path;
        var visited = new HashSet<string>(StringComparer.Ordinal) { path };
        while (nextPath is not null)
        {
            using var request = CreateRequest(HttpMethod.Get, nextPath);
            using var document = await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("value", out var values))
                items.AddRange(values.EnumerateArray().Select(value => value.Clone()));
            nextPath = ReadNextPagePath(document.RootElement, visited);
        }
        return items;
    }

    private sealed class ChecklistExpansion
    {
        public bool Enabled { get; set; } = true;
    }

    private async Task<IReadOnlyList<TaskItem>> FetchTasksAsync(string listId, IReadOnlyDictionary<string, LabelItem> categories,
        ChecklistExpansion expansion, CancellationToken cancellationToken)
    {
        var tasks = new List<TaskItem>();
        string? nextPath = $"/me/todo/lists/{listId}/tasks?$top=100&$expand=checklistItems";
        var visited = new HashSet<string>(StringComparer.Ordinal) { nextPath };
        while (nextPath is not null)
        {
            using var document = await FetchTaskPageAsync(nextPath, expansion, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var task in value.EnumerateArray())
                {
                    List<JsonElement>? checklist = null;
                    if (expansion.Enabled && task.TryGetProperty("checklistItems", out var expanded) && expanded.ValueKind == JsonValueKind.Array)
                    {
                        checklist = expanded.EnumerateArray().Select(item => item.Clone()).ToList();
                        if (task.TryGetProperty("checklistItems@odata.nextLink", out var nextChecklist))
                            checklist.AddRange(await FetchChecklistAsync(nextChecklist.GetString()!, cancellationToken).ConfigureAwait(false));
                    }
                    tasks.Add(MapTask(task, listId, categories, providerConnectionId, checklist));
                }
            }

            nextPath = ReadNextPagePath(document.RootElement, visited);
        }

        return tasks;
    }

    private async Task<JsonDocument> FetchTaskPageAsync(string path, ChecklistExpansion expansion, CancellationToken cancellationToken)
    {
        var withoutExpansion = RemoveChecklistExpansion(path);
        if (!expansion.Enabled) path = withoutExpansion;
        using var request = CreateRequest(HttpMethod.Get, path);
        try { return await SendJsonAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException exception) when (path != withoutExpansion &&
            exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotImplemented)
        {
            expansion.Enabled = false;
            using var fallback = CreateRequest(HttpMethod.Get, withoutExpansion);
            return await SendJsonAsync(fallback, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string RemoveChecklistExpansion(string path)
    {
        var queryStart = path.IndexOf('?');
        if (queryStart < 0) return path;
        var query = string.Join("&", path[(queryStart + 1)..].Split('&').Where(parameter =>
            !Uri.UnescapeDataString(parameter.Split('=', 2)[0]).Equals("$expand", StringComparison.OrdinalIgnoreCase)));
        return path[..queryStart] + (query.Length == 0 ? string.Empty : $"?{query}");
    }

    private async Task<IReadOnlyList<JsonElement>> FetchChecklistAsync(string initialPath, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        string? path = initialPath.Replace(BaseUrl, string.Empty, StringComparison.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { path };
        while (path is not null)
        {
            using var request = CreateRequest(HttpMethod.Get, path);
            using var document = await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
            items.AddRange(document.RootElement.GetProperty("value").EnumerateArray().Select(item => item.Clone()));
            path = ReadNextPagePath(document.RootElement, visited);
        }
        return items;
    }

    private static string? ReadNextPagePath(JsonElement page, HashSet<string> visited)
    {
        var path = page.TryGetProperty("@odata.nextLink", out var next)
            ? next.GetString()?.Replace(BaseUrl, string.Empty, StringComparison.Ordinal) : null;
        if (path is not null && !visited.Add(path))
            throw new MicrosoftPagingException("Microsoft Graph returned a repeated collection page.");
        return path;
    }

    private sealed class MicrosoftPagingException(string message) : InvalidOperationException(message);

    private static TaskItem MapTask(JsonElement task, string listId, IReadOnlyDictionary<string, LabelItem> categories, string providerConnectionId,
        IReadOnlyList<JsonElement>? checklist)
    {
        var id = GetString(task, "id") ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var due = ParseGraphDate(task, "dueDateTime");
        var status = GetString(task, "status");
        var labels = MapCategories(task, categories, providerConnectionId);
        var metadata = new Dictionary<string, object> { ["id"] = id, ["listId"] = listId, ["synced_at"] = DateTimeOffset.UtcNow };
        metadata["categories"] = labels.Select(label => new { name = label.Name, color = label.Color });
        string? recurrenceRule = null;
        if (task.TryGetProperty("recurrence", out var recurrence))
        {
            if (HasCompleteRecurrence(recurrence))
            {
                recurrenceRule = JsonSerializer.Serialize(recurrence);
                metadata["recurrenceObserved"] = true;
            }
            else if (recurrence.ValueKind == JsonValueKind.Null)
            {
                metadata["recurrenceObserved"] = true;
            }
        }
        if (task.TryGetProperty("isReminderOn", out var reminderOn))
            metadata["reminder"] = new { isReminderOn = reminderOn.Clone(),
                reminderDateTime = task.TryGetProperty("reminderDateTime", out var reminderDate) ? reminderDate.Clone() : (JsonElement?)null };
        if (checklist is not null) metadata["checklistItems"] = checklist;
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
            RecurrenceRule = recurrenceRule,
            CreatedAt = ParseDate(GetString(task, "createdDateTime")) ?? DateTimeOffset.UtcNow,
            CompletedAt = ParseGraphDate(task, "completedDateTime"),
            Labels = labels,
            ProviderMetadataJson = JsonSerializer.Serialize(new
            {
                msToDo = metadata,
            }),
        };
    }

    private static bool HasCompleteRecurrence(JsonElement recurrence)
    {
        if (recurrence.ValueKind != JsonValueKind.Object ||
            !recurrence.TryGetProperty("pattern", out var pattern) || pattern.ValueKind != JsonValueKind.Object ||
            !recurrence.TryGetProperty("range", out var range) || range.ValueKind != JsonValueKind.Object ||
            !HasNumber(pattern, "interval", 1, int.MaxValue) || !HasDate(range, "startDate"))
            return false;

        var hasDays = pattern.TryGetProperty("daysOfWeek", out var days) && days.ValueKind == JsonValueKind.Array &&
            days.GetArrayLength() > 0 && days.EnumerateArray().All(day => day.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(day.GetString()));
        var validPattern = GetString(pattern, "type") switch
        {
            "daily" => true,
            "weekly" => hasDays,
            "absoluteMonthly" => HasNumber(pattern, "dayOfMonth", 1, 31),
            "relativeMonthly" => hasDays,
            "absoluteYearly" => HasNumber(pattern, "dayOfMonth", 1, 31) && HasNumber(pattern, "month", 1, 12),
            "relativeYearly" => hasDays && HasNumber(pattern, "month", 1, 12),
            _ => false,
        };
        return validPattern && GetString(range, "type") switch
        {
            "noEnd" => true,
            "endDate" => HasDate(range, "endDate"),
            "numbered" => HasNumber(range, "numberOfOccurrences", 1, int.MaxValue),
            _ => false,
        };
    }

    private static bool HasNumber(JsonElement value, string name, int min, int max) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number &&
        field.TryGetInt32(out var number) && number >= min && number <= max;

    private static bool HasDate(JsonElement value, string name) =>
        DateOnly.TryParseExact(GetString(value, name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

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
