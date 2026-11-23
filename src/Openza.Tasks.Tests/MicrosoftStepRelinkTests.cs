using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class MicrosoftStepRelinkTests
{
    [Fact]
    public async Task Missing_source_relink_preserves_step_marker_and_user_child_edits_without_duplicates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "openza-step-relink", Guid.NewGuid().ToString("N"));
        var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
        try
        {
            await store.InitializeAsync();
            const string checklist = """{"msToDo":{"checklistItems":[{"displayName":"First","isChecked":false},{"displayName":"Second","isChecked":false},{"displayName":"Third","isChecked":true}]}}""";
            var source = new ProviderSourceItem { Id = "source", ExternalId = "external", IntegrationId = IntegrationIds.MicrosoftToDo,
                ProviderConnectionId = "mstodo_default", Title = "Parent", SnapshotJson = checklist };
            await store.UpsertProviderSourceItemAsync(source);
            var parent = (await store.AdoptProviderSourceItemAsync(source.Id))!;
            Task<IReadOnlyList<TaskItem>> Children() => store.GetTasksAsync(new() { ParentId = parent.Id, IncludeSubtasks = true });
            var children = await Children();
            var deleted = children.Single(child => child.Title == "First");
            var renamed = children.Single(child => child.Title == "Second");
            await store.DeleteTaskAsync(deleted.Id);
            await store.UpsertTaskAsync(renamed with { Title = "User renamed", CompletionState = TaskCompletionState.Completed });
            var engine = new TaskSyncEngine(store);
            var returning = new TaskItem { Id = "remote", ExternalId = source.ExternalId, IntegrationId = IntegrationIds.MicrosoftToDo,
                Title = source.Title, ProviderMetadataJson = checklist };
            for (var index = 0; index < 2; index++)
            {
                Assert.True((await engine.SyncAsync(new SnapshotProvider([]))).Success);
                var detached = (await store.GetTaskAsync(parent.Id))!;
                Assert.Null(detached.SourceIntegrationId);
                Assert.Null(detached.SourceExternalId);
                Assert.Null(detached.SourceRecurrenceRule);
                Assert.True((await engine.SyncAsync(new SnapshotProvider([returning]))).Success);
                var remaining = await Children();
                Assert.Equal(2, remaining.Count);
                Assert.DoesNotContain(remaining, child => child.Id == deleted.Id || child.Title == "First");
                Assert.Contains(remaining, child => child.Id == renamed.Id && child.Title == "User renamed" && child.IsCompleted);
                Assert.Equal(IntegrationIds.MicrosoftToDo, (await store.GetTaskAsync(parent.Id))!.SourceIntegrationId);
                Assert.Contains("\"msToDoStepsApplied\":true", (await store.GetTaskAsync(parent.Id))!.SourceMetadataJson);
            }
        }
        finally { TestDirectory.Delete(directory); }
    }

    private sealed class SnapshotProvider(IReadOnlyList<TaskItem> tasks) : ISyncProvider
    {
        public string IntegrationId => IntegrationIds.MicrosoftToDo;
        public string ProviderConnectionId => "mstodo_default";
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderSnapshot(tasks, [], []));
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
