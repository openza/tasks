using System.Net;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class MicrosoftChecklistProviderTests
{
    [Fact]
    public async Task Expanded_checklist_preserves_names_completion_and_page_order()
    {
        var paths = new List<string>();
        using var client = new HttpClient(new Handler(path =>
        {
            paths.Add(path);
            if (path.EndsWith("/lists", StringComparison.Ordinal)) return Json("""{"value":[{"id":"list","displayName":"Inbox"}]}""");
            if (path.Contains("masterCategories", StringComparison.Ordinal)) return Json("""{"value":[]}""");
            if (path.Contains("$skiptoken=page2", StringComparison.Ordinal)) return Json("""{"value":[{"id":"empty","title":"Empty","checklistItems":[]}]}""");
            if (path.Contains("/tasks?", StringComparison.Ordinal))
            {
                return Json("""{"value":[{"id":"task","title":"Parent","checklistItems":[{"displayName":"Zulu","isChecked":true,"checkedDateTime":"2026-10-12T09:00:00Z"}],"checklistItems@odata.nextLink":"https://graph.microsoft.com/v1.0/checklist-page-2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/todo/lists/list/tasks?$top=100&$skiptoken=page2&%24expand=checklistItems"}""");
            }
            if (path == "/v1.0/checklist-page-2") return Json("""{"value":[{"displayName":"Alpha","isChecked":false}]}""");
            throw new InvalidOperationException(path);
        }));
        var snapshot = await new MicrosoftToDoProvider(client, "test-token").FetchSnapshotAsync();
        Assert.Equal(2, snapshot.Tasks.Count);
        using var metadata = JsonDocument.Parse(snapshot.Tasks[0].ProviderMetadataJson!);
        var checklist = metadata.RootElement.GetProperty("msToDo").GetProperty("checklistItems");
        Assert.Equal(new[] { "Zulu", "Alpha" }, checklist.EnumerateArray().Select(item => item.GetProperty("displayName").GetString()));
        Assert.True(checklist[0].GetProperty("isChecked").GetBoolean());
        Assert.False(checklist[1].GetProperty("isChecked").GetBoolean());
        Assert.Equal("2026-10-12T09:00:00Z", checklist[0].GetProperty("checkedDateTime").GetString());
        using var empty = JsonDocument.Parse(snapshot.Tasks[1].ProviderMetadataJson!);
        Assert.Equal(0, empty.RootElement.GetProperty("msToDo").GetProperty("checklistItems").GetArrayLength());
        Assert.DoesNotContain(paths, path => path.EndsWith("/checklistItems", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsupported_expansion_does_not_fetch_each_completed_or_open_task_checklist()
    {
        var checklistRequests = 0;
        using var client = new HttpClient(new Handler(path =>
        {
            if (path.EndsWith("/lists", StringComparison.Ordinal)) return Json("""{"value":[{"id":"list"}]}""");
            if (path.Contains("masterCategories", StringComparison.Ordinal)) return Json("""{"value":[]}""");
            if (path.Contains("$expand", StringComparison.Ordinal)) return new(HttpStatusCode.BadRequest);
            if (path.EndsWith("/checklistItems", StringComparison.Ordinal)) { checklistRequests++; return Json("""{"value":[]}"""); }
            return Json("""{"value":[{"id":"completed","status":"completed"},{"id":"open","status":"notStarted"}]}""");
        }));
        var snapshot = await new MicrosoftToDoProvider(client, "test-token").FetchSnapshotAsync();
        Assert.Equal(0, checklistRequests);
        Assert.All(snapshot.Tasks, task =>
        {
            using var metadata = JsonDocument.Parse(task.ProviderMetadataJson!);
            Assert.False(metadata.RootElement.GetProperty("msToDo").TryGetProperty("checklistItems", out _));
        });
    }

    [Theory]
    [InlineData(true, "$expand")]
    [InlineData(true, "%24expand")]
    [InlineData(false, "%24expand")]
    [InlineData(false, "%24%65xpand")]
    public async Task Expansion_failure_disables_it_for_remaining_encoded_pages_and_lists(bool rejectFirstPage, string expandKey)
    {
        var paths = new List<string>();
        var failures = new List<int>();
        using var client = new HttpClient(new Handler(path =>
        {
            paths.Add(path);
            if (path.EndsWith("/lists", StringComparison.Ordinal)) return Json("""{"value":[{"id":"list"},{"id":"other"}]}""");
            if (path.Contains("masterCategories", StringComparison.Ordinal)) return Json("""{"value":[]}""");
            if (path.EndsWith("/checklistItems", StringComparison.Ordinal)) return Json("""{"value":[]}""");
            var page = path.Contains("other/tasks", StringComparison.Ordinal) ? 4 : path.Contains("page3", StringComparison.Ordinal) ? 3 : path.Contains("page2", StringComparison.Ordinal) ? 2 : 1;
            var expanded = Uri.UnescapeDataString(path).Contains("$expand=", StringComparison.Ordinal);
            if (expanded && page >= (rejectFirstPage ? 1 : 2)) { failures.Add(paths.Count - 1); return new(HttpStatusCode.BadRequest); }
            var task = new Dictionary<string, object> { ["id"] = $"task{page}" };
            if (expanded) task["checklistItems"] = Array.Empty<object>();
            var response = new Dictionary<string, object> { ["value"] = new[] { task } };
            if (page < 3) response["@odata.nextLink"] = $"https://graph.microsoft.com/v1.0/me/todo/lists/list/tasks?$top=100&$skiptoken=page{page + 1}&{expandKey}=checklistItems";
            return Json(JsonSerializer.Serialize(response));
        }));
        var snapshot = await new MicrosoftToDoProvider(client, "test-token").FetchSnapshotAsync();
        Assert.Equal(4, snapshot.Tasks.Count);
        Assert.Single(failures);
        Assert.DoesNotContain(paths.Skip(failures[0] + 1), path => Uri.UnescapeDataString(path).Contains("$expand=", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.EndsWith("/checklistItems", StringComparison.Ordinal));
        Assert.All(snapshot.Tasks.Skip(rejectFirstPage ? 0 : 1), task =>
        {
            using var metadata = JsonDocument.Parse(task.ProviderMetadataJson!);
            Assert.False(metadata.RootElement.GetProperty("msToDo").TryGetProperty("checklistItems", out _));
        });
    }

    [Fact]
    public async Task Expansion_is_retried_on_the_next_snapshot()
    {
        var supported = false;
        using var client = new HttpClient(new Handler(path =>
        {
            if (path.EndsWith("/lists", StringComparison.Ordinal)) return Json("""{"value":[{"id":"list"}]}""");
            if (path.Contains("masterCategories", StringComparison.Ordinal)) return Json("""{"value":[]}""");
            if (path.Contains("$expand", StringComparison.Ordinal) && !supported) return new(HttpStatusCode.NotImplemented);
            return supported ? Json("""{"value":[{"id":"task","checklistItems":[{"displayName":"Retried"}]}]}""") : Json("""{"value":[{"id":"task"}]}""");
        }));
        var provider = new MicrosoftToDoProvider(client, "test-token");
        using var missing = JsonDocument.Parse(Assert.Single((await provider.FetchSnapshotAsync()).Tasks).ProviderMetadataJson!);
        Assert.False(missing.RootElement.GetProperty("msToDo").TryGetProperty("checklistItems", out _));
        supported = true;
        using var present = JsonDocument.Parse(Assert.Single((await provider.FetchSnapshotAsync()).Tasks).ProviderMetadataJson!);
        Assert.Equal("Retried", present.RootElement.GetProperty("msToDo").GetProperty("checklistItems")[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Missing_expanded_collection_stays_absent_without_per_task_requests()
    {
        using var client = new HttpClient(new Handler(path => path switch
        {
            "/v1.0/me/todo/lists" => Json("""{"value":[{"id":"list"}]}"""),
            "/v1.0/me/outlook/masterCategories" => Json("""{"value":[]}"""),
            _ when path.Contains("/tasks?", StringComparison.Ordinal) => Json("""{"value":[{"id":"task"}]}"""),
            _ => throw new InvalidOperationException(path),
        }));
        var snapshot = await new MicrosoftToDoProvider(client, "test-token").FetchSnapshotAsync();
        using var metadata = JsonDocument.Parse(Assert.Single(snapshot.Tasks).ProviderMetadataJson!);
        Assert.False(metadata.RootElement.GetProperty("msToDo").TryGetProperty("checklistItems", out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Repeated_pages_and_cycles_fail_the_snapshot(bool taskPages, bool cycle)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler(path =>
        {
            Assert.True(++requests < 12, "Paging did not stop.");
            if (path.EndsWith("/lists", StringComparison.Ordinal)) return Json("""{"value":[{"id":"list"}]}""");
            if (path.Contains("masterCategories", StringComparison.Ordinal)) return Json("""{"value":[]}""");
            var initial = taskPages ? "/me/todo/lists/list/tasks?$top=100&$expand=checklistItems" : "/me/todo/lists/list/tasks/task/checklistItems";
            if (!taskPages && path.Contains("/tasks?", StringComparison.Ordinal)) return Json("""{"value":[{"id":"task","checklistItems":[],"checklistItems@odata.nextLink":"https://graph.microsoft.com/v1.0/me/todo/lists/list/tasks/task/checklistItems"}]}""");
            var target = cycle && path != "/v1.0/page-2" ? "/page-2" : initial;
            return Json(JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = Array.Empty<object>(),
                ["@odata.nextLink"] = $"https://graph.microsoft.com/v1.0{target}" }));
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MicrosoftToDoProvider(client, "test-token").FetchSnapshotAsync());
        Assert.Equal("Microsoft Graph returned a repeated collection page.", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Checklist_failure_never_becomes_empty_data(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler(path => path switch
        {
            "/v1.0/me/todo/lists" => Json("""{"value":[{"id":"list"}]}"""),
            "/v1.0/me/outlook/masterCategories" => Json("""{"value":[]}"""),
            _ when path.Contains("/tasks?", StringComparison.Ordinal) => Json("""{"value":[{"id":"task","checklistItems":[],"checklistItems@odata.nextLink":"https://graph.microsoft.com/v1.0/checklist-page-2"}]}"""),
            _ => new(status),
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => new MicrosoftToDoProvider(client, "test-token").FetchSnapshotAsync());
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request.RequestUri!.PathAndQuery));
    }
}
