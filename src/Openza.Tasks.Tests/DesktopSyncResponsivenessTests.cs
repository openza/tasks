using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Desktop.ViewModels;
using Openza.Tasks.Application.Tasks;

namespace Openza.Tasks.Tests;

public sealed class DesktopSyncResponsivenessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-sync-ui-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Sync_preserves_unsaved_editor_and_rejects_duplicate_requests()
    {
        var (store, vm, provider) = await CreateAsync();
        await vm.SelectNavigationAsync(vm.NavigationItems.Single(x => x.Kind == TaskListKind.Open));
        await vm.SelectTaskAsync(Assert.Single(vm.Tasks));
        var sync = vm.RunTodoistSyncAsync();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            vm.DetailTitle = "   "; // Invalid/incomplete typing must not be lost or auto-saved.
            vm.DetailNotes = "Unsaved notes";
            vm.DetailPriorityIndex = 0;
            await vm.RunTodoistSyncAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await vm.RunAutomaticTodoistSyncAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, provider.FetchCalls);
            await vm.SetProjectSortAsync(new());
            Assert.True(vm.IsBusy);
            Assert.True(vm.IsSyncing);
        }
        finally { provider.Release.TrySetResult(); await sync; }
        Assert.Equal("   ", vm.DetailTitle);
        Assert.Equal("Unsaved notes", vm.DetailNotes);
        Assert.Equal(0, vm.DetailPriorityIndex);
        Assert.Equal("task", vm.SelectedTask?.Task.Id);
        Assert.Equal("Original", (await store.GetTaskAsync("task"))!.Title);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsSyncing);
    }

    [Fact]
    public async Task Saving_a_local_edit_does_not_revert_untouched_fields_updated_by_sync()
    {
        var (store, vm, _) = await CreateAsync();
        await vm.SelectNavigationAsync(vm.NavigationItems.Single(x => x.Kind == TaskListKind.Open));
        await vm.SelectTaskAsync(Assert.Single(vm.Tasks));
        var task = (await store.GetTaskAsync("task"))!;
        var date = new DateOnly(2026, 4, 1);
        await store.UpsertTaskAsync(task with { PlannedOn = date, CompletionState = TaskCompletionState.Completed });
        vm.DetailTitle = "Local title";
        Assert.True(await vm.SaveSelectedAsync());
        var saved = (await store.GetTaskAsync("task"))!;
        Assert.Equal("Local title", saved.Title);
        Assert.Equal(date, saved.PlannedOn);
        Assert.True(saved.IsCompleted);
    }

    [Fact]
    public async Task Failed_sync_releases_guard_and_can_retry()
    {
        var (_, vm, provider) = await CreateAsync();
        provider.Release.TrySetException(new InvalidOperationException("Offline test"));
        await vm.RunTodoistSyncAsync();
        Assert.Contains("Offline test", vm.StatusMessage);
        Assert.False(vm.IsSyncing);
        await vm.RunTodoistSyncAsync();
        Assert.Equal(2, provider.FetchCalls);
    }

    [Fact]
    public async Task Incoming_snapshot_preserves_newer_pending_local_completion_and_date()
    {
        var (store, _, _) = await CreateAsync();
        var source = new ProviderSourceItem
        {
            Id = "source", IntegrationId = IntegrationIds.Todoist, ProviderConnectionId = "todoist_default",
            ExternalId = "remote", ProviderTaskId = "remote", Title = "Remote", PlannedOn = new DateOnly(2026, 1, 1),
        };
        await store.UpsertProviderSourceItemAsync(source);
        var task = (await store.AdoptProviderSourceItemAsync(source.Id))!;
        var service = new TaskApplicationService(store);
        var planned = new DateOnly(2026, 3, 1);
        await service.UpdateTaskAsync(new UpdateTaskRequest { TaskId = task.Id, PlannedOn = OptionalValue<DateOnly?>.Set(planned) });
        await service.UpdateTaskAsync(new UpdateTaskRequest { TaskId = task.Id, Completed = OptionalValue<bool>.Set(true) });
        var local = (await store.GetTaskAsync(task.Id))!;
        await store.UpsertProviderSourceItemAsync(source with { PlannedOn = new DateOnly(2026, 2, 1), CompletionState = TaskCompletionState.Open });
        var refreshed = (await store.GetTaskAsync(task.Id))!;
        Assert.True(refreshed.IsCompleted);
        Assert.Equal(local.CompletedAt, refreshed.CompletedAt);
        Assert.Equal(planned, refreshed.PlannedOn);
        Assert.NotEmpty(await store.GetPendingCompletionsAsync(IntegrationIds.Todoist));
        Assert.NotEmpty(await store.GetPendingTaskDateUpdatesAsync(IntegrationIds.Todoist));
    }

    [Fact]
    public async Task Completion_acknowledges_only_dates_queued_before_request()
    {
        var (store, _, provider) = await CreateAsync();
        await store.QueueCompletionAsync(new PendingCompletion { Id = "complete", TaskId = "task", Provider = IntegrationIds.Todoist, ProviderTaskId = "remote", Completed = true });
        var old = new PendingTaskDateUpdate { Id = "old", TaskId = "task", Provider = IntegrationIds.Todoist, ProviderTaskId = "remote", PlannedOn = new DateOnly(2026, 1, 1) };
        await store.QueueTaskDateUpdateAsync(old);
        provider.Complete = () => store.QueueTaskDateUpdateAsync(old with { Id = "new", PlannedOn = new DateOnly(2026, 2, 1) });
        await new TaskSyncEngine(store).SyncPendingCompletionsAsync(provider);
        Assert.Equal("new", Assert.Single(await store.GetPendingTaskDateUpdatesAsync(IntegrationIds.Todoist)).Id);
    }

    [Fact]
    public async Task Navigation_and_saving_finish_while_provider_is_waiting()
    {
        var (store, vm, provider) = await CreateAsync();
        var sync = vm.RunTodoistSyncAsync();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await vm.SelectNavigationAsync(vm.NavigationItems.Single(x => x.Kind == TaskListKind.Open)).WaitAsync(TimeSpan.FromSeconds(2));
            await vm.SelectTaskAsync(Assert.Single(vm.Tasks));
            vm.DetailTitle = "Edited during sync";
            Assert.True(await vm.SaveSelectedAsync().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("Edited during sync", (await store.GetTaskAsync("task"))!.Title);
        }
        finally { provider.Release.TrySetResult(); await sync; }
    }

    private async Task<(SqliteTaskStore, MainWindowViewModel, SlowProvider)> CreateAsync()
    {
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "task", Title = "Original" });
        var credentials = new InMemoryCredentialStore();
        await credentials.SaveAsync("todoist-token", "test-token");
        var provider = new SlowProvider();
        return (store, new MainWindowViewModel(store, credentials, todoistSyncProviderFactory: _ => provider), provider);
    }

    private sealed class SlowProvider : ISyncProvider
    {
        public int FetchCalls { get; private set; }
        public Func<Task> Complete { get; set; } = () => Task.CompletedTask;
        public string IntegrationId => IntegrationIds.Todoist;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
        {
            FetchCalls++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new([], [], []);
        }
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) => Complete();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
