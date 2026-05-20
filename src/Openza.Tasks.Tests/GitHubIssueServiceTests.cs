using System.Net;
using System.Text;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Tests;

public sealed class GitHubIssueServiceTests
{
    [Fact]
    public async Task CreateIssue_posts_expected_rest_payload_and_maps_result()
    {
        HttpRequestMessage? captured = null;
        string? payload = null;
        var service = new GitHubIssueService(new HttpClient(new StubHandler(async request =>
        {
            captured = request;
            payload = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    """
                    {
                      "id": 12345,
                      "number": 42,
                      "title": "Fix import",
                      "html_url": "https://github.com/openza/tasks/issues/42",
                      "state": "open",
                      "labels": [{"name": "bug"}],
                      "assignees": [{"login": "deependra"}]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        })));

        var result = await service.CreateIssueAsync(
            "token",
            new GitHubIssueCreateRequest(
                "openza",
                "tasks",
                "Fix import",
                "Body",
                ["bug"],
                ["deependra"]));

        Assert.Equal(HttpMethod.Post, captured?.Method);
        Assert.Equal("https://api.github.com/repos/openza/tasks/issues", captured?.RequestUri?.ToString());
        Assert.Contains("\"title\":\"Fix import\"", payload);
        Assert.Contains("\"labels\":[\"bug\"]", payload);
        Assert.Equal("openza/tasks#42", result.DisplayName);
        Assert.Equal("https://github.com/openza/tasks/issues/42", result.Url);
    }

    [Fact]
    public void BuildIssueBody_includes_task_context_and_source_url()
    {
        var body = GitHubIssueService.BuildIssueBody(
            new TaskItem
            {
                Title = "Todoist task",
                Notes = "Need a GitHub issue",
                Priority = 1,
                SourceUrl = "https://app.todoist.com/app/task/example-123",
                PlannedOn = new DateOnly(2026, 5, 19),
            },
            new ProjectItem { Name = "Sync" });

        Assert.Contains("Need a GitHub issue", body);
        Assert.Contains("Project: Sync", body);
        Assert.Contains("Priority: Urgent", body);
        Assert.Contains("Source: https://app.todoist.com/app/task/example-123", body);
        Assert.Contains("Created from Openza Tasks.", body);
    }

    [Fact]
    public async Task GetIssueStatus_marks_deleted_issue_as_not_found()
    {
        var service = new GitHubIssueService(new HttpClient(new StubHandler(request =>
        {
            Assert.Equal("https://api.github.com/repos/openza/tasks/issues/42", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        })));

        var result = await service.GetIssueStatusAsync("token", "openza", "tasks", 42);

        Assert.False(result.Exists);
        Assert.True(result.Unavailable);
    }

    [Fact]
    public async Task GetIssueStatus_treats_gone_issue_as_deleted()
    {
        var service = new GitHubIssueService(new HttpClient(new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone)))));

        var result = await service.GetIssueStatusAsync("token", "openza", "tasks", 42);

        Assert.False(result.Exists);
        Assert.True(result.Unavailable);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
