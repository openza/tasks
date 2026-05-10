using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Sync;

public sealed class TodoistProvider(HttpClient httpClient, string accessToken) : ISyncProvider
{
    private const string BaseUrl = "https://api.todoist.com/api/v1";
    private const int PageSize = 200;
    public string IntegrationId => IntegrationIds.Todoist;

    public async Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var tasksJson = await SendPagedJsonAsync("/tasks", cancellationToken).ConfigureAwait(false);
        using var projectsJson = await SendPagedJsonAsync("/projects", cancellationToken).ConfigureAwait(false);
        using var labelsJson = await SendPagedJsonAsync("/labels", cancellationToken).ConfigureAwait(false);

        var projects = MapProjects(projectsJson.RootElement);
        var labels = MapLabels(labelsJson.RootElement);
        var projectNames = projects.Where(p => p.ExternalId is not null).ToDictionary(p => p.ExternalId!, p => p.Name, StringComparer.Ordinal);
        var tasks = MapTasks(tasksJson.RootElement, projectNames);

        return new ProviderSnapshot(tasks, projects, labels);
    }

    public async Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default)
    {
        var suffix = completion.Completed ? "close" : "reopen";
        using var request = CreateRequest(HttpMethod.Post, $"/tasks/{completion.ProviderTaskId}/{suffix}");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{BaseUrl}{path}");
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

    private async Task<JsonDocument> SendPagedJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();

        string? cursor = null;
        do
        {
            using var request = CreateRequest(HttpMethod.Get, BuildPagedPath(path, cursor));
            using var page = await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
            cursor = CopyPageResults(page.RootElement, writer);
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string BuildPagedPath(string path, string? cursor)
    {
        var query = $"limit={PageSize.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        return $"{path}?{query}";
    }

    private static string? CopyPageResults(JsonElement root, Utf8JsonWriter writer)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                item.WriteTo(writer);
            }

            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                item.WriteTo(writer);
            }
        }

        var nextCursor = GetString(root, "next_cursor");
        return string.IsNullOrWhiteSpace(nextCursor) ? null : nextCursor;
    }

    private static IReadOnlyList<ProjectItem> MapProjects(JsonElement root)
    {
        return root.EnumerateArray().Select(project => new ProjectItem
        {
            Id = $"todoist_{GetString(project, "id")}",
            ExternalId = GetString(project, "id"),
            IntegrationId = IntegrationIds.Todoist,
            Name = GetString(project, "name") ?? "Todoist project",
            Color = TodoistColorToHex(GetString(project, "color")),
            ParentId = PrefixOrNull("todoist_", GetString(project, "parent_id")),
            SortOrder = GetInt(project, "order", GetInt(project, "child_order", GetInt(project, "default_order"))),
            IsFavorite = GetBool(project, "is_favorite"),
            IsArchived = GetBool(project, "is_archived"),
            CreatedAt = DateTimeOffset.UtcNow,
            ProviderMetadataJson = project.GetRawText(),
        }).ToList();
    }

    private static IReadOnlyList<LabelItem> MapLabels(JsonElement root)
    {
        return root.EnumerateArray().Select(label =>
        {
            var name = GetString(label, "name") ?? "label";
            return new LabelItem
            {
                Id = $"todoist_label_{name}",
                ExternalId = GetString(label, "id"),
                IntegrationId = IntegrationIds.Todoist,
                Name = name,
                Color = TodoistColorToHex(GetString(label, "color")),
                SortOrder = GetInt(label, "order"),
                CreatedAt = DateTimeOffset.UtcNow,
                ProviderMetadataJson = label.GetRawText(),
            };
        }).ToList();
    }

    private static IReadOnlyList<TaskItem> MapTasks(JsonElement root, IReadOnlyDictionary<string, string> projectNames)
    {
        return root.EnumerateArray().Select(task =>
        {
            var id = GetString(task, "id") ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var dueDate = ParseTodoistDue(task);
            return new TaskItem
            {
                Id = $"todoist_{id}",
                ExternalId = id,
                IntegrationId = IntegrationIds.Todoist,
                Title = GetString(task, "content") ?? string.Empty,
                Description = GetString(task, "description"),
                ProjectId = PrefixOrNull("todoist_", GetString(task, "project_id")),
                ParentId = PrefixOrNull("todoist_", GetString(task, "parent_id")),
                Priority = 5 - Math.Clamp(GetInt(task, "priority", defaultValue: 1), 1, 4),
                Status = GetBool(task, "is_completed") || GetBool(task, "checked") ? TaskItemStatus.Completed : TaskItemStatus.None,
                DueDate = dueDate,
                DueTime = dueDate is { Hour: > 0 } ? dueDate.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : null,
                CreatedAt = ParseDate(GetString(task, "created_at")) ?? ParseDate(GetString(task, "added_at")) ?? DateTimeOffset.UtcNow,
                CompletedAt = ParseDate(GetString(task, "completed_at")),
                Labels = MapTaskLabels(task),
                ProviderMetadataJson = BuildTodoistMetadata(task, projectNames),
            };
        }).ToList();
    }

    private static IReadOnlyList<LabelItem> MapTaskLabels(JsonElement task)
    {
        if (!task.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return labels.EnumerateArray().Select(label =>
        {
            var name = label.GetString() ?? string.Empty;
            return new LabelItem
            {
                Id = $"todoist_label_{name}",
                ExternalId = name,
                IntegrationId = IntegrationIds.Todoist,
                Name = name,
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }).ToList();
    }

    private static string BuildTodoistMetadata(JsonElement task, IReadOnlyDictionary<string, string> projectNames)
    {
        var projectId = GetString(task, "project_id");
        var projectName = projectId is not null && projectNames.TryGetValue(projectId, out var name) ? name : null;
        return JsonSerializer.Serialize(new
        {
            todoist = new { id = GetString(task, "id"), synced_at = DateTimeOffset.UtcNow },
            sourceTask = new { projectId, projectName, parentId = GetString(task, "parent_id") },
        });
    }

    private static DateTimeOffset? ParseTodoistDue(JsonElement task)
    {
        if (!task.TryGetProperty("due", out var due) || due.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return ParseDate(GetString(due, "datetime")) ?? ParseDate(GetString(due, "date"));
    }

    private static string? PrefixOrNull(string prefix, string? value) => string.IsNullOrWhiteSpace(value) ? null : $"{prefix}{value}";

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;

    private static int GetInt(JsonElement element, string property, int defaultValue = 0) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : defaultValue;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private static string TodoistColorToHex(string? colorName) => colorName switch
    {
        "berry_red" => "#b8255f",
        "red" => "#db4035",
        "orange" => "#ff9933",
        "yellow" => "#fad000",
        "olive_green" => "#afb83b",
        "lime_green" => "#7ecc49",
        "green" => "#299438",
        "mint_green" => "#6accbc",
        "teal" => "#158fad",
        "sky_blue" => "#14aaf5",
        "light_blue" => "#96c3eb",
        "blue" => "#4073ff",
        "grape" => "#884dff",
        "violet" => "#af38eb",
        "lavender" => "#eb96eb",
        "magenta" => "#e05194",
        "salmon" => "#ff8d85",
        "charcoal" => "#808080",
        "grey" => "#b8b8b8",
        "taupe" => "#ccac93",
        _ => "#808080",
    };
}
