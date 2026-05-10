using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class TaskSyncEngineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-tasks-sync-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Sync_imports_provider_tasks_and_pushes_pending_completions()
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
    }

    [Fact]
    public async Task Sync_merges_provider_refresh_without_losing_local_project_or_notes()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "proj_local",
            IntegrationId = IntegrationIds.Local,
            Name = "Local project",
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "todoist_remote_1",
            ExternalId = "remote_1",
            IntegrationId = IntegrationIds.Todoist,
            Title = "Old title",
            ProjectId = "proj_local",
            Status = TaskItemStatus.Waiting,
            Notes = "Local note",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        var engine = new TaskSyncEngine(store);

        var result = await engine.SyncAsync(new FakeProvider());

        var task = await store.GetTaskAsync("todoist_remote_1");
        Assert.True(result.Success);
        Assert.Equal(1, result.TasksUpdated);
        Assert.NotNull(task);
        Assert.Equal("Remote task", task.Title);
        Assert.Equal("proj_local", task.ProjectId);
        Assert.Equal(TaskItemStatus.Waiting, task.Status);
        Assert.Equal("Local note", task.Notes);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), task.CreatedAt);
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
        public int CompletedCalls { get; private set; }

        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProviderSnapshot(
                [new TaskItem
                {
                    Id = "todoist_remote_1",
                    ExternalId = "remote_1",
                    IntegrationId = IntegrationIds.Todoist,
                    Title = "Remote task",
                }],
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
