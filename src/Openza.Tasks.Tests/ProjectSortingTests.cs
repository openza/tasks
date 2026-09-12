using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed class ProjectSortingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-project-sort-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ProjectSortMode.Default, false, "c,b,a")]
    [InlineData(ProjectSortMode.Default, true, "c,b,a")]
    [InlineData(ProjectSortMode.Name, false, "a,b,c")]
    [InlineData(ProjectSortMode.Name, true, "c,b,a")]
    [InlineData(ProjectSortMode.Created, false, "b,a,c")]
    [InlineData(ProjectSortMode.Created, true, "c,a,b")]
    [InlineData(ProjectSortMode.Updated, false, "a,c,b")]
    [InlineData(ProjectSortMode.Updated, true, "c,a,b")]
    [InlineData(ProjectSortMode.OpenTaskCount, false, "b,c,a")]
    [InlineData(ProjectSortMode.OpenTaskCount, true, "a,c,b")]
    public void Modes_and_directions_sort_consistently(ProjectSortMode mode, bool descending, string expected)
    {
        var projects = new[]
        {
            Project("a", "Alpha", 2) with { CreatedAt = DateTimeOffset.UnixEpoch.AddDays(2), UpdatedAt = DateTimeOffset.UnixEpoch },
            Project("b", "Beta", 1) with { CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = null },
            Project("c", "Charlie", 0) with { CreatedAt = DateTimeOffset.UnixEpoch.AddDays(3), UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(3) },
        };
        var counts = new Dictionary<string, int> { ["a"] = 3, ["c"] = 1 };
        var result = ProjectSorting.Sort(projects, new() { Mode = mode, Descending = descending }, counts);
        Assert.Equal(expected.Split(','), result.Select(project => project.Id));
        Assert.Equal(new[] { "a", "b", "c" }, projects.Select(project => project.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Favorites_and_equal_names_have_stable_order(bool descending)
    {
        var projects = new[] { Project("b", "same"), Project("a", "Same"), Project("favorite", "Zulu") with { IsFavorite = true } };
        var settings = new ProjectSortSettings { Mode = ProjectSortMode.Name, Descending = descending };
        var result = ProjectSorting.Sort(projects, settings, new Dictionary<string, int>());
        Assert.Equal(new[] { "favorite", "a", "b" }, result.Select(project => project.Id));
        Assert.Equal(result, ProjectSorting.Sort(projects.Reverse(), settings, new Dictionary<string, int>()));
        var unpinned = ProjectSorting.Sort(projects, settings with { FavoritesFirst = false }, new Dictionary<string, int>());
        Assert.Equal(descending ? "favorite" : "a", unpinned[0].Id);
    }

    [Fact]
    public void Unknown_mode_falls_back_to_default_order()
    {
        var projects = new[] { Project("a", "Alpha", 2), Project("b", "Beta", 1) };
        Assert.Equal("b", ProjectSorting.Sort(projects, new() { Mode = (ProjectSortMode)999, Descending = true }, new Dictionary<string, int>())[0].Id);
    }

    [Fact]
    public async Task Sorting_preserves_project_task_and_unsaved_edits_without_writing_project_order()
    {
        var (store, vm, _) = await CreateAsync();
        await store.UpsertProjectAsync(Project("a", "Alpha", 2));
        await store.UpsertProjectAsync(Project("b", "Beta", 1));
        await store.UpsertTaskAsync(new TaskItem { Id = "task", Title = "Original", ProjectId = "a" });
        await vm.SelectNavigationAsync(vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await vm.SelectProjectAsync(vm.ProjectItems.Single(item => item.Project.Id == "a"));
        await vm.SelectTaskAsync(Assert.Single(vm.Tasks));
        vm.DetailTitle = "Unsaved title";
        vm.DetailNotes = "Unsaved notes";
        var selectedTask = vm.SelectedTask;
        vm.ProjectItems.CollectionChanged += (_, _) => vm.SelectedProject = null;
        await vm.SetProjectSortAsync(new() { Mode = ProjectSortMode.Name });
        Assert.Equal(new[] { "a", "b" }, vm.ProjectItems.Select(item => item.Project.Id));
        Assert.Equal("a", vm.SelectedProject?.Project.Id);
        Assert.Same(selectedTask, vm.SelectedTask);
        Assert.Equal("Unsaved title", vm.DetailTitle);
        Assert.Equal("Unsaved notes", vm.DetailNotes);
        Assert.Equal("Original", (await store.GetTaskAsync("task"))!.Title);
        Assert.Equal(2, (await store.GetProjectsAsync()).Single(project => project.Id == "a").SortOrder);
    }

    [Fact]
    public async Task Preferences_are_separate_per_space_and_survive_restart()
    {
        var (store, vm, preferences) = await CreateAsync();
        var other = new SpaceItem { Id = "other", Name = "Other" };
        await store.UpsertSpaceAsync(other);
        await vm.SelectSpaceAsync(new SpaceNavigationItemViewModel(null));
        await vm.SetProjectSortAsync(new() { Mode = ProjectSortMode.Name, FavoritesFirst = false });
        await vm.SelectSpaceAsync(new SpaceNavigationItemViewModel(other));
        Assert.Equal(ProjectSortMode.Default, vm.ProjectSort.Mode);
        await vm.SetProjectSortAsync(new() { Mode = ProjectSortMode.Created, Descending = true });
        var restored = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
        await restored.SelectSpaceAsync(new SpaceNavigationItemViewModel(null));
        Assert.Equal(ProjectSortMode.Name, restored.ProjectSort.Mode);
        Assert.False(restored.ProjectSort.FavoritesFirst);
        await restored.SelectSpaceAsync(new SpaceNavigationItemViewModel(other));
        Assert.Equal(ProjectSortMode.Created, restored.ProjectSort.Mode);
        Assert.True(restored.ProjectSort.Descending);
    }

    [Fact]
    public async Task Count_sort_updates_after_completion_and_honors_filters()
    {
        var (store, vm, _) = await CreateAsync();
        await store.UpsertProjectAsync(Project("a", "Alpha"));
        await store.UpsertProjectAsync(Project("b", "Beta"));
        await store.UpsertProjectAsync(Project("done", "Done") with { Status = ProjectLifecycleStates.Completed });
        await store.UpsertTaskAsync(new TaskItem { Id = "task", Title = "Task", ProjectId = "b" });
        await vm.SelectNavigationAsync(vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await vm.SetProjectSortAsync(new() { Mode = ProjectSortMode.OpenTaskCount, Descending = true });
        Assert.Equal(new[] { "b", "a" }, vm.ProjectItems.Select(item => item.Project.Id));
        await store.CompleteTaskAsync("task");
        await vm.ApplySearchAsync();
        Assert.Equal(new[] { "a", "b" }, vm.ProjectItems.Select(item => item.Project.Id));
        vm.ProjectSearchText = "Beta";
        vm.ApplyProjectFilter();
        Assert.Equal("b", Assert.Single(vm.ProjectItems).Project.Id);
        vm.ProjectSearchText = "";
        vm.ProjectFilterIndex = 2;
        vm.ApplyProjectFilter();
        Assert.Equal("done", Assert.Single(vm.ProjectItems).Project.Id);
    }

    [Fact]
    public async Task Older_preferences_retain_existing_order_and_other_settings()
    {
        var (store, vm, preferences) = await CreateAsync();
        await preferences.SaveAsync(new DesktopPreferences { Theme = "Dark" });
        await store.UpsertProjectAsync(Project("a", "Alpha", 2));
        await store.UpsertProjectAsync(Project("b", "Beta", 1));
        await vm.ApplySearchAsync();
        Assert.Equal(new[] { "b", "a" }, vm.ProjectItems.Select(item => item.Project.Id));
        Assert.True(vm.ProjectSort.FavoritesFirst);
        await vm.SetProjectSortAsync(new() { Mode = ProjectSortMode.Name });
        Assert.Equal("Dark", preferences.Load().Theme);
    }

    private async Task<(SqliteTaskStore, MainWindowViewModel, DesktopPreferencesStore)> CreateAsync()
    {
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        var preferences = new DesktopPreferencesStore(Path.Combine(_directory, "settings.json"));
        return (store, new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences), preferences);
    }

    private static ProjectItem Project(string id, string name, int order = 0) => new() { Id = id, Name = name, SortOrder = order };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
