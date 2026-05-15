using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class TaskSyncEngineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-tasks-sync-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Sync_records_provider_source_items_and_pushes_pending_completions()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.QueueCompletionAsync(new PendingCompletion
        {
            Id = "completion_1",
            TaskId = "local",
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote_1",
            Completed = true,
        });
        var provider = new FakeProvider();
        var engine = new TaskSyncEngine(store);

        var result = await engine.SyncAsync(provider);

        Assert.True(result.Success);
        Assert.Equal(1, result.TasksAdded);
        Assert.Equal(1, result.CompletionsSynced);
        Assert.Equal(1, provider.CompletedCalls);
        Assert.Empty(await store.GetPendingCompletionsAsync(IntegrationIds.Todoist));
        Assert.Empty(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
        Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
    }

    [Fact]
    public async Task Sync_preserves_provider_tasks_missing_from_snapshot_by_default()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "todoist_orphan",
            ExternalId = "orphan",
            IntegrationId = IntegrationIds.Todoist,
            Title = "Local provider task",
        });
        var engine = new TaskSyncEngine(store);

        var result = await engine.SyncAsync(new EmptyProvider());

        Assert.True(result.Success);
        Assert.Equal(0, result.TasksDeleted);
        Assert.NotNull(await store.GetTaskAsync("todoist_orphan"));
        Assert.Empty(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
    }

    [Fact]
    public async Task Sync_stores_provider_parent_reference_for_subtasks()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider
        {
            IncludeChildTask = true,
        };
        var engine = new TaskSyncEngine(store);

        var result = await engine.SyncAsync(provider);

        var sources = await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true);
        var childSource = Assert.Single(sources, source => source.ExternalId == "remote_child");
        Assert.True(result.Success);
        Assert.Equal("remote_1", childSource.ParentExternalId);
    }

    [Fact]
    public async Task Sync_refreshes_adopted_wrapper_provider_fields_without_overwriting_openza_fields()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "proj_local",
            IntegrationId = IntegrationIds.Local,
            Name = "Local project",
        });
        var provider = new FakeProvider { Title = "Remote task", Description = "Remote description" };
        var engine = new TaskSyncEngine(store);

        var firstSync = await engine.SyncAsync(provider);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
        var adopted = await store.AdoptProviderSourceItemAsync(source.Id);
        Assert.True(firstSync.Success);
        Assert.NotNull(adopted);

        var localCreatedAt = adopted.CreatedAt;
        await store.UpsertTaskAsync(adopted with
        {
            Title = "Old title",
            ProjectId = "proj_local",
            Priority = 1,
            Status = TaskItemStatus.Waiting,
            Notes = "Local note",
            PlannedOn = new DateOnly(2026, 5, 13),
            DeadlineOn = new DateOnly(2026, 5, 14),
            ScheduledStart = new DateTimeOffset(2026, 5, 13, 9, 0, 0, 0, TimeSpan.Zero),
            ScheduledEnd = new DateTimeOffset(2026, 5, 13, 10, 0, 0, 0, TimeSpan.Zero),
            DurationMinutes = 60,
            RecurrenceRule = "FREQ=WEEKLY;BYDAY=WE",
            LocalMetadataJson = """{"focus":"deep"}""",
        });

        provider.Title = "Remote task updated";
        provider.Description = "Remote description updated";
        var result = await engine.SyncAsync(provider);

        var task = await store.GetTaskAsync(adopted.Id);
        Assert.True(result.Success);
        Assert.Equal(1, result.TasksUpdated);
        Assert.NotNull(task);
        Assert.Equal("Remote task updated", task.Title);
        Assert.Null(task.Description);
        Assert.Equal("Remote description updated", task.SourceDescription);
        Assert.Equal("proj_local", task.ProjectId);
        Assert.Equal(1, task.Priority);
        Assert.Equal(TaskItemStatus.Waiting, task.Status);
        Assert.Equal("Local note", task.Notes);
        Assert.Equal(new DateOnly(2026, 5, 13), task.PlannedOn);
        Assert.Equal(new DateOnly(2026, 5, 14), task.DeadlineOn);
        Assert.Equal(new DateTimeOffset(2026, 5, 13, 9, 0, 0, 0, TimeSpan.Zero), task.ScheduledStart);
        Assert.Equal(new DateTimeOffset(2026, 5, 13, 10, 0, 0, 0, TimeSpan.Zero), task.ScheduledEnd);
        Assert.Equal(60, task.DurationMinutes);
        Assert.Equal("FREQ=WEEKLY;BYDAY=WE", task.RecurrenceRule);
        Assert.Equal("""{"focus":"deep"}""", task.LocalMetadataJson);
        Assert.Equal(localCreatedAt.ToUnixTimeSeconds(), task.CreatedAt.ToUnixTimeSeconds());
        Assert.Equal(IntegrationIds.Local, task.IntegrationId);
        Assert.Equal(IntegrationIds.Todoist, task.SourceIntegrationId);
        Assert.Equal("remote_1", task.SourceExternalId);
    }

    [Fact]
    public async Task Sync_reopens_completed_recurring_provider_task_with_next_source_date()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider
        {
            Title = "Pay monthly bill",
            PlannedOn = new DateOnly(2026, 6, 6),
            RecurrenceRule = "every 6th",
        };
        var engine = new TaskSyncEngine(store);

        var firstSync = await engine.SyncAsync(provider);
        var task = Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
        await store.QueueCompletionAsync(new PendingCompletion
        {
            Id = "completion_recurring",
            TaskId = task.Id,
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = task.SourceProviderTaskId!,
            Completed = true,
            CompletedAt = DateTimeOffset.UtcNow,
        });
        await store.CompleteTaskAsync(task.Id);

        provider.PlannedOn = new DateOnly(2026, 7, 6);
        var secondSync = await engine.SyncAsync(provider);

        var refreshed = await store.GetTaskAsync(task.Id);
        Assert.True(firstSync.Success);
        Assert.True(secondSync.Success);
        Assert.NotNull(refreshed);
        Assert.Equal(1, provider.CompletedCalls);
        Assert.Equal(TaskCompletionState.Open, refreshed.CompletionState);
        Assert.Null(refreshed.CompletedAt);
        Assert.Equal(new DateOnly(2026, 7, 6), refreshed.PlannedOn);
        Assert.Equal("every 6th", refreshed.RecurrenceRule);
    }

    [Fact]
    public async Task Sync_refreshes_open_recurring_provider_task_with_next_source_date()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider
        {
            Title = "Pay monthly bill",
            PlannedOn = new DateOnly(2026, 6, 6),
            RecurrenceRule = "every 6th",
        };
        var engine = new TaskSyncEngine(store);

        var firstSync = await engine.SyncAsync(provider);
        var task = Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));

        provider.PlannedOn = new DateOnly(2026, 7, 6);
        var secondSync = await engine.SyncAsync(provider);

        var refreshed = await store.GetTaskAsync(task.Id);
        Assert.True(firstSync.Success);
        Assert.True(secondSync.Success);
        Assert.NotNull(refreshed);
        Assert.Equal(TaskCompletionState.Open, refreshed.CompletionState);
        Assert.Equal(new DateOnly(2026, 7, 6), refreshed.PlannedOn);
        Assert.Equal("every 6th", refreshed.RecurrenceRule);
    }

    private SqliteTaskStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new SqliteTaskStore(Path.Combine(_directory, "openza_tasks.db"));
    }

    public void Dispose()
    {
        TestDirectory.Delete(_directory);
    }

    private sealed class FakeProvider : ISyncProvider
    {
        public string IntegrationId => IntegrationIds.Todoist;
        public string Title { get; set; } = "Remote task";
        public string? Description { get; set; }
        public DateOnly? PlannedOn { get; set; }
        public string? RecurrenceRule { get; set; }
        public bool IncludeChildTask { get; set; }
        public int CompletedCalls { get; private set; }

        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
        {
            List<TaskItem> tasks =
            [
                new TaskItem
                {
                    Id = "todoist_remote_1",
                    ExternalId = "remote_1",
                    IntegrationId = IntegrationIds.Todoist,
                    Title = Title,
                    Description = Description,
                    PlannedOn = PlannedOn,
                    RecurrenceRule = RecurrenceRule,
                },
            ];
            if (IncludeChildTask)
            {
                tasks.Add(new TaskItem
                {
                    Id = "todoist_remote_child",
                    ExternalId = "remote_child",
                    IntegrationId = IntegrationIds.Todoist,
                    Title = "Remote child",
                    ParentId = "todoist_remote_1",
                });
            }

            return Task.FromResult(new ProviderSnapshot(
                tasks,
                [],
                []));
        }

        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default)
        {
            CompletedCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyProvider : ISyncProvider
    {
        public string IntegrationId => IntegrationIds.Todoist;

        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderSnapshot([], [], []));

        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
