using System.Net;
using System.Text;
using Openza.Tasks.Application.Sync;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class MicrosoftToDoImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-microsoft-import-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Imports_every_page_as_editable_local_copies_and_does_not_overwrite_on_retry()
    {
        var store = await CreateStoreAsync();
        await store.UpsertProjectAsync(new() { Id = "existing-local-project", Name = "Weekend" });
        using var http = new HttpClient(new GraphHandler());
        var service = new MicrosoftToDoImportService(store, new Resolver(new MicrosoftToDoProvider(http, "example-token")));

        Assert.Equal(new(2, 2), await service.ImportAsync(SpaceIds.Default));
        var projects = await store.GetProjectsAsync(SpaceIds.Default);
        var project = Assert.Single(projects, project => project.Name == "Weekend" && project.Id != "existing-local-project");
        Assert.Contains(projects, project => project.Name == "Empty list");
        Assert.All(projects, project => Assert.Equal(IntegrationIds.Local, project.IntegrationId));
        var tasks = await store.GetTasksAsync(new() { Kind = TaskListKind.All });
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, task =>
        {
            Assert.False(task.IsProviderTask);
            Assert.False(task.HasProviderSource);
            Assert.Null(task.ExternalId);
            Assert.Equal(project.Id, task.ProjectId);
        });
        var open = Assert.Single(tasks, task => task.IsOpen);
        Assert.Equal("Pack a picnic", open.Title);
        Assert.Equal("Bring a blanket", open.Notes);
        Assert.Equal(new DateOnly(2026, 12, 5), open.DeadlineOn);
        Assert.Equal(1, open.Priority);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero), Assert.Single(tasks, task => task.IsCompleted).CompletedAt);

        var taskService = new TaskApplicationService(store);
        await taskService.UpdateTaskAsync(new() { TaskId = open.Id, Title = OptionalValue<string?>.Set("Take a hike") });
        await taskService.SetCompletedAsync(open.Id, completed: true);
        await store.UpsertProjectAsync(project with { Name = "Renamed" });
        Assert.Equal(new(0, 0), await service.ImportAsync(SpaceIds.Default));
        Assert.Equal("Take a hike", (await store.GetTaskAsync(open.Id))?.Title);
        Assert.True((await store.GetTaskAsync(open.Id))?.IsCompleted);
        Assert.Contains(await store.GetProjectsAsync(SpaceIds.Default), item => item.Id == project.Id && item.Name == "Renamed");
        Assert.Empty(await store.GetPendingCompletionsAsync(IntegrationIds.MicrosoftToDo));
    }

    [Fact]
    public async Task Graph_failure_on_a_later_page_does_not_import_any_partial_data()
    {
        var store = await CreateStoreAsync();
        using var http = new HttpClient(new GraphHandler(failSecondTaskPage: true));
        var service = new MicrosoftToDoImportService(store, new Resolver(new MicrosoftToDoProvider(http, "example-token")));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.ImportAsync(SpaceIds.Default));

        Assert.Empty(await store.GetProjectsAsync(SpaceIds.Default));
        Assert.Empty(await store.GetTasksAsync(new() { Kind = TaskListKind.All }));
    }

    [Fact]
    public async Task Invalid_task_rolls_back_the_project_created_in_the_same_import()
    {
        var store = await CreateStoreAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddLocalCopiesAsync(SpaceIds.Default,
            [new() { Id = "project", Name = "Weekend" }], [new() { IntegrationId = IntegrationIds.MicrosoftToDo }], cloud: null));
        Assert.Empty(await store.GetProjectsAsync(SpaceIds.Default));
    }

    private async Task<SqliteTaskStore> CreateStoreAsync()
    {
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        return store;
    }

    private sealed class Resolver(ISyncProvider provider) : IConnectedSyncProviderResolver
    {
        public Task<PersonalCloudConfiguredProvider?> ResolveAsync(string integrationId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(IntegrationIds.MicrosoftToDo, integrationId);
            return Task.FromResult<PersonalCloudConfiguredProvider?>(new(provider, "test-personal-account"));
        }
    }

    private sealed class GraphHandler(bool failSecondTaskPage = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            if (failSecondTaskPage && request.RequestUri.Query == "?cursor=tasks")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            var json = request.RequestUri.PathAndQuery switch
            {
                "/v1.0/me/todo/lists" => """
                    {"value":[{"id":"list1","displayName":"Weekend"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/todo/lists?cursor=lists"}
                    """,
                "/v1.0/me/todo/lists?cursor=lists" => """{"value":[{"id":"list2","displayName":"Empty list"}]}""",
                "/v1.0/me/outlook/masterCategories" or "/v1.0/me/todo/lists/list2/tasks?$top=100" => """{"value":[]}""",
                "/v1.0/me/todo/lists/list1/tasks?$top=100" => """
                    {"value":[{"id":"task1","title":"Pack a picnic","body":{"content":"Bring a blanket"},
                    "importance":"high","dueDateTime":{"dateTime":"2026-12-05T00:00:00","timeZone":"UTC"}}],
                    "@odata.nextLink":"https://graph.microsoft.com/v1.0/me/todo/lists/list1/tasks?cursor=tasks"}
                    """,
                "/v1.0/me/todo/lists/list1/tasks?cursor=tasks" => """
                    {"value":[{"id":"task2","title":"Pick a trail","status":"completed",
                    "completedDateTime":{"dateTime":"2026-11-20T10:00:00","timeZone":"UTC"}}]}
                    """,
                _ => throw new InvalidOperationException($"Unexpected Graph request: {request.RequestUri}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
