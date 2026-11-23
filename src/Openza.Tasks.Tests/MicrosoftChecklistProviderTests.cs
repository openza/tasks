using System.Net;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class MicrosoftChecklistProviderTests
{
    [Theory]
    [InlineData("expanded")]
    [InlineData("omitted")]
    [InlineData("unsupported")]
    public async Task Checklist_preserves_names_completion_and_page_order_with_expansion_or_fallback(string mode)
    {
        var paths = new List<string>();
        using var client = new HttpClient(new Handler(path =>
        {
            paths.Add(path);
            if (path.EndsWith("/lists", StringComparison.Ordinal)) return Json("""{"value":[{"id":"list","displayName":"Inbox"}]}""");
            if (path.Contains("masterCategories", StringComparison.Ordinal)) return Json("""{"value":[]}""");
            if (path.Contains("/tasks?", StringComparison.Ordinal))
            {
                if (mode == "unsupported" && path.Contains("$expand", StringComparison.Ordinal)) return new(HttpStatusCode.BadRequest);
                return mode == "expanded"
                    ? Json("""{"value":[{"id":"task","title":"Parent","checklistItems":[{"displayName":"Zulu","isChecked":true,"checkedDateTime":"2026-10-12T09:00:00Z"}],"checklistItems@odata.nextLink":"https://graph.microsoft.com/v1.0/checklist-page-2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/tasks-page-2"}""")
                    : Json("""{"value":[{"id":"task","title":"Parent"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/tasks-page-2"}""");
            }
            if (path.EndsWith("/checklistItems", StringComparison.Ordinal)) return Json("""{"value":[{"displayName":"Zulu","isChecked":true,"checkedDateTime":"2026-10-12T09:00:00Z"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/checklist-page-2"}""");
            if (path == "/v1.0/checklist-page-2") return Json("""{"value":[{"displayName":"Alpha","isChecked":false}]}""");
            if (path == "/v1.0/tasks-page-2") return Json("""{"value":[{"id":"empty","title":"Empty","checklistItems":[]}]}""");
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
        Assert.Equal(mode == "expanded" ? 0 : 1, paths.Count(path => path.EndsWith("/checklistItems", StringComparison.Ordinal)));
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
            if (!taskPages && path.Contains("/tasks?", StringComparison.Ordinal)) return Json("""{"value":[{"id":"task"}]}""");
            var target = cycle && path != "/v1.0/page-2" ? "/page-2" : initial;
            return Json(JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = Array.Empty<object>(),
                ["@odata.nextLink"] = $"https://graph.microsoft.com/v1.0{target}" }));
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MicrosoftToDoProvider(client, "test-token").FetchSnapshotAsync());
        Assert.Equal("Microsoft Graph returned a repeated collection page.", error.Message);
    }

    [Fact]
    public async Task Checklist_failure_never_becomes_empty_data()
    {
        using var client = new HttpClient(new Handler(path => path switch
        {
            "/v1.0/me/todo/lists" => Json("""{"value":[{"id":"list"}]}"""),
            "/v1.0/me/outlook/masterCategories" => Json("""{"value":[]}"""),
            _ when path.Contains("/tasks?", StringComparison.Ordinal) => Json("""{"value":[{"id":"task"}]}"""),
            _ => new(HttpStatusCode.ServiceUnavailable),
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => new MicrosoftToDoProvider(client, "test-token").FetchSnapshotAsync());
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request.RequestUri!.PathAndQuery));
    }
}
