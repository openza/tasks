using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudSourceLinkedOpenzaTaskTests : IDisposable
{
    private const string InstanceId = "instance_source_linked_000000000000001";
    private const string DeviceId = "device_source_linked_000000000000001";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "openza-personal-cloud-source-linked-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Adopted_row_uses_the_normal_Openza_task_flow_and_queues_provider_write_back()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_todoist_cloud_1",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "todoist_private_external_id",
            ProviderTaskId = "todoist_private_command_id",
            Title = "Provider-owned title",
            Description = "Safe source description",
            SourceProjectName = "Provider Inbox",
            SnapshotJson = "{\"accessToken\":\"never-upload-this\"}",
            CompletionState = TaskCompletionState.Open,
        });

        var adopted = await store.AdoptProviderSourceItemAsync("source_todoist_cloud_1");
        Assert.NotNull(adopted);
        Assert.Equal(IntegrationIds.Local, adopted.IntegrationId);
        Assert.True(adopted.HasProviderSource);

        var create = await store.EnqueueExistingTaskForPersonalCloudAsync(
            adopted.Id, InstanceId, DeviceId);
        var mutation = PersonalCloudK0Codec.ParseTaskCreateMutation(create.CanonicalJson, InstanceId);
        Assert.Equal(adopted.Id, mutation.Payload.Task.Id);
        Assert.Equal(IntegrationIds.Todoist, mutation.Payload.Source?.IntegrationKind);
        Assert.Equal(64, mutation.Payload.Source?.ExternalIdHash.Length);
        Assert.DoesNotContain("todoist_default", create.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("todoist_private_external_id", create.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("todoist_private_command_id", create.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("never-upload-this", create.CanonicalJson, StringComparison.Ordinal);

        var titleError = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            store.UpdateTaskTitleWithPersonalCloudIntentAsync(
                adopted.Id, "Cloud title", 1, InstanceId, DeviceId));
        Assert.Equal("invalid_request", titleError.Code);

        await store.UpdateTaskWorkflowStatusWithPersonalCloudIntentAsync(
            adopted.Id, TaskWorkflowStatus.Next, 1, InstanceId, DeviceId);
        await store.UpdateTaskPriorityWithPersonalCloudIntentAsync(
            adopted.Id, 1, 2, InstanceId, DeviceId);
        await store.UpdateTaskPlannedOnWithPersonalCloudIntentAsync(
            adopted.Id, new DateOnly(2026, 9, 20), 3, InstanceId, DeviceId);

        var pendingDate = Assert.Single(await store.GetPendingTaskDateUpdatesAsync(IntegrationIds.Todoist));
        Assert.Equal("todoist_private_command_id", pendingDate.ProviderTaskId);
        Assert.Equal(new DateOnly(2026, 9, 20), pendingDate.PlannedOn);

        await store.CompleteTaskWithPersonalCloudIntentAsync(
            adopted.Id, DateTimeOffset.Parse("2026-09-13T12:00:00Z"), 4, InstanceId, DeviceId);
        await store.ReopenTaskWithPersonalCloudIntentAsync(
            adopted.Id, DateTimeOffset.Parse("2026-09-13T12:01:00Z"), 5, InstanceId, DeviceId);

        var pendingCompletions = await store.GetPendingCompletionsAsync(IntegrationIds.Todoist);
        Assert.Equal(2, pendingCompletions.Count);
        Assert.All(pendingCompletions, pending => Assert.Equal("todoist_private_command_id", pending.ProviderTaskId));
        Assert.True(pendingCompletions[0].Completed);
        Assert.False(pendingCompletions[1].Completed);
        Assert.Equal(6, (await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId)).Count);

        var provider = new RecordingTodoistProvider();
        var engine = new TaskSyncEngine(store);
        Assert.Equal(1, await engine.SyncPendingTaskDateUpdatesAsync(provider));
        Assert.Equal(2, await engine.SyncPendingCompletionsAsync(provider));
        Assert.Equal(new DateOnly(2026, 9, 20), Assert.Single(provider.DateUpdates).PlannedOn);
        Assert.Equal([true, false], provider.Completions.Select(item => item.Completed).ToArray());
        Assert.Empty(await store.GetPendingTaskDateUpdatesAsync(IntegrationIds.Todoist));
        Assert.Empty(await store.GetPendingCompletionsAsync(IntegrationIds.Todoist));
    }

    [Fact]
    public async Task Duplicate_adopted_task_create_is_rejected_without_advancing_the_device_sequence()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_todoist_cloud_2",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "todoist_private_external_id_2",
            ProviderTaskId = "todoist_private_command_id_2",
            Title = "Second provider task",
        });
        var adopted = await store.AdoptProviderSourceItemAsync("source_todoist_cloud_2");
        Assert.NotNull(adopted);

        await store.EnqueueExistingTaskForPersonalCloudAsync(adopted.Id, InstanceId, DeviceId);
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            store.EnqueueExistingTaskForPersonalCloudAsync(adopted.Id, InstanceId, DeviceId));

        Assert.Equal("entity_exists", error.Code);
        var onlyEntry = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId));
        Assert.Equal(1, onlyEntry.DeviceSequence);
    }

    [Fact]
    public async Task Replica_applies_source_refresh_and_command_result_without_inventing_writable_provider_identity()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        const string taskId = "local_source_replica_000000000000000001";
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = taskId,
            IntegrationId = IntegrationIds.Local,
            Title = "Source-linked replica",
            SourceIntegrationId = IntegrationIds.Todoist,
            SourceDescription = "Old safe description",
            CreatedAt = DateTimeOffset.Parse("2026-09-13T09:00:00Z"),
        });
        var descriptor = new K0SafeSourceDescriptor
        {
            IntegrationKind = IntegrationIds.Todoist,
            ExternalIdHash = new string('a', 64),
            SourceUrl = "https://todoist.com/app/task/opaque",
            Title = "Refreshed source title",
            Description = "Refreshed safe description",
            ProjectName = "Operations",
            Priority = 2,
            PlannedOn = null,
            PlannedAt = null,
            DeadlineOn = null,
            DeadlineAt = null,
            RecurrenceRule = null,
            RefreshedAt = "2026-09-13T09:05:00.000Z",
        };
        var page = PersonalCloudK0Codec.Serialize(new
        {
            protocolVersion = 1,
            product = "tasks",
            changes = new object[]
            {
                new
                {
                    protocolVersion = 1, product = "tasks", changeSeq = 1,
                    mutationId = "mutation_source_refresh_1", operation = "task.updatedOpenzaFields",
                    entityKind = "task", entityId = taskId, entityVersion = 1,
                    payload = new { task = new { id = taskId, source = descriptor } },
                    serverAcceptedAt = "2026-09-13T09:05:01.000Z",
                },
                new
                {
                    protocolVersion = 1, product = "tasks", changeSeq = 2,
                    mutationId = "mutation_external_confirm_1", operation = "task.updatedOpenzaFields",
                    entityKind = "task", entityId = taskId, entityVersion = 1,
                    payload = new { task = new { id = taskId, externalCommand = new
                    {
                        command = "complete", state = "confirmed", errorCode = (string?)null,
                    } } },
                    serverAcceptedAt = "2026-09-13T09:05:02.000Z",
                },
            },
            nextCheckpoint = 2,
        });

        Assert.Equal(2, await store.ApplyPersonalCloudChangePageAsync(InstanceId, DeviceId, page));
        var task = await store.GetTaskAsync(taskId);
        Assert.Equal("Refreshed safe description", task?.SourceDescription);
        Assert.Contains("Refreshed source title", task?.SourceMetadataJson, StringComparison.Ordinal);
        Assert.Null(task?.SourceExternalId);
        Assert.Null(task?.SourceProviderTaskId);
        Assert.Empty(await store.GetPendingCompletionsAsync(IntegrationIds.Todoist));
    }

    private SqliteTaskStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new SqliteTaskStore(Path.Combine(_directory, $"{Guid.NewGuid():N}.db"));
    }

    private sealed class RecordingTodoistProvider : ITaskDateUpdateProvider
    {
        public string IntegrationId => IntegrationIds.Todoist;
        public List<PendingTaskDateUpdate> DateUpdates { get; } = [];
        public List<PendingCompletion> Completions { get; } = [];

        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderSnapshot([], [], []));

        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default)
        {
            Completions.Add(completion);
            return Task.CompletedTask;
        }

        public Task UpdateTaskDateAsync(PendingTaskDateUpdate update, CancellationToken cancellationToken = default)
        {
            DateUpdates.Add(update);
            return Task.CompletedTask;
        }
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
