using Microsoft.Data.Sqlite;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudOrdinaryTaskActionsTests : IDisposable
{
    private const string InstanceId = "instance_00000000000000000000000000000001";
    private const string DeviceId = "device_00000000000000000000000000000001";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "openza-personal-cloud-ordinary-actions", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Connected_ordinary_task_actions_are_durable_cloud_intents()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        var connection = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
            InstanceId = InstanceId,
            DeviceId = DeviceId,
            ArtifactVersion = "1.2.3",
            ConnectedAt = DateTimeOffset.UtcNow,
        };
        var service = new TaskApplicationService(store, () => connection);

        var created = await service.CreateTaskAsync(new CreateTaskRequest { Title = "Ordinary action" });
        Assert.Equal(2, await store.GetPersonalCloudEntityVersionAsync(InstanceId, created.Id));

        var updated = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.Id,
            ExpectedRevision = created.Revision,
            Title = OptionalValue<string?>.Set("Renamed"),
            Status = OptionalValue<TaskWorkflowStatus>.Set(TaskWorkflowStatus.Next),
            Priority = OptionalValue<int>.Set(4),
            PlannedOn = OptionalValue<DateOnly?>.Set(new DateOnly(2026, 9, 20)),
        });
        Assert.Equal("Renamed", updated.Title);
        Assert.Equal(TaskWorkflowStatus.Next, updated.WorkflowStatus);
        Assert.Equal(4, updated.Priority);
        Assert.Equal(new DateOnly(2026, 9, 20), updated.PlannedOn);
        Assert.Equal(6, await store.GetPersonalCloudEntityVersionAsync(InstanceId, created.Id));

        var completed = await service.SetCompletedAsync(created.Id, true, updated.Revision);
        Assert.True(completed.IsCompleted);
        Assert.Equal(7, await store.GetPersonalCloudEntityVersionAsync(InstanceId, created.Id));

        var reopened = await service.SetCompletedAsync(created.Id, false, completed.Revision);
        Assert.False(reopened.IsCompleted);
        Assert.Equal(8, await store.GetPersonalCloudEntityVersionAsync(InstanceId, created.Id));

        await service.DeleteTaskAsync(created.Id, reopened.Revision);
        Assert.Null(await store.GetTaskAsync(created.Id));
        Assert.Equal(9, await store.GetPersonalCloudEntityVersionAsync(InstanceId, created.Id));

        var pending = await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId);
        Assert.Equal(
            [
                "task.create",
                "task.updateOpenzaFields",
                "task.updateOpenzaFields",
                "task.updateOpenzaFields",
                "task.updateOpenzaFields",
                "task.updateOpenzaFields",
                "task.complete",
                "task.reopen",
                "task.delete",
            ],
            pending.Select(item => item.Operation));
        Assert.Equal(Enumerable.Range(1, 9).Select(value => (long)value), pending.Select(item => item.DeviceSequence));
    }

    [Fact]
    public async Task Reopen_and_replan_in_one_save_orders_reopen_before_planning()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "reopen-and-replan.db"));
        await store.InitializeAsync();
        var connection = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
            InstanceId = InstanceId,
            DeviceId = DeviceId,
            ArtifactVersion = "1.2.3",
            ConnectedAt = DateTimeOffset.UtcNow,
        };
        var service = new TaskApplicationService(store, () => connection);
        var created = await service.CreateTaskAsync(new CreateTaskRequest { Title = "Completed task" });
        var completed = await service.SetCompletedAsync(created.Id, true, created.Revision);

        var reopened = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.Id,
            ExpectedRevision = completed.Revision,
            Completed = OptionalValue<bool>.Set(false),
            PlannedOn = OptionalValue<DateOnly?>.Set(new DateOnly(2026, 9, 21)),
        });

        Assert.False(reopened.IsCompleted);
        Assert.Equal(new DateOnly(2026, 9, 21), reopened.PlannedOn);
        var pending = await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId);
        Assert.Equal(
            ["task.create", "task.updateOpenzaFields", "task.complete", "task.reopen", "task.updateOpenzaFields"],
            pending.Select(item => item.Operation));
        Assert.Equal(5, await store.GetPersonalCloudEntityVersionAsync(InstanceId, created.Id));
    }

    [Fact]
    public async Task Connected_detail_save_queues_project_and_label_changes()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "project-and-labels.db"));
        await store.InitializeAsync();
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_launch",
            SpaceId = SpaceIds.Default,
            IntegrationId = IntegrationIds.Local,
            Name = "Launch",
        });
        await store.UpsertLabelAsync(new LabelItem { Id = "label_focus", Name = "Focus" });
        await store.UpsertLabelAsync(new LabelItem { Id = "label_review", Name = "Review" });
        var connection = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
            InstanceId = InstanceId,
            DeviceId = DeviceId,
            ArtifactVersion = "1.2.3",
            ConnectedAt = DateTimeOffset.UtcNow,
        };
        var service = new TaskApplicationService(store, () => connection);
        var created = await service.CreateTaskAsync(new CreateTaskRequest { Title = "Related task" });

        var related = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.Id,
            ExpectedRevision = created.Revision,
            Project = OptionalValue<string?>.Set("project_launch"),
            Labels = OptionalValue<IReadOnlyList<string>>.Set(["label_focus"]),
        });
        var relabeled = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.Id,
            ExpectedRevision = related.Revision,
            Labels = OptionalValue<IReadOnlyList<string>>.Set(["label_review"]),
        });
        var cleared = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.Id,
            ExpectedRevision = relabeled.Revision,
            Project = OptionalValue<string?>.Set(null),
        });

        Assert.Null(cleared.ProjectId);
        Assert.Equal(["label_review"], cleared.Labels.Select(label => label.Id));
        var pending = await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId);
        Assert.Equal(
            ["task.create", "task.updateOpenzaFields", "task.setProject", "task.addLabel", "task.removeLabel", "task.addLabel", "task.setProject"],
            pending.Select(item => item.Operation));
        Assert.Contains("\"project\":null", pending[^1].CanonicalJson, StringComparison.Ordinal);
        Assert.Equal(7, await store.GetPersonalCloudEntityVersionAsync(InstanceId, created.Id));
    }

    [Fact]
    public async Task Connected_detail_save_queues_notes_and_deadline_changes()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "notes-and-deadline.db"));
        await store.InitializeAsync();
        var connection = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
            InstanceId = InstanceId,
            DeviceId = DeviceId,
            ArtifactVersion = "1.2.3",
            ConnectedAt = DateTimeOffset.UtcNow,
        };
        var service = new TaskApplicationService(store, () => connection);
        var created = await service.CreateTaskAsync(new CreateTaskRequest { Title = "Document the release" });

        var updated = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.Id,
            ExpectedRevision = created.Revision,
            Notes = OptionalValue<string?>.Set("Include migration 17."),
            DeadlineOn = OptionalValue<DateOnly?>.Set(new DateOnly(2026, 9, 22)),
        });

        Assert.Equal("Include migration 17.", updated.Notes);
        Assert.Equal(new DateOnly(2026, 9, 22), updated.DeadlineOn);
        var pending = await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId);
        Assert.Equal(["task.create", "task.updateOpenzaFields", "task.updateOpenzaFields", "task.updateOpenzaFields"], pending.Select(item => item.Operation));
        Assert.Contains("\"notes\":\"Include migration 17.\"", pending[2].CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"deadlineOn\":\"2026-09-22\"", pending[3].CanonicalJson, StringComparison.Ordinal);
        Assert.Equal(4, await store.GetPersonalCloudEntityVersionAsync(InstanceId, created.Id));
    }

    [Fact]
    public async Task Connected_space_move_keeps_the_existing_provider_subtask_tree_together()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "space-move-tree.db"));
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new SpaceItem
        {
            Id = "space_personal",
            Name = "Personal",
        });
        var connection = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
            InstanceId = InstanceId,
            DeviceId = DeviceId,
            ArtifactVersion = "1.2.3",
            ConnectedAt = DateTimeOffset.UtcNow,
        };
        var service = new TaskApplicationService(store, () => connection);
        var parent = new TaskItem { Id = "task_parent", Title = "Parent", SpaceId = SpaceIds.Default };
        var child = new TaskItem
        {
            Id = "task_child",
            Title = "Provider child",
            SpaceId = SpaceIds.Default,
            ParentId = parent.Id,
            SourceIntegrationId = IntegrationIds.Todoist,
        };
        var grandchild = new TaskItem
        {
            Id = "task_grandchild",
            Title = "Existing nested child",
            SpaceId = SpaceIds.Default,
            ParentId = child.Id,
            SourceIntegrationId = IntegrationIds.Todoist,
        };
        await store.CreateTaskWithPersonalCloudIntentAsync(parent, InstanceId, DeviceId);
        await store.CreateTaskWithPersonalCloudIntentAsync(child, InstanceId, DeviceId);
        await store.CreateTaskWithPersonalCloudIntentAsync(grandchild, InstanceId, DeviceId);

        var moved = await service.MoveTaskToSpaceAsync(parent.Id, "space_personal", parent.Revision);

        Assert.Equal("space_personal", moved.SpaceId);
        var movedChild = Assert.IsType<TaskItem>(await store.GetTaskAsync(child.Id));
        var movedGrandchild = Assert.IsType<TaskItem>(await store.GetTaskAsync(grandchild.Id));
        Assert.Equal("space_personal", movedChild.SpaceId);
        Assert.Equal("space_personal", movedGrandchild.SpaceId);
        Assert.Equal(parent.Id, movedChild.ParentId);
        Assert.Equal(child.Id, movedGrandchild.ParentId);
        var pending = await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId);
        Assert.Equal(
            ["task.create", "task.create", "task.create", "task.move", "task.move", "task.setParent", "task.move", "task.setParent"],
            pending.Select(item => item.Operation));
        Assert.Equal(Enumerable.Range(1, 8).Select(value => (long)value), pending.Select(item => item.DeviceSequence));
        Assert.Contains("\"parentId\":\"task_parent\"", pending[5].CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"parentId\":\"task_child\"", pending[7].CanonicalJson, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TestDirectory.Delete(_directory);
    }
}
