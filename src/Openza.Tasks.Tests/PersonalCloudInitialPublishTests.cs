using System.Text.Json;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudInitialPublishTests : IDisposable
{
    private const string InstanceId = "instance_publish_00000000000000000000000001";
    private const string DeviceId = "device_publish_000000000000000000000000001";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "openza-personal-cloud-publish-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Initial_publish_stages_rich_tasks_in_dependency_order_without_changing_local_rows()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        var space = new SpaceItem { Id = "space_personal", Name = "Personal" };
        var project = new ProjectItem { Id = "project_launch", SpaceId = space.Id, Name = "Launch" };
        var label = new LabelItem { Id = "label_focus", Name = "Focus" };
        await store.UpsertSpaceAsync(space);
        await store.UpsertProjectAsync(project);
        await store.UpsertLabelAsync(label);
        var parent = new TaskItem
        {
            Id = "local_parent_000000000000000000000000001",
            SpaceId = space.Id,
            Title = "Parent",
            CreatedAt = DateTimeOffset.Parse("2026-09-14T05:00:00Z"),
        };
        var child = new TaskItem
        {
            Id = "local_child_0000000000000000000000000001",
            SpaceId = space.Id,
            ProjectId = project.Id,
            ParentId = parent.Id,
            Title = "Rich child",
            WorkflowStatus = TaskWorkflowStatus.Waiting,
            Priority = 4,
            PlannedOn = new DateOnly(2026, 9, 20),
            Notes = "Publish existing notes",
            DeadlineOn = new DateOnly(2026, 9, 25),
            CompletionState = TaskCompletionState.Completed,
            CompletedAt = DateTimeOffset.Parse("2026-09-14T06:00:00Z"),
            CreatedAt = DateTimeOffset.Parse("2026-09-14T05:01:00Z"),
            Labels = [label],
        };
        await store.UpsertTaskAsync(parent);
        await store.UpsertTaskAsync(child);

        var staged = await store.StageUnpublishedTasksForPersonalCloudAsync(InstanceId, DeviceId);

        var outbox = await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId);
        Assert.Equal(13, staged);
        Assert.Equal(13, outbox.Count);
        Assert.All(outbox.Take(2), entry => Assert.Equal("task.create", entry.Operation));
        Assert.All(outbox.Skip(2).Take(2), entry => Assert.Equal("task.move", entry.Operation));
        Assert.Equal("task.setParent", outbox[^2].Operation);
        Assert.Equal("task.complete", outbox[^1].Operation);
        Assert.Equal(Enumerable.Range(1, outbox.Count).Select(value => (long)value), outbox.Select(entry => entry.DeviceSequence));

        var versions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in outbox)
        {
            using var document = JsonDocument.Parse(entry.CanonicalJson);
            var root = document.RootElement;
            Assert.Equal(entry.DeviceSequence, root.GetProperty("deviceSequence").GetInt64());
            if (entry.Operation == "task.create")
            {
                Assert.Equal(SpaceIds.Default, root.GetProperty("payload").GetProperty("task").GetProperty("spaceId").GetString());
                versions[entry.EntityId] = 1;
            }
            else
            {
                Assert.Equal(versions[entry.EntityId], root.GetProperty("baseEntityVersion").GetInt64());
                versions[entry.EntityId]++;
            }
        }

        var unchanged = Assert.IsType<TaskItem>(await store.GetTaskAsync(child.Id));
        Assert.Equal(space.Id, unchanged.SpaceId);
        Assert.Equal(project.Id, unchanged.ProjectId);
        Assert.Equal(parent.Id, unchanged.ParentId);
        Assert.Equal(TaskWorkflowStatus.Waiting, unchanged.WorkflowStatus);
        Assert.Equal(TaskCompletionState.Completed, unchanged.CompletionState);
        Assert.Equal("Publish existing notes", unchanged.Notes);
        Assert.Equal(new DateOnly(2026, 9, 25), unchanged.DeadlineOn);
        Assert.Equal([label.Id], unchanged.Labels.Select(item => item.Id));
        Assert.Equal(0, await store.StageUnpublishedTasksForPersonalCloudAsync(InstanceId, DeviceId));
        Assert.Equal(13, (await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId)).Count);
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
