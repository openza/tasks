using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task Task_cli_supports_subtasks_time_blocks_and_additive_json_fields()
    {
        var store = await CreateStoreAsync();
        var parent = await RunAsync("task", "add", "Parent", "--format", "json");
        AssertSuccess(parent);
        using var parentCreated = JsonDocument.Parse(parent.Stdout);
        var parentId = parentCreated.RootElement.GetProperty("data").GetProperty("id").GetString()!;

        var otherParent = await RunAsync("task", "add", "Other parent", "--format", "json");
        AssertSuccess(otherParent);
        using var otherParentCreated = JsonDocument.Parse(otherParent.Stdout);
        var otherParentId = otherParentCreated.RootElement.GetProperty("data").GetProperty("id").GetString()!;

        var created = await RunAsync("task", "add", "Child", "--parent", parentId,
            "--start", "2030-01-02T09:30:00Z", "--duration", "45", "--format", "json");
        AssertSuccess(created);
        using var childCreated = JsonDocument.Parse(created.Stdout);
        var child = childCreated.RootElement.GetProperty("data");
        var childId = child.GetProperty("id").GetString()!;
        var staleRevision = child.GetProperty("revision").GetInt64();
        Assert.Equal(parentId, child.GetProperty("parentId").GetString());
        Assert.Equal(0, child.GetProperty("childCount").GetInt32());
        Assert.Equal(new DateTimeOffset(2030, 1, 2, 9, 30, 0, TimeSpan.Zero), child.GetProperty("scheduledStart").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2030, 1, 2, 10, 15, 0, TimeSpan.Zero), child.GetProperty("scheduledEnd").GetDateTimeOffset());
        Assert.Equal(45, child.GetProperty("durationMinutes").GetInt32());

        var otherSpace = await RunAsync("space", "create", "Other space", "--format", "json");
        AssertSuccess(otherSpace);
        using var otherSpaceJson = JsonDocument.Parse(otherSpace.Stdout);
        var otherSpaceId = otherSpaceJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        AssertCliError(await RunAsync("task", "add", "Cross-space child", "--parent", parentId,
            "--space", otherSpaceId, "--format", "json"), 2, "invalid_arguments");

        var parentShow = await RunAsync("task", "show", parentId, "--format", "json");
        AssertSuccess(parentShow);
        using var parentShown = JsonDocument.Parse(parentShow.Stdout);
        Assert.Equal(1, parentShown.RootElement.GetProperty("data").GetProperty("childCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, parentShown.RootElement.GetProperty("data").GetProperty("parentId").ValueKind);

        var moved = await RunAsync("task", "update", childId, "--parent", otherParentId, "--format", "json");
        AssertSuccess(moved);
        using var movedJson = JsonDocument.Parse(moved.Stdout);
        Assert.Equal(otherParentId, movedJson.RootElement.GetProperty("data").GetProperty("parentId").GetString());

        var rescheduled = await RunAsync("task", "update", childId, "--start", "2030-01-03T11:00:00Z", "--duration", "30", "--format", "json");
        AssertSuccess(rescheduled);
        using var rescheduledJson = JsonDocument.Parse(rescheduled.Stdout);
        var rescheduledTask = rescheduledJson.RootElement.GetProperty("data");
        Assert.Equal(new DateTimeOffset(2030, 1, 3, 11, 0, 0, TimeSpan.Zero), rescheduledTask.GetProperty("scheduledStart").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2030, 1, 3, 11, 30, 0, TimeSpan.Zero), rescheduledTask.GetProperty("scheduledEnd").GetDateTimeOffset());
        Assert.Equal(30, rescheduledTask.GetProperty("durationMinutes").GetInt32());

        var movedBack = await RunAsync("task", "update", childId, "--parent", parentId, "--format", "json");
        AssertSuccess(movedBack);
        using var movedBackJson = JsonDocument.Parse(movedBack.Stdout);
        Assert.Equal(parentId, movedBackJson.RootElement.GetProperty("data").GetProperty("parentId").GetString());

        AssertCliError(await RunAsync("task", "update", parentId, "--parent", childId, "--format", "json"), 2, "invalid_arguments");
        var cleared = await RunAsync("task", "update", childId, "--clear-parent", "--clear-schedule",
            "--revision", movedBackJson.RootElement.GetProperty("data").GetProperty("revision").GetInt64().ToString(CultureInfo.InvariantCulture),
            "--format", "json");
        AssertSuccess(cleared);
        using var clearedJson = JsonDocument.Parse(cleared.Stdout);
        var clearedTask = clearedJson.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Null, clearedTask.GetProperty("parentId").ValueKind);
        Assert.Equal(0, clearedTask.GetProperty("childCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, clearedTask.GetProperty("scheduledStart").ValueKind);
        Assert.Equal(JsonValueKind.Null, clearedTask.GetProperty("scheduledEnd").ValueKind);
        Assert.Equal(JsonValueKind.Null, clearedTask.GetProperty("durationMinutes").ValueKind);

        AssertCliError(await RunAsync("task", "update", childId, "--title", "Stale",
            "--revision", staleRevision.ToString(CultureInfo.InvariantCulture), "--format", "json"), 4, "conflict");
        AssertCliError(await RunAsync("task", "add", "Missing parent", "--parent", "missing", "--format", "json"), 3, "not_found_or_ambiguous");
        AssertCliError(await RunAsync("task", "add", "Invalid duration", "--start", "2030-01-02T09:30:00Z", "--duration", "0", "--format", "json"), 2, "invalid_arguments");
        AssertCliError(await RunAsync("task", "add", "Duration without start", "--duration", "20", "--format", "json"), 2, "invalid_arguments");

        var nestedList = await RunAsync("task", "list", "--view", "all", "--include-subtasks", "--format", "json");
        AssertSuccess(nestedList);
        using var nestedJson = JsonDocument.Parse(nestedList.Stdout);
        var listedTasks = nestedJson.RootElement.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(3, listedTasks.Count);
        Assert.Contains(listedTasks, task => task.GetProperty("id").GetString() == parentId && task.GetProperty("childCount").GetInt32() == 0);

        AssertSuccess(await RunAsync("task", "update", childId, "--parent", parentId));
        var staleParent = await RunAsync("task", "show", parentId, "--format", "json");
        AssertSuccess(staleParent);
        using var staleParentJson = JsonDocument.Parse(staleParent.Stdout);
        var staleMoveRevision = staleParentJson.RootElement.GetProperty("data").GetProperty("revision").GetInt64();
        AssertSuccess(await RunAsync("task", "update", parentId, "--notes", "Concurrent space edit"));
        AssertCliError(await RunAsync("task", "update", parentId, "--space", otherSpaceId, "--revision",
            staleMoveRevision.ToString(CultureInfo.InvariantCulture), "--format", "json"), 4, "conflict");
        Assert.Equal(SpaceIds.Default, (await store.GetTaskAsync(parentId))!.SpaceId);
        Assert.Equal(SpaceIds.Default, (await store.GetTaskAsync(childId))!.SpaceId);
        var currentParentRevision = (await store.GetTaskAsync(parentId))!.Revision;
        var movedTree = await RunAsync("task", "update", parentId, "--space", otherSpaceId, "--revision",
            currentParentRevision.ToString(CultureInfo.InvariantCulture), "--format", "json");
        AssertSuccess(movedTree);
        using var movedTreeJson = JsonDocument.Parse(movedTree.Stdout);
        Assert.Equal(otherSpaceId, movedTreeJson.RootElement.GetProperty("data").GetProperty("spaceId").GetString());
        var movedChild = await RunAsync("task", "show", childId, "--format", "json");
        AssertSuccess(movedChild);
        using var movedChildJson = JsonDocument.Parse(movedChild.Stdout);
        Assert.Equal(otherSpaceId, movedChildJson.RootElement.GetProperty("data").GetProperty("spaceId").GetString());
        Assert.Equal(parentId, movedChildJson.RootElement.GetProperty("data").GetProperty("parentId").GetString());
    }

    [Fact]
    public async Task Space_project_and_label_commands_reuse_shared_validation_and_confirmation()
    {
        var store = await CreateStoreAsync();
        var homeSpace = Assert.Single(await store.GetSpacesAsync());
        var createdSpace = await RunAsync("space", "create", "Work", "--color", "#123456", "--icon", "briefcase", "--format", "json");
        AssertSuccess(createdSpace);
        using var spaceJson = JsonDocument.Parse(createdSpace.Stdout);
        var space = spaceJson.RootElement.GetProperty("data");
        var spaceId = space.GetProperty("id").GetString()!;
        Assert.Equal("#123456", space.GetProperty("color").GetString());
        Assert.Equal("briefcase", space.GetProperty("icon").GetString());

        AssertCliError(await RunAsync("space", "create", "Work", "--format", "json"), 2, "invalid_arguments");
        var renamedSpace = await RunAsync("space", "rename", spaceId, "Projects", "--format", "json");
        AssertSuccess(renamedSpace);
        using var renamedSpaceJson = JsonDocument.Parse(renamedSpace.Stdout);
        Assert.Equal("Projects", renamedSpaceJson.RootElement.GetProperty("data").GetProperty("name").GetString());
        AssertCliError(await RunAsync("space", "archive", spaceId, "--format", "json"), 5, "confirmation_required");
        var archivedSpace = await RunAsync("space", "archive", spaceId, "--yes", "--format", "json");
        AssertSuccess(archivedSpace);
        using var archivedJson = JsonDocument.Parse(archivedSpace.Stdout);
        Assert.True(archivedJson.RootElement.GetProperty("data").GetProperty("isArchived").GetBoolean());
        AssertCliError(await RunAsync("space", "archive", homeSpace.Id, "--yes", "--format", "json"), 2, "invalid_arguments");

        AssertCliError(await RunAsync("project", "create", "Unscoped", "--format", "json"), 2, "invalid_arguments");
        var createdProject = await RunAsync("project", "create", "Roadmap", "--space", homeSpace.Id,
            "--description", "Initial", "--color", "#345678", "--format", "json");
        AssertSuccess(createdProject);
        using var projectJson = JsonDocument.Parse(createdProject.Stdout);
        var projectId = projectJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var renamedProject = await RunAsync("project", "rename", projectId, "Product roadmap", "--format", "json");
        AssertSuccess(renamedProject);
        using var renamedProjectJson = JsonDocument.Parse(renamedProject.Stdout);
        Assert.Equal("Product roadmap", renamedProjectJson.RootElement.GetProperty("data").GetProperty("name").GetString());
        var updatedProject = await RunAsync("project", "update", projectId, "--description", "Updated",
            "--color", "#abcdef", "--icon", "map", "--status", "completed", "--favorite", "--format", "json");
        AssertSuccess(updatedProject);
        using var updatedProjectJson = JsonDocument.Parse(updatedProject.Stdout);
        var updated = updatedProjectJson.RootElement.GetProperty("data");
        Assert.Equal("Product roadmap", updated.GetProperty("name").GetString());
        Assert.Equal("#abcdef", updated.GetProperty("color").GetString());
        Assert.Equal("completed", updated.GetProperty("status").GetString());
        Assert.True(updated.GetProperty("isFavorite").GetBoolean());
        AssertCliError(await RunAsync("project", "update", projectId, "--format", "json"), 2, "invalid_arguments");

        var projectTask = await RunAsync("task", "add", "Project task", "--space", homeSpace.Id, "--project", projectId, "--format", "json");
        AssertSuccess(projectTask);
        using var projectTaskJson = JsonDocument.Parse(projectTask.Stdout);
        var projectTaskId = projectTaskJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        AssertCliError(await RunAsync("project", "delete", projectId, "--format", "json"), 5, "confirmation_required");
        var deletedProject = await RunAsync("project", "delete", projectId, "--yes", "--format", "json");
        AssertSuccess(deletedProject);
        using var deletedProjectJson = JsonDocument.Parse(deletedProject.Stdout);
        Assert.True(deletedProjectJson.RootElement.GetProperty("data").GetProperty("deleted").GetBoolean());
        Assert.Equal(projectId, deletedProjectJson.RootElement.GetProperty("data").GetProperty("id").GetString());
        var taskAfterDelete = (await store.GetTaskAsync(projectTaskId))!;
        Assert.Null(taskAfterDelete.ProjectId);
        Assert.Equal(TaskWorkflowStatus.Inbox, taskAfterDelete.WorkflowStatus);

        var createdLabel = await RunAsync("label", "create", "Focus", "--color", "#999999", "--description", "Attention", "--format", "json");
        AssertSuccess(createdLabel);
        using var labelJson = JsonDocument.Parse(createdLabel.Stdout);
        var labelId = labelJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var renamedLabel = await RunAsync("label", "rename", labelId, "Attention", "--format", "json");
        AssertSuccess(renamedLabel);
        using var renamedLabelJson = JsonDocument.Parse(renamedLabel.Stdout);
        Assert.Equal("Attention", renamedLabelJson.RootElement.GetProperty("data").GetProperty("name").GetString());
        var labeledTask = await RunAsync("task", "add", "Labeled task", "--label", "Attention", "--format", "json");
        AssertSuccess(labeledTask);
        using var labeledTaskJson = JsonDocument.Parse(labeledTask.Stdout);
        var labeledTaskId = labeledTaskJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        AssertSuccess(await RunAsync("label", "create", "Other", "--format", "json"));
        AssertCliError(await RunAsync("label", "rename", labelId, "other", "--format", "json"), 2, "invalid_arguments");
        AssertCliError(await RunAsync("label", "delete", labelId, "--format", "json"), 5, "confirmation_required");
        var deletedLabel = await RunAsync("label", "delete", labelId, "--yes", "--format", "json");
        AssertSuccess(deletedLabel);
        using var deletedLabelJson = JsonDocument.Parse(deletedLabel.Stdout);
        Assert.True(deletedLabelJson.RootElement.GetProperty("data").GetProperty("deleted").GetBoolean());
        Assert.Equal(labelId, deletedLabelJson.RootElement.GetProperty("data").GetProperty("id").GetString());
        Assert.Empty((await store.GetTaskAsync(labeledTaskId))!.Labels);
        Assert.DoesNotContain(await store.GetLabelsAsync(), label => label.Id == labelId);
    }

    [Fact]
    public async Task Task_schedule_cli_moves_start_resizes_and_clears_duration_without_changing_planned_date()
    {
        var store = await CreateStoreAsync();
        var plannedOn = new DateOnly(2030, 5, 16);
        var added = await RunAsync("task", "add", "CLI calendar block", "--date", plannedOn.ToString("yyyy-MM-dd"),
            "--start", "2030-05-16T09:00:00Z", "--duration", "60", "--format", "json");
        AssertSuccess(added);
        using var addedJson = JsonDocument.Parse(added.Stdout);
        var id = addedJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;

        var moved = await RunAsync("task", "update", id, "--start", "2030-05-16T10:00:00Z", "--format", "json");
        AssertSuccess(moved);
        using (var movedJson = JsonDocument.Parse(moved.Stdout))
        {
            var task = movedJson.RootElement.GetProperty("data");
            Assert.Equal(DateTimeOffset.Parse("2030-05-16T10:00:00Z"), task.GetProperty("scheduledStart").GetDateTimeOffset());
            Assert.Equal(DateTimeOffset.Parse("2030-05-16T11:00:00Z"), task.GetProperty("scheduledEnd").GetDateTimeOffset());
            Assert.Equal(60, task.GetProperty("durationMinutes").GetInt32());
            Assert.Equal(plannedOn.ToString("yyyy-MM-dd"), task.GetProperty("plannedOn").GetString());
        }

        var resized = await RunAsync("task", "update", id, "--duration", "90", "--format", "json");
        AssertSuccess(resized);
        using (var resizedJson = JsonDocument.Parse(resized.Stdout))
        {
            var task = resizedJson.RootElement.GetProperty("data");
            Assert.Equal(DateTimeOffset.Parse("2030-05-16T11:30:00Z"), task.GetProperty("scheduledEnd").GetDateTimeOffset());
            Assert.Equal(90, task.GetProperty("durationMinutes").GetInt32());
            Assert.Equal(plannedOn.ToString("yyyy-MM-dd"), task.GetProperty("plannedOn").GetString());
        }

        var cleared = await RunAsync("task", "update", id, "--clear-schedule", "--format", "json");
        AssertSuccess(cleared);
        using var clearedJson = JsonDocument.Parse(cleared.Stdout);
        var clearedTask = clearedJson.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Null, clearedTask.GetProperty("scheduledStart").ValueKind);
        Assert.Equal(JsonValueKind.Null, clearedTask.GetProperty("scheduledEnd").ValueKind);
        Assert.Equal(JsonValueKind.Null, clearedTask.GetProperty("durationMinutes").ValueKind);
        Assert.Equal(plannedOn.ToString("yyyy-MM-dd"), clearedTask.GetProperty("plannedOn").GetString());
        Assert.Null((await store.GetTaskAsync(id))!.DurationMinutes);
    }

    [Fact]
    public async Task Stale_revision_rejects_parent_and_schedule_edits_without_mutation()
    {
        var store = await CreateStoreAsync();
        var firstParent = await RunAsync("task", "add", "First parent", "--format", "json");
        var secondParent = await RunAsync("task", "add", "Second parent", "--format", "json");
        AssertSuccess(firstParent);
        AssertSuccess(secondParent);
        using var firstParentJson = JsonDocument.Parse(firstParent.Stdout);
        using var secondParentJson = JsonDocument.Parse(secondParent.Stdout);
        var firstParentId = firstParentJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var secondParentId = secondParentJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var childResult = await RunAsync("task", "add", "Child", "--parent", firstParentId, "--format", "json");
        AssertSuccess(childResult);
        using var childJson = JsonDocument.Parse(childResult.Stdout);
        var childId = childJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var staleParentRevision = childJson.RootElement.GetProperty("data").GetProperty("revision").GetInt64();
        var changedChild = await RunAsync("task", "update", childId, "--notes", "Concurrent edit", "--format", "json");
        AssertSuccess(changedChild);

        AssertCliError(await RunAsync("task", "update", childId, "--parent", secondParentId,
            "--revision", staleParentRevision.ToString(), "--format", "json"), 4, "conflict");
        var unchangedChild = (await store.GetTaskAsync(childId))!;
        Assert.Equal(firstParentId, unchangedChild.ParentId);
        using var changedChildJson = JsonDocument.Parse(changedChild.Stdout);
        Assert.Equal(changedChildJson.RootElement.GetProperty("data").GetProperty("revision").GetInt64(), unchangedChild.Revision);

        var scheduled = await RunAsync("task", "add", "Scheduled task", "--start", "2030-06-01T09:00:00Z", "--duration", "45", "--format", "json");
        AssertSuccess(scheduled);
        using var scheduledJson = JsonDocument.Parse(scheduled.Stdout);
        var scheduledId = scheduledJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var staleScheduleRevision = scheduledJson.RootElement.GetProperty("data").GetProperty("revision").GetInt64();
        AssertSuccess(await RunAsync("task", "update", scheduledId, "--notes", "Concurrent edit"));

        AssertCliError(await RunAsync("task", "update", scheduledId, "--start", "2030-06-01T10:00:00Z",
            "--revision", staleScheduleRevision.ToString(), "--format", "json"), 4, "conflict");
        var unchangedSchedule = (await store.GetTaskAsync(scheduledId))!;
        Assert.Equal(DateTimeOffset.Parse("2030-06-01T09:00:00Z"), unchangedSchedule.ScheduledStart);
        Assert.Equal(DateTimeOffset.Parse("2030-06-01T09:45:00Z"), unchangedSchedule.ScheduledEnd);
        Assert.Equal(45, unchangedSchedule.DurationMinutes);
    }

    [Fact]
    public async Task Connected_cli_authoring_queues_parent_schedule_workspace_and_label_intents_atomically()
    {
        var store = await CreateStoreAsync();
        const string instanceId = "instance_00000000000000000000000000000017";
        const string deviceId = "device_00000000000000000000000000000017";
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(instanceId, true, true);
        await new JsonPersonalCloudConnectionStore(Path.Combine(_directory, "personal-cloud-connection.json"))
            .SaveAsync(new PersonalCloudConnectionProfile
            {
                ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
                InstanceId = instanceId,
                DeviceId = deviceId,
                ArtifactVersion = "1.2.3",
                ConnectedAt = DateTimeOffset.UtcNow,
            });

        var label = await RunAsync("label", "create", "Focus", "--format", "json");
        AssertSuccess(label);
        using var labelJson = JsonDocument.Parse(label.Stdout);
        var labelId = labelJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var firstParent = await RunAsync("task", "add", "Cloud parent one", "--format", "json");
        var secondParent = await RunAsync("task", "add", "Cloud parent two", "--format", "json");
        AssertSuccess(firstParent);
        AssertSuccess(secondParent);
        using var firstParentJson = JsonDocument.Parse(firstParent.Stdout);
        using var secondParentJson = JsonDocument.Parse(secondParent.Stdout);
        var firstParentId = firstParentJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var secondParentId = secondParentJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var child = await RunAsync("task", "add", "Cloud child", "--parent", firstParentId, "--format", "json");
        var sibling = await RunAsync("task", "add", "Cloud sibling", "--format", "json");
        AssertSuccess(child);
        AssertSuccess(sibling);
        using var childJson = JsonDocument.Parse(child.Stdout);
        using var siblingJson = JsonDocument.Parse(sibling.Stdout);
        var childId = childJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var siblingId = siblingJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var staleParentRevision = childJson.RootElement.GetProperty("data").GetProperty("revision").GetInt64();
        AssertSuccess(await RunAsync("task", "update", childId, "--notes", "Concurrent parent edit"));
        AssertCliError(await RunAsync("task", "update", childId, "--parent", secondParentId,
            "--revision", staleParentRevision.ToString(), "--format", "json"), 4, "conflict");
        Assert.Equal(firstParentId, (await store.GetTaskAsync(childId))!.ParentId);
        var currentChildRevision = (await store.GetTaskAsync(childId))!.Revision;
        AssertSuccess(await RunAsync("task", "update", childId, "--parent", secondParentId, "--revision", currentChildRevision.ToString()));
        AssertSuccess(await RunAsync("task", "update", childId, "--label", labelId));
        AssertSuccess(await RunAsync("task", "update", siblingId, "--label", labelId));

        var scheduled = await RunAsync("task", "add", "Cloud schedule", "--start", "2030-06-01T09:00:00Z", "--duration", "60", "--format", "json");
        AssertSuccess(scheduled);
        using var scheduledJson = JsonDocument.Parse(scheduled.Stdout);
        var scheduledId = scheduledJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var staleScheduleRevision = scheduledJson.RootElement.GetProperty("data").GetProperty("revision").GetInt64();
        AssertSuccess(await RunAsync("task", "update", scheduledId, "--notes", "Concurrent schedule edit"));
        AssertCliError(await RunAsync("task", "update", scheduledId, "--duration", "90",
            "--revision", staleScheduleRevision.ToString(), "--format", "json"), 4, "conflict");
        var unchangedSchedule = (await store.GetTaskAsync(scheduledId))!;
        Assert.Equal(60, unchangedSchedule.DurationMinutes);
        Assert.Equal(DateTimeOffset.Parse("2030-06-01T10:00:00Z"), unchangedSchedule.ScheduledEnd);
        AssertSuccess(await RunAsync("task", "update", scheduledId, "--duration", "90", "--revision", unchangedSchedule.Revision.ToString()));

        var space = await RunAsync("space", "create", "Connected space", "--format", "json");
        AssertSuccess(space);
        using var spaceJson = JsonDocument.Parse(space.Stdout);
        var spaceId = spaceJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        AssertSuccess(await RunAsync("space", "rename", spaceId, "Connected renamed space"));

        var cloudTreeRoot = await RunAsync("task", "add", "Cloud tree root", "--format", "json");
        AssertSuccess(cloudTreeRoot);
        using var cloudTreeRootJson = JsonDocument.Parse(cloudTreeRoot.Stdout);
        var cloudTreeRootId = cloudTreeRootJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var cloudTreeChild = await RunAsync("task", "add", "Cloud tree child", "--parent", cloudTreeRootId, "--format", "json");
        AssertSuccess(cloudTreeChild);
        using var cloudTreeChildJson = JsonDocument.Parse(cloudTreeChild.Stdout);
        var cloudTreeChildId = cloudTreeChildJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        AssertSuccess(await RunAsync("task", "update", cloudTreeRootId, "--space", spaceId));
        Assert.Equal(spaceId, (await store.GetTaskAsync(cloudTreeRootId))!.SpaceId);
        Assert.Equal(spaceId, (await store.GetTaskAsync(cloudTreeChildId))!.SpaceId);
        AssertSuccess(await RunAsync("task", "update", cloudTreeRootId, "--space", SpaceIds.Default));

        var project = await RunAsync("project", "create", "Connected project", "--space", spaceId, "--format", "json");
        AssertSuccess(project);
        using var projectJson = JsonDocument.Parse(project.Stdout);
        var projectId = projectJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        AssertSuccess(await RunAsync("project", "update", projectId, "--color", "#123456"));
        AssertSuccess(await RunAsync("project", "delete", projectId, "--yes"));
        AssertSuccess(await RunAsync("space", "archive", spaceId, "--yes"));
        AssertCliError(await RunAsync("label", "rename", labelId, "Focus renamed", "--format", "json"), 6, "operation_restricted");
        AssertCliError(await RunAsync("label", "delete", labelId, "--yes", "--format", "json"), 6, "operation_restricted");
        Assert.Contains(await store.GetLabelsAsync(), item => item.Id == labelId && item.Name == "Focus");
        Assert.Contains((await store.GetTaskAsync(childId))!.Labels, item => item.Id == labelId);
        Assert.Contains((await store.GetTaskAsync(siblingId))!.Labels, item => item.Id == labelId);

        var pending = await store.GetPendingPersonalCloudOutboxAsync(instanceId, deviceId);
        Assert.Contains(pending, item => item.EntityId == childId && item.Operation == "task.setParent");
        Assert.Contains(pending, item => item.Operation == "task.patch" && item.CanonicalJson.Contains("durationMinutes", StringComparison.Ordinal));
        Assert.Contains(pending, item => item.Operation == "space.put");
        Assert.Contains(pending, item => item.Operation == "project.put");
        Assert.Contains(pending, item => item.Operation == "project.delete");
        Assert.Equal(2, pending.Count(item => item.Operation == "task.addLabel"));
        Assert.DoesNotContain(pending, item => item.Operation == "task.removeLabel");
    }

    [Fact]
    public async Task Legacy_cloud_task_creation_emits_parent_after_move_and_keeps_schedule_local()
    {
        var store = await CreateStoreAsync();
        const string instanceId = "instance_00000000000000000000000000000062";
        const string deviceId = "device_00000000000000000000000000000062";
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(instanceId, false, false);
        var space = new SpaceItem { Id = "space_legacy_create", Name = "Legacy space" };
        await store.UpsertSpaceAsync(space);
        var profile = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
            InstanceId = instanceId,
            DeviceId = deviceId,
            ArtifactVersion = "1.2.3",
            ConnectedAt = DateTimeOffset.UtcNow,
        };
        var service = new TaskApplicationService(store, () => profile);
        await service.InitializeAsync();
        var parent = await service.CreateTaskAsync(new CreateTaskRequest { Title = "Legacy parent", Space = space.Id });
        var start = new DateTimeOffset(2030, 7, 8, 9, 0, 0, TimeSpan.Zero);
        var child = await service.CreateTaskAsync(new CreateTaskRequest
        {
            Title = "Legacy child",
            Space = space.Id,
            ParentId = parent.Id,
            ScheduledStart = start,
            DurationMinutes = 45,
        });

        Assert.Equal(space.Id, child.SpaceId);
        Assert.Equal(parent.Id, child.ParentId);
        Assert.Equal(start, child.ScheduledStart);
        Assert.Equal(start.AddMinutes(45), child.ScheduledEnd);
        var childIntents = await store.GetPendingPersonalCloudOutboxAsync(instanceId, deviceId);
        var move = Assert.Single(childIntents, item => item.EntityId == child.Id && item.Operation == "task.move");
        var parentIntent = Assert.Single(childIntents, item => item.EntityId == child.Id && item.Operation == "task.setParent");
        Assert.True(move.DeviceSequence < parentIntent.DeviceSequence);
        Assert.Contains(parent.Id, parentIntent.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain(childIntents, item => item.EntityId == child.Id && item.Operation == "task.patch");
    }

    [Fact]
    public async Task Connected_restore_refuses_with_cloud_connected_error_without_changing_live_state()
    {
        var store = await CreateStoreAsync();
        const string instanceId = "instance_00000000000000000000000000000018";
        const string deviceId = "device_00000000000000000000000000000018";
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(instanceId, true, true);
        await new JsonPersonalCloudConnectionStore(Path.Combine(_directory, "personal-cloud-connection.json"))
            .SaveAsync(new PersonalCloudConnectionProfile
            {
                ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
                InstanceId = instanceId,
                DeviceId = deviceId,
                ArtifactVersion = "1.2.3",
                ConnectedAt = DateTimeOffset.UtcNow,
            });
        var created = await RunAsync("task", "add", "Before rollback", "--format", "json");
        AssertSuccess(created);
        using var createdJson = JsonDocument.Parse(created.Stdout);
        var taskId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var beforeRestore = await store.GetPendingPersonalCloudOutboxAsync(instanceId, deviceId);
        var point = await RunAsync("restore-point", "create", "--note", "Connected rollback", "--format", "json");
        AssertSuccess(point);
        using var pointJson = JsonDocument.Parse(point.Stdout);
        var fileName = pointJson.RootElement.GetProperty("data").GetProperty("fileName").GetString()!;

        AssertSuccess(await RunAsync("task", "update", taskId, "--notes", "After rollback"));
        var pendingAfterUpdate = await store.GetPendingPersonalCloudOutboxAsync(instanceId, deviceId);
        Assert.NotEqual(beforeRestore.Count, pendingAfterUpdate.Count);
        var blockedRestore = await RunAsync("restore-point", "restore", fileName, "--yes", "--format", "json");
        AssertCliError(blockedRestore, 6, "cloud_connected");
        using var blockedRestoreJson = JsonDocument.Parse(blockedRestore.Stderr);
        Assert.Contains("rollback isn't supported with the cloud on yet",
            blockedRestoreJson.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

        Assert.Equal("After rollback", (await store.GetTaskAsync(taskId))!.Notes);
        Assert.Equal(pendingAfterUpdate.Select(item => item.CanonicalJson),
            (await store.GetPendingPersonalCloudOutboxAsync(instanceId, deviceId)).Select(item => item.CanonicalJson));
        Assert.Equal(instanceId, new JsonPersonalCloudConnectionStore(Path.Combine(_directory, "personal-cloud-connection.json")).Load()!.InstanceId);
        AssertSuccess(await RunAsync("restore-point", "list", "--format", "json"));
    }

    [Fact]
    public async Task Restore_refuses_while_the_desktop_holds_the_shared_runtime_lease()
    {
        var store = await CreateStoreAsync();
        var added = await RunAsync("task", "add", "Before blocked restore", "--format", "json");
        AssertSuccess(added);
        using var addedJson = JsonDocument.Parse(added.Stdout);
        var taskId = addedJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var point = await RunAsync("restore-point", "create", "--format", "json");
        AssertSuccess(point);
        using var pointJson = JsonDocument.Parse(point.Stdout);
        var fileName = pointJson.RootElement.GetProperty("data").GetProperty("fileName").GetString()!;
        AssertSuccess(await RunAsync("task", "update", taskId, "--notes", "Keep current data"));

        using var desktopLease = ChannelRuntimeLease.AcquireShared(
            OpenzaRuntimeContext.Create(OpenzaChannel.Dev, _directory));
        AssertCliError(await RunAsync("restore-point", "restore", fileName, "--format", "json"), 5, "confirmation_required");
        Assert.Equal("Keep current data", (await store.GetTaskAsync(taskId))!.Notes);
        var restore = await RunAsync("restore-point", "restore", fileName, "--yes", "--format", "json");

        Assert.Equal(1, restore.ExitCode);
        Assert.Contains("before replacing its data", restore.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Keep current data", (await store.GetTaskAsync(taskId))!.Notes);
    }

    [Fact]
    public async Task Label_delete_skips_cloud_removals_for_unpublished_and_pending_delete_tasks()
    {
        var store = await CreateStoreAsync();
        const string instanceId = "instance_00000000000000000000000000000063";
        const string deviceId = "device_00000000000000000000000000000063";
        var label = new LabelItem { Id = "label_pending_cloud_delete", Name = "Focus" };
        await store.UpsertLabelAsync(label);
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_unpublished_with_label",
            Title = "Unpublished",
            Labels = [label],
        });
        await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem
        {
            Id = "task_pending_delete_with_label",
            Title = "Pending delete",
            Labels = [label],
        }, instanceId, deviceId);
        await store.DeleteTaskWithPersonalCloudIntentAsync(
            "task_pending_delete_with_label", DateTimeOffset.UtcNow, 1, instanceId, deviceId);

        await store.DeleteLabelWithPersonalCloudIntentsAsync(label.Id, instanceId, deviceId);

        var pending = await store.GetPendingPersonalCloudOutboxAsync(instanceId, deviceId);
        Assert.Contains(pending, item => item.EntityId == "task_pending_delete_with_label" && item.Operation == "task.delete");
        Assert.DoesNotContain(pending, item => item.Operation == "task.removeLabel");
        Assert.DoesNotContain((await store.GetTaskAsync("task_unpublished_with_label"))!.Labels, item => item.Id == label.Id);
        Assert.Null(await store.GetTaskAsync("task_pending_delete_with_label"));
    }

    [Fact]
    public async Task Workspace_commands_report_invalid_names_and_missing_targets()
    {
        var store = await CreateStoreAsync();
        AssertCliError(await RunAsync("space", "create", "   ", "--format", "json"), 2, "invalid_arguments");
        AssertCliError(await RunAsync("space", "rename", "space_default", "   ", "--format", "json"), 2, "invalid_arguments");
        AssertCliError(await RunAsync("space", "rename", "missing-space", "Renamed", "--format", "json"), 3, "not_found_or_ambiguous");
        AssertCliError(await RunAsync("space", "archive", "missing-space", "--yes", "--format", "json"), 3, "not_found_or_ambiguous");

        AssertCliError(await RunAsync("project", "create", "   ", "--space", "space_default", "--format", "json"), 2, "invalid_arguments");
        AssertCliError(await RunAsync("project", "rename", "missing-project", "Renamed", "--format", "json"), 3, "not_found_or_ambiguous");
        AssertCliError(await RunAsync("project", "update", "missing-project", "--name", "Renamed", "--format", "json"), 3, "not_found_or_ambiguous");
        AssertCliError(await RunAsync("project", "delete", "missing-project", "--yes", "--format", "json"), 3, "not_found_or_ambiguous");

        var project = await RunAsync("project", "create", "Color target", "--space", "space_default", "--format", "json");
        AssertSuccess(project);
        using var projectJson = JsonDocument.Parse(project.Stdout);
        var projectId = projectJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        AssertCliError(await RunAsync("project", "update", projectId, "--color", "banana", "--format", "json"), 2, "invalid_arguments");

        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_provider_owned", Name = "Provider project", IntegrationId = IntegrationIds.Todoist,
        });
        AssertSuccess(await RunAsync("project", "rename", "project_provider_owned", "Local rename", "--format", "json"));
        Assert.Equal("Local rename", (await store.GetProviderProjectsAsync(IntegrationIds.Todoist)).Single().Name);
        AssertCliError(await RunAsync("project", "update", "project_provider_owned", "--color", "#123456", "--format", "json"),
            2, "invalid_arguments");

        AssertCliError(await RunAsync("label", "create", "   ", "--format", "json"), 2, "invalid_arguments");
        AssertCliError(await RunAsync("label", "rename", "missing-label", "Renamed", "--format", "json"), 3, "not_found_or_ambiguous");
        AssertCliError(await RunAsync("label", "delete", "missing-label", "--yes", "--format", "json"), 3, "not_found_or_ambiguous");
    }

    [Fact]
    public async Task Label_rename_and_delete_are_local_only_when_cloud_connected_or_provider_owned()
    {
        var store = await CreateStoreAsync();
        var localLabel = new LabelItem { Id = "label_local_only", Name = "Local" };
        await store.UpsertLabelAsync(localLabel);
        AssertSuccess(await RunAsync("label", "rename", localLabel.Id, "Renamed"));
        Assert.Equal("Renamed", (await store.GetLabelsAsync()).Single(item => item.Id == localLabel.Id).Name);
        AssertSuccess(await RunAsync("label", "delete", localLabel.Id, "--yes"));
        Assert.DoesNotContain(await store.GetLabelsAsync(), item => item.Id == localLabel.Id);

        var providerLabel = new LabelItem { Id = "label_provider_owned", Name = "Provider", IntegrationId = IntegrationIds.Todoist };
        await store.UpsertLabelAsync(providerLabel);
        AssertCliError(await RunAsync("label", "rename", providerLabel.Id, "Changed", "--format", "json"), 6, "operation_restricted");
        AssertCliError(await RunAsync("label", "delete", providerLabel.Id, "--yes", "--format", "json"), 6, "operation_restricted");

        var cloudLabel = new LabelItem { Id = "label_cloud_owned", Name = "Cloud label" };
        await store.UpsertLabelAsync(cloudLabel);
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_cloud_label", Title = "Cloud label task", IntegrationId = IntegrationIds.Todoist, Labels = [cloudLabel],
        });
        await SavePersonalCloudConnectionAsync();

        AssertCliError(await RunAsync("label", "rename", cloudLabel.Id, "Changed", "--format", "json"), 6, "operation_restricted");
        AssertCliError(await RunAsync("label", "delete", cloudLabel.Id, "--yes", "--format", "json"), 6, "operation_restricted");
        Assert.Contains(await store.GetLabelsAsync(), item => item.Id == cloudLabel.Id && item.Name == cloudLabel.Name);
        Assert.Contains((await store.GetTaskAsync("task_cloud_label"))!.Labels, item => item.Id == cloudLabel.Id);
    }

    [Fact]
    public async Task Local_label_delete_does_not_partially_remove_task_relationships_on_conflict()
    {
        var store = await CreateStoreAsync();
        var label = new LabelItem { Id = "label_local_atomic", Name = "Atomic" };
        await store.UpsertLabelAsync(label);
        foreach (var id in new[] { "task_atomic_first", "task_atomic_second" })
            await store.UpsertTaskAsync(new TaskItem { Id = id, Title = id, Labels = [label] });
        var beforeFirst = (await store.GetTaskAsync("task_atomic_first"))!;
        var beforeSecond = (await store.GetTaskAsync("task_atomic_second"))!;

        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync();
            var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER synthetic_local_label_conflict BEFORE DELETE ON task_labels WHEN OLD.task_id = 'task_atomic_second' AND OLD.label_id = 'label_local_atomic' BEGIN SELECT RAISE(ABORT, 'synthetic relationship conflict'); END;";
            await trigger.ExecuteNonQueryAsync();
        }

        var result = await RunAsync("label", "delete", label.Id, "--yes", "--format", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(await store.GetLabelsAsync(), item => item.Id == label.Id);
        var afterFirst = (await store.GetTaskAsync("task_atomic_first"))!;
        var afterSecond = (await store.GetTaskAsync("task_atomic_second"))!;
        Assert.Contains(afterFirst.Labels, item => item.Id == label.Id);
        Assert.Contains(afterSecond.Labels, item => item.Id == label.Id);
        Assert.Equal(beforeFirst.Revision, afterFirst.Revision);
        Assert.Equal(beforeSecond.Revision, afterSecond.Revision);
    }

    [Fact]
    public async Task Parent_edits_on_provider_linked_tasks_are_restricted_with_or_without_cloud()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "task_parent_target", Title = "Parent" });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_provider_parent", Title = "Todoist task", IntegrationId = IntegrationIds.Todoist,
        });

        AssertCliError(await RunAsync("task", "update", "task_provider_parent", "--parent", "task_parent_target", "--format", "json"),
            6, "operation_restricted");
        await SavePersonalCloudConnectionAsync();
        AssertCliError(await RunAsync("task", "update", "task_provider_parent", "--clear-parent", "--format", "json"),
            6, "operation_restricted");
        Assert.Null((await store.GetTaskAsync("task_provider_parent"))!.ParentId);
    }

    [Fact]
    public void Personal_cloud_contract_errors_map_to_documented_exit_codes()
    {
        Assert.Equal(6, OpenzaCli.MapPersonalCloudContractExitCode("upgrade_required"));
        Assert.Equal(6, OpenzaCli.MapPersonalCloudContractExitCode("invalid_request"));
        Assert.Equal(4, OpenzaCli.MapPersonalCloudContractExitCode("version_conflict"));
        Assert.Equal(3, OpenzaCli.MapPersonalCloudContractExitCode("entity_not_found"));
    }

    [Fact]
    public async Task Restore_point_commands_save_notes_list_read_only_and_require_confirmation()
    {
        var store = await CreateStoreAsync();
        var emptyList = await RunAsync("restore-point", "list", "--format", "json");
        AssertSuccess(emptyList);
        using var emptyListJson = JsonDocument.Parse(emptyList.Stdout);
        Assert.Empty(emptyListJson.RootElement.GetProperty("data").EnumerateArray());
        Assert.False(Directory.Exists(Path.Combine(_directory, "restore-points")));

        var before = await RunAsync("task", "add", "Before snapshot", "--format", "json");
        AssertSuccess(before);
        using var beforeJson = JsonDocument.Parse(before.Stdout);
        var beforeId = beforeJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;

        var created = await RunAsync("restore-point", "create", "--note", "Before bulk change", "--format", "json");
        AssertSuccess(created);
        using var createdJson = JsonDocument.Parse(created.Stdout);
        var backup = createdJson.RootElement.GetProperty("data");
        var fileName = backup.GetProperty("fileName").GetString()!;
        Assert.Equal("Before bulk change", backup.GetProperty("note").GetString());
        Assert.Equal(new[] { "fileName", "createdAt", "sizeBytes", "reason", "note", "taskCount", "projectCount", "spaceCount", "integrityStatus" },
            backup.EnumerateObject().Select(property => property.Name).ToArray());

        var after = await RunAsync("task", "add", "After snapshot", "--format", "json");
        AssertSuccess(after);
        using var afterJson = JsonDocument.Parse(after.Stdout);
        var afterId = afterJson.RootElement.GetProperty("data").GetProperty("id").GetString()!;

        var listed = await RunAsync("restore-point", "list", "--format", "json");
        AssertSuccess(listed);
        using var listJson = JsonDocument.Parse(listed.Stdout);
        Assert.Contains(listJson.RootElement.GetProperty("data").EnumerateArray(), item =>
            item.GetProperty("fileName").GetString() == fileName && item.GetProperty("note").GetString() == "Before bulk change");

        AssertCliError(await RunAsync("restore-point", "restore", fileName, "--format", "json"), 5, "confirmation_required");
        AssertCliError(await RunAsync("restore-point", "restore", "missing.db", "--yes", "--format", "json"), 3, "not_found_or_ambiguous");
        var restored = await RunAsync("restore-point", "restore", fileName, "--yes", "--format", "json");
        AssertSuccess(restored);
        using var restoredJson = JsonDocument.Parse(restored.Stdout);
        Assert.True(restoredJson.RootElement.GetProperty("data").GetProperty("restored").GetBoolean());
        Assert.Equal(fileName, restoredJson.RootElement.GetProperty("data").GetProperty("fileName").GetString());
        Assert.NotNull(await store.GetTaskAsync(beforeId));
        Assert.Null(await store.GetTaskAsync(afterId));
    }

    [Fact]
    public async Task Restore_point_list_discovers_snapshots_without_a_live_database()
    {
        var store = await CreateStoreAsync();
        AssertSuccess(await RunAsync("task", "add", "Snapshot only", "--format", "json"));
        var created = await RunAsync("restore-point", "create", "--format", "json");
        AssertSuccess(created);
        using var createdJson = JsonDocument.Parse(created.Stdout);
        var fileName = createdJson.RootElement.GetProperty("data").GetProperty("fileName").GetString()!;

        SqliteConnection.ClearAllPools();
        File.Delete(store.DatabasePath);
        var listed = await RunAsync("restore-point", "list", "--format", "json");

        AssertSuccess(listed);
        using var listedJson = JsonDocument.Parse(listed.Stdout);
        Assert.Contains(listedJson.RootElement.GetProperty("data").EnumerateArray(),
            item => item.GetProperty("fileName").GetString() == fileName);
        Assert.False(File.Exists(store.DatabasePath));
    }

    [Fact]
    public async Task Help_documents_new_task_management_and_restore_options()
    {
        var addHelp = await RunAsync("task", "add", "--help");
        var updateHelp = await RunAsync("task", "update", "--help");
        var projectHelp = await RunAsync("project", "update", "--help");
        var restoreHelp = await RunAsync("restore-point", "restore", "--help");

        AssertSuccess(addHelp);
        AssertSuccess(updateHelp);
        AssertSuccess(projectHelp);
        AssertSuccess(restoreHelp);
        Assert.Contains("--parent", addHelp.Stdout);
        Assert.Contains("--start", addHelp.Stdout);
        Assert.Contains("--duration", addHelp.Stdout);
        Assert.Contains("--clear-parent", updateHelp.Stdout);
        Assert.Contains("--clear-schedule", updateHelp.Stdout);
        Assert.Contains("--color", projectHelp.Stdout);
        Assert.Contains("--yes", restoreHelp.Stdout);
    }

    private Task SavePersonalCloudConnectionAsync() =>
        new JsonPersonalCloudConnectionStore(Path.Combine(_directory, "personal-cloud-connection.json")).SaveAsync(
            new PersonalCloudConnectionProfile
            {
                ServiceOrigin = new Uri("https://cloud.example"),
                InstanceId = "instance_00000000000000000000000000000071",
                DeviceId = "device_00000000000000000000000000000071",
                ArtifactVersion = "test",
                ConnectedAt = DateTimeOffset.UtcNow,
            });

    private static void AssertCliError(CliResult result, int exitCode, string code)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        using var json = JsonDocument.Parse(result.Stderr);
        Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
