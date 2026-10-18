using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed class ReadQueryCharacterizationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-read-characterization", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Empty_search_returns_before_opening_a_missing_database_even_when_cancelled()
    {
        var path = Path.Combine(_directory, "missing.db");
        var store = new SqliteTaskStore(path, readOnly: true);
        Assert.Empty(await store.SearchAsync(new() { SearchText = "  \t\n" }, new CancellationToken(true)));
        Assert.False(Directory.Exists(_directory));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetTasksAsync(new(), new CancellationToken(true)));
    }

    [Fact]
    public async Task Search_preserves_task_project_ties_snippet_limit_and_archived_space_filter()
    {
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new() { Id = "hidden", Name = "Archived", IsArchived = true });
        await store.UpsertProjectAsync(new() { Id = "project", Name = "Target" });
        await store.UpsertTaskAsync(new() { Id = "exact", Title = "Target" });
        await store.UpsertTaskAsync(new() { Id = "body", Title = "Unrelated", Notes = "target\n" + new string('x', 160) });
        await store.UpsertTaskAsync(new() { Id = "archived", SpaceId = "hidden", Title = "Target" });
        await store.UpsertTaskAsync(new() { Id = "completed", Title = "Target", Status = TaskItemStatus.Completed });

        var results = await store.SearchAsync(new() { SearchText = "TARGET target", IncludeAllSpaces = true, Limit = 0 });
        Assert.Equal(new[] { "exact", "project", "body" }, results.Select(x => x.Id));
        Assert.Equal(new[] { 700, 700, 300 }, results.Select(x => x.Score));
        Assert.Equal(140, results[^1].Snippet.Length);
        Assert.DoesNotContain('\n', results[^1].Snippet);
        Assert.EndsWith("...", results[^1].Snippet);
        Assert.Equal("exact", Assert.Single(await store.SearchAsync(new() { SearchText = "target", IncludeAllSpaces = true, Limit = 1 })).Id);
        var readonlyStore = new SqliteTaskStore(store.DatabasePath, readOnly: true);
        await readonlyStore.InitializeAsync();
        Assert.Equal(results, await readonlyStore.SearchAsync(new() { SearchText = "TARGET target", IncludeAllSpaces = true, Limit = 0 }));
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
