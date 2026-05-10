using System.Net;
using System.Text;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class SyncProviderTests
{
    [Fact]
    public async Task TodoistProvider_maps_project_labels_due_date_and_completion_endpoint()
    {
        var handler = new FakeHttpMessageHandler(request => request.RequestUri?.PathAndQuery switch
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
                      "due": { "date": "2026-05-12" },
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
        Assert.Equal("todoist_project1", task.ProjectId);
        Assert.Equal("todoist_parent1", task.ParentId);
        Assert.Equal(1, task.Priority);
        Assert.Equal("release", Assert.Single(task.Labels).Name);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Uri.PathAndQuery == "/api/v1/tasks?limit=200");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Uri.PathAndQuery == "/api/v1/tasks?limit=200&cursor=next-page");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/api/v1/tasks/task1/close");
        Assert.All(handler.Requests, request => Assert.Equal("Bearer", request.AuthorizationScheme));
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
            "/v1.0/me/todo/lists/list1/tasks?$top=100" => Json("""
                {
                  "value": [
                    {
                      "id": "task1",
                      "title": "Review package",
                      "body": { "content": "Store readiness" },
                      "importance": "high",
                      "status": "notStarted",
                      "categories": ["Release"],
                      "dueDateTime": { "dateTime": "2026-05-12T00:00:00" },
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
        Assert.Equal("mstodo_list1", task.ProjectId);
        Assert.Equal(1, task.Priority);
        Assert.Equal("Release", Assert.Single(task.Labels).Name);
        var completionRequest = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Patch);
        Assert.Equal("/v1.0/me/todo/lists/list1/tasks/task1", completionRequest.Uri.AbsolutePath);
        Assert.Contains("\"notStarted\"", completionRequest.Content);
    }

    private static HttpResponseMessage Json(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Empty(HttpStatusCode statusCode) => new(statusCode);

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
