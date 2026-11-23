using System.Text.Json;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed partial class MicrosoftCategoryAdoptionTests
{
    private const string CombinedSnapshot = """{"msToDo":{"categories":[{"name":"Release","color":"#32CD32"}],"checklistItems":[{"displayName":"First","isChecked":true},{"displayName":"Second","isChecked":false}]}}""";

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"msToDo\":{\"categories\":[{\"name\":\"Release\",\"color\":\"#32CD32\"}]}}")]
    [InlineData("{\"msToDo\":{\"checklistItems\":[{\"displayName\":\"First\",\"isChecked\":true},{\"displayName\":\"Second\",\"isChecked\":false}]}}")]
    [InlineData(CombinedSnapshot)]
    public async Task Categories_and_steps_have_independent_markers_and_preserve_both_after_refresh_and_relink(string initialSnapshot)
    {
        var store = await StoreAsync();
        var source = Source("Release") with { SnapshotJson = initialSnapshot };
        await store.UpsertProviderSourceItemAsync(source);
        var task = (await store.AdoptProviderSourceItemAsync(source.Id))!;
        using (var initial = JsonDocument.Parse(initialSnapshot))
        {
            using var initialTask = JsonDocument.Parse(task.SourceMetadataJson!);
            var hasMicrosoft = initial.RootElement.TryGetProperty("msToDo", out var microsoft);
            Assert.Equal(hasMicrosoft && microsoft.TryGetProperty("categories", out _),
                initialTask.RootElement.TryGetProperty("msToDoCategoriesApplied", out var applied) && applied.GetBoolean());
            Assert.Equal(hasMicrosoft && microsoft.TryGetProperty("checklistItems", out _),
                task.SourceMetadataJson!.Contains("\"msToDoStepsApplied\":true"));
        }
        source = source with { SnapshotJson = CombinedSnapshot };
        await store.UpsertProviderSourceItemAsync(source);
        task = (await store.GetTaskAsync(task.Id))!;
        Assert.True(Applied(task));
        Assert.Contains("\"msToDoStepsApplied\":true", task.SourceMetadataJson);
        Assert.Equal("Release", Assert.Single(task.Labels).Name);
        var children = await store.GetTasksAsync(new() { ParentId = task.Id, Kind = TaskListKind.All,
            IncludeSubtasks = true, SortMode = TaskSortMode.CreatedNewest, SortDirection = TaskSortDirection.Descending });
        Assert.Equal(new[] { "First", "Second" }, children.Select(child => child.Title));
        Assert.True(children[0].IsCompleted);
        Assert.False(children[1].IsCompleted);
        await store.SetTaskLabelsAsync(task.Id, []);
        foreach (var child in children) await store.DeleteTaskAsync(child.Id);
        task = (await store.GetTaskAsync(task.Id))!;
        await store.UpsertTaskAsync(task with { SourceMetadataJson = null });
        await store.DetachProviderSourceItemAsync(source.Id);
        await store.UpsertProviderSourceItemAsync(source);
        task = (await store.GetTaskAsync(task.Id))!;
        Assert.True(Applied(task));
        Assert.Contains("\"msToDoStepsApplied\":true", task.SourceMetadataJson);
        Assert.Empty(task.Labels);
        Assert.Empty(await store.GetTasksAsync(new() { ParentId = task.Id, Kind = TaskListKind.All, IncludeSubtasks = true }));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public async Task Empty_categories_and_steps_mark_both_without_creating_rows()
    {
        var store = await StoreAsync();
        var task = await AdoptAsync(store, Source() with { SnapshotJson = """{"msToDo":{"categories":[],"checklistItems":[]}}""" });
        Assert.True(Applied(task));
        Assert.Contains("\"msToDoStepsApplied\":true", task.SourceMetadataJson);
        Assert.Empty(task.Labels);
        Assert.Single(await store.GetTasksAsync(new() { Kind = TaskListKind.All, IncludeSubtasks = true }));
    }

    [Fact]
    public async Task Malformed_steps_roll_back_category_adoption_and_both_markers()
    {
        var store = await StoreAsync();
        var source = Source("Release") with { SnapshotJson = """{"msToDo":{"categories":[{"name":"Release"}],"checklistItems":[7]}}""" };
        await store.UpsertProviderSourceItemAsync(source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdoptProviderSourceItemAsync(source.Id));
        Assert.Empty(await store.GetTasksAsync(new() { Kind = TaskListKind.All, IncludeSubtasks = true }));
        Assert.Empty(await store.GetLabelsAsync());
    }
}
