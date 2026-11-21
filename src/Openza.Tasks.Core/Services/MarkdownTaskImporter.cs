using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Services;

public static class MarkdownTaskImporter
{
    public static async Task ImportAsync(ITaskStore store, IReadOnlyList<ParsedTask> tasks, string spaceId, PersonalCloudConnectionProfile? cloud = null)
    {
        var cloudStore = cloud is not null ? store as SqliteTaskStore : null;
        var workspace = cloudStore is not null && await cloudStore.SupportsPersonalCloudWorkspaceAsync(cloud!.InstanceId);
        var projects = (await store.GetProjectsAsync(spaceId, includeArchived: true)).ToList();
        var labels = (await store.GetLabelsAsync()).ToList();
        foreach (var parsed in tasks)
        {
            ProjectItem? project = null;
            if (parsed.ProjectName is { } name)
            {
                project = projects.FirstOrDefault(item => item.Name == name && item.IntegrationId == IntegrationIds.Local
                    && item.EffectiveStatus != ProjectLifecycleStates.Archived);
                if (project is null)
                {
                    project = new ProjectItem { Id = $"local_{Guid.NewGuid():N}", Name = name, SpaceId = spaceId };
                    if (workspace)
                        await cloudStore!.UpsertProjectWithPersonalCloudIntentAsync(project, cloud!.InstanceId, cloud.DeviceId);
                    else
                        await store.UpsertProjectAsync(project);
                    projects.Add(project);
                }
            }
            var taskLabels = new List<LabelItem>();
            foreach (var imported in parsed.Labels)
            {
                var label = labels.FirstOrDefault(item => item.Name == imported.Name && item.IntegrationId == IntegrationIds.Local);
                if (label is null)
                {
                    label = new LabelItem { Id = $"local_{Guid.NewGuid():N}", Name = imported.Name, Color = imported.Color };
                    await store.UpsertLabelAsync(label);
                    labels.Add(label);
                }
                taskLabels.Add(label);
            }
            var now = DateTimeOffset.UtcNow;
            var task = new TaskItem
            {
                Id = $"local_{Guid.NewGuid():N}", SpaceId = spaceId, Title = parsed.Title,
                ProjectId = project?.Id, Priority = parsed.Priority, PlannedOn = parsed.PlannedOn,
                DeadlineOn = parsed.DeadlineOn, Notes = parsed.Notes, WorkflowStatus = parsed.WorkflowStatus,
                CompletionState = parsed.IsCompleted ? TaskCompletionState.Completed : parsed.CompletionState,
                CompletedAt = parsed.IsCompleted ? now : null, CreatedAt = now, UpdatedAt = now,
                Labels = taskLabels,
            };
            if (workspace)
                await cloudStore!.CreateRichTaskWithPersonalCloudIntentAsync(task, cloud!.InstanceId, cloud.DeviceId);
            else if (cloudStore is not null)
                await CreateLegacyCloudTaskAsync(cloudStore, task, project, cloud!);
            else
                await store.UpsertTaskAsync(task);
        }
    }

    private static async Task CreateLegacyCloudTaskAsync(SqliteTaskStore store, TaskItem task, ProjectItem? project, PersonalCloudConnectionProfile cloud)
    {
        var baseline = task with
        {
            SpaceId = SpaceIds.Default, ProjectId = null, Notes = null, Priority = 2,
            WorkflowStatus = TaskWorkflowStatus.Inbox, CompletionState = TaskCompletionState.Open,
            CompletedAt = null, PlannedOn = null, DeadlineOn = null, Labels = [],
        };
        await store.CreateTaskWithPersonalCloudIntentAsync(baseline, cloud.InstanceId, cloud.DeviceId);
        var version = 1L;
        if (task.SpaceId != baseline.SpaceId)
        {
            var space = (await store.GetSpacesAsync()).Single(item => item.Id == task.SpaceId);
            await store.MoveTaskTreeToSpaceWithPersonalCloudIntentAsync(task.Id, space.Id, space.Name,
                new Dictionary<string, long> { [task.Id] = version++ }, cloud.InstanceId, cloud.DeviceId);
        }
        if (project is not null)
            await store.MutateTaskRelationshipWithPersonalCloudIntentAsync(task.Id, "task.setProject", project.Id, project.Name, version++, cloud.InstanceId, cloud.DeviceId);
        foreach (var label in task.Labels)
            await store.MutateTaskRelationshipWithPersonalCloudIntentAsync(task.Id, "task.addLabel", label.Id, label.Name, version++, cloud.InstanceId, cloud.DeviceId);
        if (task.WorkflowStatus != baseline.WorkflowStatus)
            await store.UpdateTaskWorkflowStatusWithPersonalCloudIntentAsync(task.Id, task.WorkflowStatus, version++, cloud.InstanceId, cloud.DeviceId);
        if (task.Priority != baseline.Priority)
            await store.UpdateTaskPriorityWithPersonalCloudIntentAsync(task.Id, task.Priority, version++, cloud.InstanceId, cloud.DeviceId);
        if (task.PlannedOn is not null)
            await store.UpdateTaskPlannedOnWithPersonalCloudIntentAsync(task.Id, task.PlannedOn, version++, cloud.InstanceId, cloud.DeviceId);
        if (task.Notes is not null)
            await store.UpdateTaskNotesWithPersonalCloudIntentAsync(task.Id, task.Notes, version++, cloud.InstanceId, cloud.DeviceId);
        if (task.DeadlineOn is not null)
            await store.UpdateTaskDeadlineOnWithPersonalCloudIntentAsync(task.Id, task.DeadlineOn, version++, cloud.InstanceId, cloud.DeviceId);
        if (task.IsCompleted)
            await store.CompleteTaskWithPersonalCloudIntentAsync(task.Id, task.CompletedAt!.Value, version, cloud.InstanceId, cloud.DeviceId);
        else if (task.CompletionState == TaskCompletionState.Cancelled)
            await store.UpsertTaskAsync((await store.GetTaskAsync(task.Id))! with { CompletionState = TaskCompletionState.Cancelled });
    }
}
