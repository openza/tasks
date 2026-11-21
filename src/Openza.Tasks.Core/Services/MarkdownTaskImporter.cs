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
        if (cloud is not null && !workspace && tasks.Any(task => task.CompletionState == TaskCompletionState.Cancelled))
            throw new InvalidOperationException("Cancelled tasks can't be imported while connected to a Personal Cloud that doesn't support workspace sync; disconnect Personal Cloud before importing this file, or remove the cancelled tasks.");
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
                var label = labels.FirstOrDefault(item => string.Equals(item.Name, imported.Name, StringComparison.OrdinalIgnoreCase)
                    && item.IntegrationId == IntegrationIds.Local);
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
                await cloudStore.CreateLegacyRichTaskWithPersonalCloudIntentAsync(task, cloud!.InstanceId, cloud.DeviceId);
            else
                await store.UpsertTaskAsync(task);
        }
    }

}
