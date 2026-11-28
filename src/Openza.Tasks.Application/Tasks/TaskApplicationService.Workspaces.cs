using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Application.Tasks;

public sealed partial class TaskApplicationService
{
    public async Task<IReadOnlyDictionary<string, int>> GetTaskChildCountsAsync(CancellationToken cancellationToken = default)
    {
        var tasks = await Store.GetTasksAsync(new TaskQuery
        {
            Kind = TaskListKind.All,
            IncludeSubtasks = true,
        }, cancellationToken).ConfigureAwait(false);
        return tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.ParentId))
            .GroupBy(task => task.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }

    public async Task<SpaceItem> CreateSpaceAsync(string name, string? color = null, string? icon = null, CancellationToken cancellationToken = default)
    {
        using var lease = AcquirePersonalCloudAuthoringLease();
        name = RequireName(name, "space");
        var spaces = await Store.GetSpacesAsync(includeArchived: true, cancellationToken).ConfigureAwait(false);
        if (spaces.Any(space => !space.IsArchived && string.Equals(space.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new ArgumentException("A space with that name already exists.", nameof(name));

        var now = DateTimeOffset.UtcNow;
        var space = new SpaceItem
        {
            Id = $"space_{Guid.NewGuid():N}",
            Name = name,
            Color = WorkspaceColorValidation.Normalize(color, "#6B8AFD", nameof(color)),
            Icon = icon,
            SortOrder = spaces.Count,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await UpsertSpaceWithCloudAsync(space, cancellationToken).ConfigureAwait(false);
        return space;
    }

    public async Task<SpaceItem> RenameSpaceAsync(string spaceId, string name, CancellationToken cancellationToken = default)
    {
        using var lease = AcquirePersonalCloudAuthoringLease();
        name = RequireName(name, "space");
        var space = await RequireSpaceByIdAsync(spaceId, cancellationToken).ConfigureAwait(false);
        var renamed = space with { Name = name, UpdatedAt = DateTimeOffset.UtcNow };
        await UpsertSpaceWithCloudAsync(renamed, cancellationToken).ConfigureAwait(false);
        return renamed;
    }

    public async Task<SpaceItem> ArchiveSpaceAsync(string spaceId, CancellationToken cancellationToken = default)
    {
        using var lease = AcquirePersonalCloudAuthoringLease();
        var space = await RequireSpaceByIdAsync(spaceId, cancellationToken).ConfigureAwait(false);
        if (space.IsArchived) return space;
        var activeSpaces = await Store.GetSpacesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (activeSpaces.Count <= 1)
            throw new ArgumentException("The last active space cannot be archived.", nameof(spaceId));
        var archived = space with { IsArchived = true, UpdatedAt = DateTimeOffset.UtcNow };
        await UpsertSpaceWithCloudAsync(archived, cancellationToken).ConfigureAwait(false);
        return archived;
    }

    public async Task<ProjectItem> CreateProjectAsync(
        string? spaceReference,
        string name,
        string? description = null,
        string? color = null,
        string? icon = null,
        CancellationToken cancellationToken = default)
    {
        using var lease = AcquirePersonalCloudAuthoringLease();
        name = RequireName(name, "project");
        if (string.IsNullOrWhiteSpace(spaceReference))
            throw new ArgumentException("Choose a specific space before creating a project.", nameof(spaceReference));
        var space = await ResolveSpaceAsync(spaceReference, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var project = new ProjectItem
        {
            Id = $"project_{Guid.NewGuid():N}",
            SpaceId = space.Id,
            IntegrationId = IntegrationIds.Local,
            Name = name,
            Description = NullIfEmpty(description),
            Color = WorkspaceColorValidation.Normalize(color, "#6B8AFD", nameof(color)),
            Icon = icon,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await UpsertProjectWithCloudAsync(project, cancellationToken).ConfigureAwait(false);
        return project;
    }

    public async Task<ProjectItem> RenameProjectAsync(string projectId, string name, CancellationToken cancellationToken = default) =>
        await UpdateProjectAsync(new UpdateProjectRequest
        {
            ProjectId = projectId,
            Name = OptionalValue<string?>.Set(name),
        }, cancellationToken).ConfigureAwait(false);

    public async Task<ProjectItem> UpdateProjectAsync(UpdateProjectRequest request, CancellationToken cancellationToken = default)
    {
        using var lease = AcquirePersonalCloudAuthoringLease();
        var project = await RequireProjectByIdAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.IntegrationId != IntegrationIds.Local &&
            (request.Description.IsSpecified || request.Color.IsSpecified || request.Icon.IsSpecified ||
             request.Status.IsSpecified || request.IsFavorite.IsSpecified))
            throw new ArgumentException("Only local projects can be edited.", nameof(request));
        var name = request.Name.IsSpecified ? RequireName(request.Name.Value, "project") : project.Name;
        var status = request.Status.IsSpecified ? ProjectLifecycleStates.Normalize(request.Status.Value) : project.Status;
        var updated = project with
        {
            Name = name,
            Description = request.Description.IsSpecified ? NullIfEmpty(request.Description.Value) : project.Description,
            Color = request.Color.IsSpecified ? WorkspaceColorValidation.Normalize(request.Color.Value, "#808080", nameof(request)) : project.Color,
            Icon = request.Icon.IsSpecified ? NullIfEmpty(request.Icon.Value) : project.Icon,
            Status = status,
            IsArchived = request.Status.IsSpecified ? status == ProjectLifecycleStates.Archived : project.IsArchived,
            IsFavorite = request.IsFavorite.IsSpecified ? request.IsFavorite.Value : project.IsFavorite,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        if (project.IntegrationId != IntegrationIds.Local)
        {
            await Store.UpsertProjectAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        await UpsertProjectWithCloudAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task DeleteProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        using var lease = AcquirePersonalCloudAuthoringLease();
        var project = await RequireProjectByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project.IntegrationId != IntegrationIds.Local)
            throw new ArgumentException("Only local projects can be deleted.", nameof(projectId));
        if (Store is SqliteTaskStore sqlite && PersonalCloudTarget() is { } cloud &&
            await sqlite.SupportsPersonalCloudWorkspaceAsync(cloud.InstanceId, cancellationToken).ConfigureAwait(false))
            await sqlite.DeleteProjectWithPersonalCloudIntentAsync(projectId, cloud.InstanceId, cloud.DeviceId, cancellationToken).ConfigureAwait(false);
        else
            await Store.DeleteProjectAsync(projectId, moveTasksToInbox: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LabelItem> CreateLabelAsync(string name, string? color = null, string? description = null, CancellationToken cancellationToken = default)
    {
        name = RequireName(name, "label");
        var labels = await Store.GetLabelsAsync(cancellationToken).ConfigureAwait(false);
        var existing = labels.FirstOrDefault(label => string.Equals(label.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null) return existing;
        var label = new LabelItem
        {
            Id = $"label_{Guid.NewGuid():N}",
            Name = name,
            Color = WorkspaceColorValidation.Normalize(color, "#808080", nameof(color)),
            Description = NullIfEmpty(description),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await Store.UpsertLabelAsync(label, cancellationToken).ConfigureAwait(false);
        return (await Store.GetLabelsAsync(cancellationToken).ConfigureAwait(false)).First(item => item.Id == label.Id);
    }

    public async Task<LabelItem> RenameLabelAsync(string labelId, string name, CancellationToken cancellationToken = default)
    {
        using var lease = AcquirePersonalCloudAuthoringLease();
        name = RequireName(name, "label");
        var label = await RequireLabelByIdAsync(labelId, cancellationToken).ConfigureAwait(false);
        if (label.IntegrationId != IntegrationIds.Local)
            throw new OperationRestrictedException("Provider-owned labels cannot be renamed locally.");
        if (PersonalCloudTarget() is not null)
            throw new OperationRestrictedException("Label rename is not synchronized to Personal Cloud yet; disconnect Personal Cloud before renaming labels.");
        if ((await Store.GetLabelsAsync(cancellationToken).ConfigureAwait(false)).Any(item =>
            item.Id != labelId && string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new ArgumentException("A label with that name already exists.", nameof(name));
        var renamed = label with { Name = name };
        await Store.UpsertLabelAsync(renamed, cancellationToken).ConfigureAwait(false);
        return renamed;
    }

    public async Task DeleteLabelAsync(string labelId, CancellationToken cancellationToken = default)
    {
        using var lease = AcquirePersonalCloudAuthoringLease();
        var label = await RequireLabelByIdAsync(labelId, cancellationToken).ConfigureAwait(false);
        if (label.IntegrationId != IntegrationIds.Local)
            throw new OperationRestrictedException("Provider-owned labels cannot be deleted locally.");
        if (PersonalCloudTarget() is not null)
            throw new OperationRestrictedException("Label deletion is not synchronized to Personal Cloud yet; disconnect Personal Cloud before deleting labels.");
        await Store.DeleteLabelAsync(labelId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TaskItem?> ResolveParentTaskAsync(string? parentId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parentId)) return null;
        return await Store.GetTaskAsync(parentId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Parent task '{parentId}' was not found.");
    }

    private async Task<string?> ResolveParentIdAsync(string taskId, string? parentId, string spaceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parentId)) return null;
        if (string.Equals(taskId, parentId, StringComparison.Ordinal))
            throw new ArgumentException("A task cannot be its own parent.", nameof(parentId));
        var parent = await ResolveParentTaskAsync(parentId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Parent task '{parentId}' was not found.");
        if (!string.Equals(parent.SpaceId, spaceId, StringComparison.Ordinal))
            throw new ArgumentException("A subtask must use the same space as its parent.", nameof(parentId));
        var tasks = await Store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All, IncludeSubtasks = true }, cancellationToken).ConfigureAwait(false);
        var byId = tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = parent;
        while (true)
        {
            if (!seen.Add(current.Id))
                throw new InvalidOperationException("The existing task hierarchy contains a cycle.");
            if (string.Equals(current.Id, taskId, StringComparison.Ordinal))
                throw new ArgumentException("Reparenting would create a task hierarchy cycle.", nameof(parentId));
            if (current.ParentId is not { } currentParentId || !byId.TryGetValue(currentParentId, out var ancestor)) break;
            current = ancestor;
        }
        return parent.Id;
    }

    private async Task<SpaceItem> RequireSpaceByIdAsync(string id, CancellationToken cancellationToken) =>
        (await Store.GetSpacesAsync(includeArchived: true, cancellationToken).ConfigureAwait(false)).FirstOrDefault(space => space.Id == id)
        ?? throw new KeyNotFoundException($"Space '{id}' was not found.");

    private async Task<ProjectItem> RequireProjectByIdAsync(string id, CancellationToken cancellationToken) =>
        await Store.GetProjectAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Project '{id}' was not found.");

    private async Task<LabelItem> RequireLabelByIdAsync(string id, CancellationToken cancellationToken) =>
        await Store.GetLabelAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Label '{id}' was not found.");

    private async Task UpsertSpaceWithCloudAsync(SpaceItem space, CancellationToken cancellationToken)
    {
        if (Store is SqliteTaskStore sqlite && PersonalCloudTarget() is { } cloud &&
            await sqlite.SupportsPersonalCloudWorkspaceAsync(cloud.InstanceId, cancellationToken).ConfigureAwait(false))
            await sqlite.UpsertSpaceWithPersonalCloudIntentAsync(space, cloud.InstanceId, cloud.DeviceId, cancellationToken).ConfigureAwait(false);
        else
            await Store.UpsertSpaceAsync(space, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertProjectWithCloudAsync(ProjectItem project, CancellationToken cancellationToken)
    {
        if (project.IntegrationId == IntegrationIds.Local && Store is SqliteTaskStore sqlite && PersonalCloudTarget() is { } cloud &&
            await sqlite.SupportsPersonalCloudWorkspaceAsync(cloud.InstanceId, cancellationToken).ConfigureAwait(false))
            await sqlite.UpsertProjectWithPersonalCloudIntentAsync(project, cloud.InstanceId, cloud.DeviceId, cancellationToken).ConfigureAwait(false);
        else
            await Store.UpsertProjectAsync(project, cancellationToken).ConfigureAwait(false);
    }

    private static string RequireName(string? name, string entity)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? throw new ArgumentException($"A {entity} name is required.", nameof(name)) : trimmed;
    }

    private static (DateTimeOffset? Start, DateTimeOffset? End, int? DurationMinutes) ResolveCreateSchedule(CreateTaskRequest request)
    {
        var start = request.ScheduledStart is { } value ? TruncateToMilliseconds(value) : null;
        var duration = request.DurationMinutes;
        if (duration is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Duration must be greater than zero.");
        if (duration is not null && start is null)
            throw new ArgumentException("A duration requires a scheduled start.", nameof(request));
        DateTimeOffset? end = duration is { } minutes ? start!.Value.AddMinutes(minutes) : null;
        if (!ReminderValues.IsInRange(start) || !ReminderValues.IsInRange(end))
            throw new ArgumentOutOfRangeException(nameof(request), "The scheduled time is out of range.");
        return (start, end, duration);
    }
}
