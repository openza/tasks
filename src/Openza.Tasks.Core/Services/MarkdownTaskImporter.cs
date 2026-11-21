using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Services;

public static class MarkdownTaskImporter
{
    public static async Task ImportAsync(ITaskStore store, IReadOnlyList<ParsedTask> tasks, string spaceId)
    {
        var projects = (await store.GetProjectsAsync(spaceId, includeArchived: true)).ToList();
        var labels = (await store.GetLabelsAsync()).ToList();
        foreach (var parsed in tasks)
        {
            ProjectItem? project = null;
            if (parsed.ProjectName is { } name)
            {
                project = projects.FirstOrDefault(item => item.Name == name && item.IntegrationId == IntegrationIds.Local);
                if (project is null)
                {
                    project = new ProjectItem { Id = $"local_{Guid.NewGuid():N}", Name = name, SpaceId = spaceId };
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
            };
            await store.UpsertTaskAsync(task);
            await store.SetTaskLabelsAsync(task.Id, taskLabels);
        }
    }
}
