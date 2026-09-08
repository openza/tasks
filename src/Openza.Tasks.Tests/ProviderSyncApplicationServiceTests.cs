using Openza.Tasks.Application.Sync;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class ProviderSyncApplicationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "openza-provider-sync-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Preflight_is_read_only_and_reports_connection_and_pending_counts()
    {
        var store = await CreateReadyStoreAsync();
        await QueuePendingWritesAsync(store);
        var credentials = new InMemoryCredentialStore();
        await credentials.SaveAsync(ProviderCredentialKeys.TodoistToken, "test-token");
        var providerCreated = false;
        var service = new ProviderSyncApplicationService(store, credentials, (_, _) =>
        {
            providerCreated = true;
            return new FakeProvider();
        });

        var preflight = await service.GetPreflightAsync("todoist", "push", "pending");

        Assert.True(preflight.Ready);
        Assert.Equal(1, preflight.Pending.Completions);
        Assert.Equal(1, preflight.Pending.Reopens);
        Assert.Equal(1, preflight.Pending.DateUpdates);
        Assert.Equal(3, preflight.Pending.Total);
        Assert.False(providerCreated);
    }

    [Fact]
    public async Task Push_sends_dates_before_completions_and_clears_only_successful_rows()
    {
        var store = await CreateReadyStoreAsync();
        await QueuePendingWritesAsync(store);
        var credentials = new InMemoryCredentialStore();
        await credentials.SaveAsync(ProviderCredentialKeys.TodoistToken, "test-token");
        var provider = new FakeProvider();
        var service = new ProviderSyncApplicationService(store, credentials, (_, _) => provider);

        var result = await service.PushPendingAsync("todoist", "push", "pending");

        Assert.True(result.Success);
        Assert.Equal(["date", "completion", "reopen"], provider.Calls);
        Assert.Equal(3, result.Applied.Total);
        Assert.Equal(0, result.Remaining.Total);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_push_succeeds_without_credentials_or_provider_calls(bool ready)
    {
        var store = ready ? await CreateReadyStoreAsync() : await CreateStoreAsync();
        var providerCreated = false;
        var service = new ProviderSyncApplicationService(store, new InMemoryCredentialStore(), (_, _) =>
        {
            providerCreated = true;
            return new FakeProvider();
        });

        var result = await service.PushPendingAsync("todoist", "push", "pending");

        Assert.True(result.Success);
        Assert.Equal(0, result.Planned.Total);
        Assert.Equal(0, result.Applied.Total);
        Assert.Equal(0, result.Remaining.Total);
        Assert.False(providerCreated);
    }

    [Theory]
    [InlineData("date", 0, 1, 1, 1)]
    [InlineData("completion", 1, 0, 1, 1)]
    [InlineData("reopen", 2, 0, 0, 1)]
    public async Task Push_failure_keeps_failed_and_unattempted_rows_for_retry(
        string failOn,
        int applied,
        int remainingDates,
        int remainingCompletions,
        int remainingReopens)
    {
        var store = await CreateReadyStoreAsync();
        await QueuePendingWritesAsync(store);
        var credentials = new InMemoryCredentialStore();
        await credentials.SaveAsync(ProviderCredentialKeys.TodoistToken, "test-token");
        var provider = new FakeProvider { FailOn = failOn };
        var service = new ProviderSyncApplicationService(store, credentials, (_, _) => provider);

        var result = await service.PushPendingAsync("todoist", "push", "pending");

        Assert.False(result.Success);
        Assert.Equal(applied, result.Applied.Total);
        Assert.Equal(remainingDates, result.Remaining.DateUpdates);
        Assert.Equal(remainingCompletions, result.Remaining.Completions);
        Assert.Equal(remainingReopens, result.Remaining.Reopens);
    }

    [Theory]
    [InlineData("mstodo", "push", "pending")]
    [InlineData("todoist", "pull", "pending")]
    [InlineData("todoist", "push", "all")]
    public async Task Unsupported_contract_values_are_rejected_without_provider_calls(
        string provider,
        string direction,
        string scope)
    {
        var store = await CreateReadyStoreAsync();
        var credentials = new InMemoryCredentialStore();
        var providerCreated = false;
        var service = new ProviderSyncApplicationService(store, credentials, (_, _) =>
        {
            providerCreated = true;
            return new FakeProvider();
        });

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetPreflightAsync(provider, direction, scope));
        Assert.False(providerCreated);
    }

    private async Task<SqliteTaskStore> CreateReadyStoreAsync()
    {
        var store = await CreateStoreAsync();
        await store.SetIntegrationConfiguredAsync(IntegrationIds.Todoist, true);
        await store.SetIntegrationActiveAsync(IntegrationIds.Todoist, true);
        return store;
    }

    private async Task<SqliteTaskStore> CreateStoreAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        return store;
    }

    private static async Task QueuePendingWritesAsync(SqliteTaskStore store)
    {
        var now = DateTimeOffset.UtcNow;
        await store.QueueTaskDateUpdateAsync(new PendingTaskDateUpdate
        {
            Id = "date-1",
            TaskId = "task-date",
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote-date",
            PlannedOn = new DateOnly(2026, 8, 16),
            CreatedAt = now.AddMinutes(-3),
        });
        await store.QueueCompletionAsync(new PendingCompletion
        {
            Id = "completion-1",
            TaskId = "task-complete",
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote-complete",
            Completed = true,
            CreatedAt = now.AddMinutes(-2),
        });
        await store.QueueCompletionAsync(new PendingCompletion
        {
            Id = "reopen-1",
            TaskId = "task-reopen",
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote-reopen",
            Completed = false,
            CreatedAt = now.AddMinutes(-1),
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeProvider : ITaskDateUpdateProvider
    {
        public string IntegrationId => IntegrationIds.Todoist;
        public List<string> Calls { get; } = [];
        public string? FailOn { get; init; }

        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Push-only sync must not fetch a snapshot.");

        public Task UpdateTaskDateAsync(PendingTaskDateUpdate update, CancellationToken cancellationToken = default)
        {
            Calls.Add("date");
            if (FailOn == "date")
            {
                throw new HttpRequestException("simulated provider failure");
            }
            return Task.CompletedTask;
        }

        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default)
        {
            Calls.Add(completion.Completed ? "completion" : "reopen");
            if (FailOn == (completion.Completed ? "completion" : "reopen"))
            {
                throw new HttpRequestException("simulated provider failure");
            }
            return Task.CompletedTask;
        }
    }
}
