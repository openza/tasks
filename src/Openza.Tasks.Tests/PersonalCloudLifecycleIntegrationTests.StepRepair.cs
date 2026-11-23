using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudLifecycleIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Step_batch_rechecks_connection_after_repair_between_tasks_or_before_staging(bool pairBeforeSecondTask)
    {
        using var f = await Fixture.CreateAsync();
        var original = f.Service.CurrentConnection!;
        var sources = new List<ProviderSourceItem>();
        var parents = new List<TaskItem>();
        for (var index = 0; index < 2; index++)
        {
            var source = new ProviderSourceItem { Id = $"repair_steps_{index}", ExternalId = $"repair_steps_{index}",
                IntegrationId = IntegrationIds.MicrosoftToDo, ProviderConnectionId = "mstodo_default", Title = $"Parent {index}", SnapshotJson = "{}" };
            await f.Tasks.UpsertProviderSourceItemAsync(source);
            parents.Add((await f.Tasks.AdoptProviderSourceItemAsync(source.Id))!);
            sources.Add(source with { SnapshotJson = """{"msToDo":{"checklistItems":[{"displayName":"First"},{"displayName":"Second","isChecked":true}]}}""" });
        }
        await f.Service.DisconnectAsync();
        f.Tasks.PersonalCloudConnection = () => f.Service.CurrentConnection;
        await using (var batch = f.Tasks.BeginMicrosoftStepSyncBatch(CancellationToken.None))
        {
            await batch.UpsertAsync(sources[0]);
            Assert.Empty(await f.Tasks.GetPendingPersonalCloudOutboxAsync(original.InstanceId, original.DeviceId));
            if (pairBeforeSecondTask) await f.Service.PairExistingAsync(Fixture.Origin, "synthetic-pair-code-1234567890", "Paired again", false);
            await batch.UpsertAsync(sources[1]);
            if (!pairBeforeSecondTask) await f.Service.PairExistingAsync(Fixture.Origin, "synthetic-pair-code-1234567890", "Paired again", false);
            await batch.StageAsync();
        }
        var outbox = await f.Tasks.GetPendingPersonalCloudOutboxAsync(original.InstanceId, original.DeviceId);
        foreach (var parent in parents)
        {
            var children = await f.Tasks.GetTasksAsync(new() { ParentId = parent.Id, IncludeSubtasks = true });
            Assert.Equal(2, children.Count);
            Assert.All(children, child =>
            {
                Assert.Contains(outbox, entry => entry.EntityId == child.Id && entry.Operation == "task.create");
                Assert.Contains(outbox, entry => entry.EntityId == child.Id && entry.Operation == "task.setParent");
            });
        }
    }
}
