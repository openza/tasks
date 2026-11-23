using System.Net;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class SyncProviderTests
{
    [Fact]
    public async Task TodoistProvider_validates_access_with_bearer_token()
    {
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri?.PathAndQuery == "/api/v1/tasks?limit=1"
                ? Json("""{ "results": [], "next_cursor": null }""")
                : Empty(HttpStatusCode.NotFound));
        var provider = new TodoistProvider(new HttpClient(handler), "token");

        await provider.ValidateAccessAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/tasks?limit=1", request.Uri.PathAndQuery);
        Assert.Equal("Bearer", request.AuthorizationScheme);
    }

    [Fact]
    public async Task TodoistProvider_maps_project_labels_date_and_completion_endpoint()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            if (IsTodoistCompletedTasksRequest(request))
            {
                return Json("""{ "items": [], "next_cursor": null }""");
            }

            return request.RequestUri?.PathAndQuery switch
            {
                "/api/v1/tasks?limit=200" => Json("""
                {
                  "results": [
                    {
                      "id": "task1",
                      "content": "Ship Tasks",
                      "description": "WinUI migration",
                      "project_id": "project1",
                      "parent_id": "parent1",
                      "priority": 4,
                      "due": { "date": "2026-05-12", "is_recurring": true, "string": "every 12th" },
                      "labels": ["release"],
                      "added_at": "2026-05-01T10:00:00Z"
                    }
                  ],
                  "next_cursor": "next-page"
                }
                """),
                "/api/v1/tasks?limit=200&cursor=next-page" => Json("""{ "results": [], "next_cursor": null }"""),
                "/api/v1/projects?limit=200" => Json("""
                {
                  "results": [
                    {
                      "id": "project1",
                      "name": "Openza",
                      "color": "blue",
                      "child_order": 7,
                      "is_favorite": true
                    }
                  ],
                  "next_cursor": null
                }
                """),
                "/api/v1/labels?limit=200" => Json("""{ "results": [{ "id": "label1", "name": "release", "color": "green", "order": 2 }], "next_cursor": null }"""),
                "/api/v1/tasks/task1/close" => Empty(HttpStatusCode.OK),
                _ => Empty(HttpStatusCode.NotFound),
            };
        });
        var provider = new TodoistProvider(new HttpClient(handler), "token");

        var snapshot = await provider.FetchSnapshotAsync();
        await provider.CompleteTaskAsync(new PendingCompletion
        {
            Id = "completion1",
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = "task1",
            Completed = true,
        });

        var task = Assert.Single(snapshot.Tasks);
        Assert.Equal("WinUI migration", task.Description);
        Assert.Equal("todoist_project1", task.ProjectId);
        Assert.Equal("todoist_parent1", task.ParentId);
        Assert.Equal(TaskItemStatus.None, task.Status);
        Assert.Equal(1, task.Priority);
        Assert.Equal(new DateOnly(2026, 5, 12), task.PlannedOn);
        Assert.Null(task.PlannedAt);
        Assert.Equal("every 12th", task.RecurrenceRule);
        Assert.Equal("release", Assert.Single(task.Labels).Name);
        using var metadata = JsonDocument.Parse(task.ProviderMetadataJson!);
        var labels = metadata.RootElement.GetProperty("sourceTask").GetProperty("labels").EnumerateArray().Select(label => label.GetString()).ToList();
        Assert.Equal(["release"], labels);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Uri.PathAndQuery == "/api/v1/tasks?limit=200");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Uri.PathAndQuery == "/api/v1/tasks?limit=200&cursor=next-page");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Uri.AbsolutePath == "/api/v1/tasks/completed/by_completion_date");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/api/v1/tasks/task1/close");
        Assert.All(handler.Requests, request => Assert.Equal("Bearer", request.AuthorizationScheme));
    }

    [Fact]
    public async Task TodoistProvider_maps_completed_tasks_from_completion_history()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            if (IsTodoistCompletedTasksRequest(request))
            {
                return Json("""
                    {
                      "items": [
                        {
                          "task_id": "task1",
                          "content": "Repair Ovi cycle",
                          "project_id": "project1",
                          "completed_at": "2026-05-17T10:00:00Z"
                        }
                      ],
                      "next_cursor": null
                    }
                    """);
            }

            return request.RequestUri?.PathAndQuery switch
            {
                "/api/v1/tasks?limit=200" => Json("""{ "results": [], "next_cursor": null }"""),
                "/api/v1/projects?limit=200" => Json("""{ "results": [{ "id": "project1", "name": "Home" }], "next_cursor": null }"""),
                "/api/v1/labels?limit=200" => Json("""{ "results": [], "next_cursor": null }"""),
                _ => Empty(HttpStatusCode.NotFound),
            };
        });
        var provider = new TodoistProvider(new HttpClient(handler), "token");

        var snapshot = await provider.FetchSnapshotAsync();

        Assert.Empty(snapshot.Tasks);
        var task = Assert.Single(snapshot.CompletedTasks);
        Assert.Equal("task1", task.ExternalId);
        Assert.Equal("Repair Ovi cycle", task.Title);
        Assert.Equal("todoist_project1", task.ProjectId);
        Assert.Equal(TaskCompletionState.Completed, task.CompletionState);
        Assert.Equal(new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero), task.CompletedAt);
    }

    [Fact]
    public async Task TodoistProvider_moves_task_to_configured_project()
    {
        var handler = new FakeHttpMessageHandler(request => request.RequestUri?.PathAndQuery switch
        {
            "/api/v1/tasks/task1/move" => Empty(HttpStatusCode.OK),
            _ => Empty(HttpStatusCode.NotFound),
        });
        var provider = new TodoistProvider(new HttpClient(handler), "token");

        await provider.MoveTaskAsync("task1", "processed_project");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/tasks/task1/move", request.Uri.AbsolutePath);
        Assert.Contains("\"project_id\":\"processed_project\"", request.Content);
    }

    [Fact]
    public async Task TodoistProvider_updates_task_due_date()
    {
        var handler = new FakeHttpMessageHandler(request => request.RequestUri?.PathAndQuery switch
        {
            "/api/v1/tasks/task1" => Empty(HttpStatusCode.OK),
            _ => Empty(HttpStatusCode.NotFound),
        });
        var provider = new TodoistProvider(new HttpClient(handler), "token");

        await provider.UpdateTaskDateAsync(new PendingTaskDateUpdate
        {
            TaskId = "local_1",
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = "task1",
            PlannedOn = new DateOnly(2026, 6, 2),
        });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/tasks/task1", request.Uri.AbsolutePath);
        Assert.Contains("\"due_date\":\"2026-06-02\"", request.Content);
    }

    [Fact]
    public async Task TodoistProvider_maps_floating_due_datetime_from_date_field()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            if (IsTodoistCompletedTasksRequest(request))
            {
                return Json("""{ "items": [], "next_cursor": null }""");
            }

            return request.RequestUri?.PathAndQuery switch
            {
                "/api/v1/tasks?limit=200" => Json("""
                {
                  "results": [
                    {
                      "id": "task1",
                      "content": "Evening Chores",
                      "priority": 1,
                      "due": { "date": "2026-05-16T23:00:00", "is_recurring": true, "string": "every day at 23:00" }
                    }
                  ],
                  "next_cursor": null
                }
                """),
                "/api/v1/projects?limit=200" => Json("""{ "results": [], "next_cursor": null }"""),
                "/api/v1/labels?limit=200" => Json("""{ "results": [], "next_cursor": null }"""),
                _ => Empty(HttpStatusCode.NotFound),
            };
        });
        var provider = new TodoistProvider(new HttpClient(handler), "token");

        var task = Assert.Single((await provider.FetchSnapshotAsync()).Tasks);

        Assert.Equal(new DateOnly(2026, 5, 16), task.PlannedOn);
        Assert.NotNull(task.PlannedAt);
        Assert.Equal(new DateTimeOffset(2026, 5, 16, 23, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 5, 16, 23, 0, 0))), task.PlannedAt);
        Assert.Equal("every day at 23:00", task.RecurrenceRule);
    }

    [Fact]
    public async Task MicrosoftToDoProvider_maps_lists_categories_tasks_and_completion_endpoint()
    {
        var handler = new FakeHttpMessageHandler(request => request.RequestUri?.PathAndQuery switch
        {
            "/v1.0/me/todo/lists" => Json("""
                {
                  "value": [
                    { "id": "list1", "displayName": "Inbox", "wellknownListName": "defaultList", "isOwner": true }
                  ]
                }
                """),
            "/v1.0/me/outlook/masterCategories" => Json("""
                {
                  "value": [
                    { "displayName": "Release", "color": "preset5" }
                  ]
                }
                """),
            "/v1.0/me/todo/lists/list1/tasks?$top=100&$expand=checklistItems" => Json("""
                {
                  "value": [
                    {
                      "id": "task1",
                      "checklistItems": [],
                      "title": "Review package",
                      "body": { "content": "Store readiness" },
                      "importance": "high",
                      "status": "notStarted",
                      "categories": ["Release"],
                      "dueDateTime": { "dateTime": "2026-05-12T00:00:00" },
                      "recurrence": { "pattern": { "type": "weekly" } },
                      "createdDateTime": "2026-05-01T10:00:00Z"
                    }
                  ]
                }
                """),
            "/v1.0/me/todo/lists/list1/tasks/task1" => Empty(HttpStatusCode.NoContent),
            _ => Empty(HttpStatusCode.NotFound),
        });
        var provider = new MicrosoftToDoProvider(new HttpClient(handler), "token");

        var snapshot = await provider.FetchSnapshotAsync();
        await provider.CompleteTaskAsync(new PendingCompletion
        {
            Id = "completion1",
            Provider = IntegrationIds.MicrosoftToDo,
            ProviderTaskId = "list1|task1",
            Completed = false,
        });

        var project = Assert.Single(snapshot.Projects);
        Assert.Equal("mstodo_list1", project.Id);
        var task = Assert.Single(snapshot.Tasks);
        Assert.Equal("Store readiness", task.Description);
        Assert.Equal("mstodo_list1", task.ProjectId);
        Assert.Equal(TaskItemStatus.None, task.Status);
        Assert.Equal(1, task.Priority);
        Assert.Equal("weekly", task.RecurrenceRule);
        Assert.Equal("Release", Assert.Single(task.Labels).Name);
        using var metadata = JsonDocument.Parse(task.ProviderMetadataJson!);
        var category = Assert.Single(metadata.RootElement.GetProperty("msToDo").GetProperty("categories").EnumerateArray());
        Assert.Equal("Release", category.GetProperty("name").GetString());
        Assert.Equal("#0078D4", category.GetProperty("color").GetString());
        var completionRequest = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Patch);
        Assert.Equal("/v1.0/me/todo/lists/list1/tasks/task1", completionRequest.Uri.AbsolutePath);
        Assert.Contains("\"notStarted\"", completionRequest.Content);
    }

    [Fact]
    public async Task MicrosoftToDoProvider_follows_list_and_category_pages_and_preserves_category_colors()
    {
        var handler = new FakeHttpMessageHandler(request => request.RequestUri?.PathAndQuery switch
        {
            "/v1.0/me/todo/lists" => Json("""{"value":[{"id":"first","displayName":"First"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/todo/lists?$skip=1"}"""),
            "/v1.0/me/todo/lists?$skip=1" => Json("""{"value":[{"id":"second","displayName":"Second"}]}"""),
            "/v1.0/me/outlook/masterCategories" => Json("""{"value":[{"displayName":"Other","color":"preset0"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/outlook/masterCategories?$skip=1"}"""),
            "/v1.0/me/outlook/masterCategories?$skip=1" => Json("""{"value":[{"displayName":"Release","color":"preset3"}]}"""),
            "/v1.0/me/todo/lists/first/tasks?$top=100&$expand=checklistItems" => Json("""{"value":[]}"""),
            "/v1.0/me/todo/lists/second/tasks?$top=100&$expand=checklistItems" => Json("""{"value":[{"id":"task","title":"Paged","categories":["Release","Unknown"],"checklistItems":[{"displayName":"First","isChecked":true}]}]}"""),
            _ => Empty(HttpStatusCode.NotFound),
        });
        var snapshot = await new MicrosoftToDoProvider(new HttpClient(handler), "token").FetchSnapshotAsync();
        Assert.Equal(2, snapshot.Projects.Count);
        Assert.Equal(2, snapshot.Labels.Count);
        var task = Assert.Single(snapshot.Tasks);
        var source = TaskSyncEngine.ToProviderSourceItem(task, "mstodo_default", new Dictionary<string, string>());
        using var metadata = JsonDocument.Parse(source.SnapshotJson!);
        var categories = metadata.RootElement.GetProperty("msToDo").GetProperty("categories").EnumerateArray().ToArray();
        Assert.Equal("Release", categories[0].GetProperty("name").GetString());
        Assert.Equal("#32CD32", categories[0].GetProperty("color").GetString());
        Assert.Equal("#0078D4", categories[1].GetProperty("color").GetString());
        Assert.Equal("First", Assert.Single(metadata.RootElement.GetProperty("msToDo").GetProperty("checklistItems").EnumerateArray()).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task MicrosoftToDoProvider_preserves_task_categories_when_catalog_is_unavailable()
    {
        var handler = new FakeHttpMessageHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/v1.0/me/todo/lists" => Json("""{"value":[{"id":"list"}]}"""),
            "/v1.0/me/todo/lists/list/tasks" => Json("""{"value":[{"id":"task","categories":["Release"]}]}"""),
            _ => Empty(HttpStatusCode.Forbidden),
        });
        var snapshot = await new MicrosoftToDoProvider(new HttpClient(handler), "token").FetchSnapshotAsync();
        Assert.Empty(snapshot.Labels);
        var label = Assert.Single(Assert.Single(snapshot.Tasks).Labels);
        Assert.Equal("Release", label.Name);
        Assert.Equal("#0078D4", label.Color);
    }

    [Theory]
    [InlineData("lists", false)]
    [InlineData("categories", false)]
    [InlineData("tasks", false)]
    [InlineData("lists", true)]
    [InlineData("categories", true)]
    [InlineData("tasks", true)]
    public async Task MicrosoftToDoProvider_rejects_repeated_pages_without_publishing_a_partial_snapshot(string collection, bool cycle)
    {
        var repeatedRequests = 0;
        var path = collection switch
        {
            "lists" => "/v1.0/me/todo/lists",
            "categories" => "/v1.0/me/outlook/masterCategories",
            _ => "/v1.0/me/todo/lists/list/tasks?$top=100&$expand=checklistItems",
        };
        var secondPath = path + (path.Contains('?') ? "&page=2" : "?page=2");
        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.PathAndQuery == path || request.RequestUri.PathAndQuery == secondPath)
            {
                if (++repeatedRequests > 4) throw new InvalidOperationException("Test stopped unbounded paging.");
                return Json(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["value"] = Array.Empty<object>(),
                    ["@odata.nextLink"] = "https://graph.microsoft.com" + (cycle && repeatedRequests == 1 ? secondPath : path),
                }));
            }
            return request.RequestUri.AbsolutePath == "/v1.0/me/todo/lists"
                ? Json("""{"value":[{"id":"list"}]}""") : Json("""{"value":[]}""");
        });

        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            new MicrosoftToDoProvider(new HttpClient(handler), "token").FetchSnapshotAsync());

        Assert.Equal("Microsoft Graph returned a repeated collection page.", error.Message);
        Assert.InRange(repeatedRequests, 1, 2);
    }

    private static HttpResponseMessage Json(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Empty(HttpStatusCode statusCode) => new(statusCode);

    private static bool IsTodoistCompletedTasksRequest(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get &&
        request.RequestUri?.AbsolutePath == "/api/v1/tasks/completed/by_completion_date";

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri ?? new Uri("about:blank"),
                request.Headers.Authorization?.Scheme,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responder(request);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? AuthorizationScheme, string Content);
}
