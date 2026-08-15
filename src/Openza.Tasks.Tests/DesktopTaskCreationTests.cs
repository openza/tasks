using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed class DesktopTaskCreationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "openza-tasks-desktop-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateTaskAsync_parses_trims_and_deduplicates_comma_separated_labels()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        var viewModel = new MainWindowViewModel(store);

        await viewModel.CreateTaskAsync(new AddTaskDraft(
            "Review labels",
            string.Empty,
            null,
            0,
            2,
            null,
            " work, Urgent, WORK,  ",
            false));

        var tasks = await store.GetTasksAsync(new TaskQuery
        {
            Kind = TaskListKind.All,
            IncludeSubtasks = true,
        });

        var task = Assert.Single(tasks);
        Assert.Equal(new[] { "Urgent", "work" }, task.Labels.Select(label => label.Name).Order());
    }

    [Fact]
    public async Task Task_list_shows_only_top_level_tasks_and_keeps_subtasks_in_details()
    {
        var store = await CreateStoreAsync();
        var parent = CreateTask("parent", "Parent task");
        await store.UpsertTaskAsync(parent);
        await store.UpsertTaskAsync(CreateTask("child", "Child task") with { ParentId = parent.Id });
        var viewModel = new MainWindowViewModel(store);

        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        var visibleParent = Assert.Single(viewModel.Tasks);
        await viewModel.SelectTaskAsync(visibleParent);

        Assert.Equal("parent", visibleParent.Task.Id);
        Assert.Equal("child", Assert.Single(viewModel.Subtasks).Task.Id);
    }

    [Fact]
    public async Task Saving_changed_Todoist_date_queues_one_write_back()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("adopted", "Adopted task") with
        {
            SourceIntegrationId = IntegrationIds.Todoist,
            SourceProviderTaskId = "todoist-task",
            PlannedOn = new DateOnly(2026, 8, 14),
        });
        var viewModel = new MainWindowViewModel(store);
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(Assert.Single(viewModel.Tasks));
        viewModel.DetailDate = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

        Assert.True(await viewModel.SaveSelectedAsync());

        var update = Assert.Single(await store.GetPendingTaskDateUpdatesAsync(IntegrationIds.Todoist));
        Assert.Equal("todoist-task", update.ProviderTaskId);
        Assert.Equal(new DateOnly(2026, 8, 15), update.PlannedOn);
    }

    [Fact]
    public async Task Refresh_marks_programmatic_detail_changes_as_suppressed()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("task", "Refresh task"));
        var viewModel = new MainWindowViewModel(store);
        var detailEventsWereSuppressed = true;
        var detailEventCount = 0;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(MainWindowViewModel.DetailProject) or
                nameof(MainWindowViewModel.DetailStatusIndex) or
                nameof(MainWindowViewModel.DetailDate))
            {
                detailEventCount++;
                detailEventsWereSuppressed &= viewModel.IsUpdatingDetails;
            }
        };

        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(Assert.Single(viewModel.Tasks));

        Assert.True(detailEventCount > 0);
        Assert.True(detailEventsWereSuppressed);
        Assert.False(viewModel.IsUpdatingDetails);
    }

    private async Task<SqliteTaskStore> CreateStoreAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        return store;
    }

    private static TaskItem CreateTask(string id, string title) => new()
    {
        Id = id,
        SpaceId = SpaceIds.Default,
        IntegrationId = IntegrationIds.Local,
        Title = title,
        Status = TaskItemStatus.Inbox,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    public void Dispose() => TestDirectory.Delete(_directory);
}
