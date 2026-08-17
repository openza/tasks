using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Application.Tasks;

public sealed class TaskApplicationService(ITaskStore store)
{
    public ITaskStore Store { get; } = store;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Store.InitializeAsync(cancellationToken);
    public Task<IReadOnlyList<TaskItem>> ListTasksAsync(TaskQuery query, CancellationToken cancellationToken = default) => Store.GetTasksAsync(query, cancellationToken);
    public Task<TaskItem?> GetTaskAsync(string id, CancellationToken cancellationToken = default) => Store.GetTaskAsync(id, cancellationToken);
    public Task<IReadOnlyList<SpaceItem>> ListSpacesAsync(CancellationToken cancellationToken = default) => Store.GetSpacesAsync(cancellationToken: cancellationToken);
    public Task<IReadOnlyList<ProjectItem>> ListProjectsAsync(string? spaceId = null, CancellationToken cancellationToken = default) => Store.GetProjectsAsync(spaceId, cancellationToken: cancellationToken);
    public Task<IReadOnlyList<LabelItem>> ListLabelsAsync(CancellationToken cancellationToken = default) => Store.GetLabelsAsync(cancellationToken);
    public Task<IReadOnlyList<GlobalSearchResult>> SearchAsync(GlobalSearchQuery query, CancellationToken cancellationToken = default) => Store.SearchAsync(query, cancellationToken);
    public Task<TaskCountSummary> GetCountsAsync(CancellationToken cancellationToken = default) => Store.GetTaskCountsAsync(cancellationToken: cancellationToken);

    public async Task<TaskItem> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
    {
        var title = request.Title.Trim();
        if (title.Length == 0)
        {
            throw new ArgumentException("A task title is required.", nameof(request));
        }

        var space = await ResolveSpaceAsync(request.Space, cancellationToken).ConfigureAwait(false);
        var project = await ResolveProjectAsync(request.Project, space.Id, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var task = new TaskItem
        {
            Id = $"task_{Guid.NewGuid():N}",
            SpaceId = space.Id,
            IntegrationId = IntegrationIds.Local,
            Title = title,
            Notes = NullIfEmpty(request.Notes),
            ProjectId = project?.Id,
            WorkflowStatus = request.Status,
            CompletionState = request.Completed ? TaskCompletionState.Completed : TaskCompletionState.Open,
            CompletedAt = request.Completed ? now : null,
            Priority = ValidatePriority(request.Priority),
            PlannedOn = request.PlannedOn,
            DeadlineOn = request.DeadlineOn,
            CreatedAt = now,
            UpdatedAt = now,
            Labels = await ResolveLabelsAsync(request.Labels, cancellationToken).ConfigureAwait(false),
        };
        await Store.UpsertTaskAsync(task, cancellationToken).ConfigureAwait(false);
        return await Store.GetTaskAsync(task.Id, cancellationToken).ConfigureAwait(false) ?? task;
    }

    public async Task<TaskItem> UpdateTaskAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default)
    {
        var original = await RequireTaskAsync(request.TaskId, cancellationToken).ConfigureAwait(false);
        var expectedRevision = request.ExpectedRevision ?? original.Revision;
        var targetSpaceId = request.Space.IsSpecified
            ? (await ResolveSpaceAsync(request.Space.Value, cancellationToken).ConfigureAwait(false)).Id
            : original.SpaceId;
        string? targetProjectId;
        if (request.Project.IsSpecified)
        {
            targetProjectId = (await ResolveProjectAsync(request.Project.Value, targetSpaceId, cancellationToken).ConfigureAwait(false))?.Id;
        }
        else if (!request.Space.IsSpecified || original.ProjectId is null)
        {
            targetProjectId = original.ProjectId;
        }
        else
        {
            targetProjectId = (await Store.GetProjectsAsync(targetSpaceId, includeArchived: true, cancellationToken: cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(project => project.Id == original.ProjectId)?.Id;
        }

        var title = request.Title.IsSpecified ? request.Title.Value?.Trim() : original.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A task title is required.", nameof(request));
        }

        var plannedOn = request.PlannedOn.IsSpecified ? request.PlannedOn.Value : original.PlannedOn;
        var deadlineOn = request.DeadlineOn.IsSpecified ? request.DeadlineOn.Value : original.DeadlineOn;
        var labels = request.Labels.IsSpecified
            ? await ResolveLabelsAsync(request.Labels.Value ?? [], cancellationToken).ConfigureAwait(false)
            : original.Labels;
        var targetCompletionState = request.Completed.IsSpecified
            ? request.Completed.Value ? TaskCompletionState.Completed : TaskCompletionState.Open
            : original.CompletionState;
        var completed = targetCompletionState == TaskCompletionState.Completed;
        var now = DateTimeOffset.UtcNow;
        var updated = original with
        {
            Title = title,
            Notes = request.Notes.IsSpecified ? NullIfEmpty(request.Notes.Value) : original.Notes,
            SpaceId = targetSpaceId,
            ProjectId = targetProjectId,
            WorkflowStatus = request.Status.IsSpecified ? request.Status.Value : original.WorkflowStatus,
            CompletionState = targetCompletionState,
            CompletedAt = completed ? original.CompletedAt ?? now : null,
            Priority = request.Priority.IsSpecified ? ValidatePriority(request.Priority.Value) : original.Priority,
            PlannedOn = plannedOn,
            PlannedAt = ProviderWriteBackPlanner.PreserveExactTime(plannedOn, original.PlannedOn, original.PlannedAt),
            DeadlineOn = deadlineOn,
            DeadlineAt = ProviderWriteBackPlanner.PreserveExactTime(deadlineOn, original.DeadlineOn, original.DeadlineAt),
            Labels = labels,
            LocalMetadataJson = request.LocalMetadataJson.IsSpecified ? request.LocalMetadataJson.Value : original.LocalMetadataJson,
            UpdatedAt = now,
        };
        var pendingCompletion = original.IsCompleted == completed
            ? null
            : ProviderWriteBackPlanner.CreateCompletion(original, completed, now);
        var pendingDate = ProviderWriteBackPlanner.CreateTodoistDateUpdate(original, updated, DateTimeOffset.UtcNow);
        if (!await Store.TryUpsertTaskWithPendingUpdatesAsync(updated, expectedRevision, pendingCompletion, pendingDate, cancellationToken).ConfigureAwait(false))
        {
            throw new TaskConflictException(original.Id);
        }
        return await RequireTaskAsync(original.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaskItem> SetCompletedAsync(string id, bool completed, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        var original = await RequireTaskAsync(id, cancellationToken).ConfigureAwait(false);
        if (original.IsCompleted == completed)
        {
            return original;
        }
        var now = DateTimeOffset.UtcNow;
        var updated = original with
        {
            CompletionState = completed ? TaskCompletionState.Completed : TaskCompletionState.Open,
            CompletedAt = completed ? original.CompletedAt ?? now : null,
            UpdatedAt = now,
        };
        var pending = ProviderWriteBackPlanner.CreateCompletion(original, completed, now);
        if (!await Store.TryUpsertTaskWithPendingUpdatesAsync(updated, expectedRevision ?? original.Revision, pending, null, cancellationToken).ConfigureAwait(false))
        {
            throw new TaskConflictException(id);
        }
        return await RequireTaskAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteTaskAsync(string id, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        var original = await RequireTaskAsync(id, cancellationToken).ConfigureAwait(false);
        if (!await Store.TryDeleteTaskAsync(id, expectedRevision ?? original.Revision, cancellationToken).ConfigureAwait(false))
        {
            throw new TaskConflictException(id);
        }
    }

    private async Task<TaskItem> RequireTaskAsync(string id, CancellationToken cancellationToken) =>
        await Store.GetTaskAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Task '{id}' was not found.");

    private async Task<SpaceItem> ResolveSpaceAsync(string? value, CancellationToken cancellationToken)
    {
        var spaces = await Store.GetSpacesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(value))
        {
            return spaces.FirstOrDefault(space => space.Id == SpaceIds.Default)
                ?? spaces.FirstOrDefault()
                ?? throw new ReferenceResolutionException("No active space is available.");
        }
        return Resolve(value, spaces, item => item.Id, item => item.Name, "space");
    }

    private async Task<ProjectItem?> ResolveProjectAsync(string? value, string spaceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var projects = await Store.GetProjectsAsync(spaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Resolve(value, projects, item => item.Id, item => item.Name, "project");
    }

    private async Task<IReadOnlyList<LabelItem>> ResolveLabelsAsync(IReadOnlyList<string> values, CancellationToken cancellationToken)
    {
        var existing = await Store.GetLabelsAsync(cancellationToken).ConfigureAwait(false);
        var labels = new List<LabelItem>();
        foreach (var value in values.Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            var exactId = existing.FirstOrDefault(label => string.Equals(label.Id, value, StringComparison.Ordinal));
            if (exactId is not null)
            {
                labels.Add(exactId);
                continue;
            }

            var nameMatches = existing
                .Where(label => string.Equals(label.Name, value, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            if (nameMatches.Count > 1)
            {
                throw new ReferenceResolutionException($"More than one label matches '{value}'. Use its exact id.");
            }

            // A value that is neither an existing id nor name intentionally creates a local label.
            labels.Add(nameMatches.Count == 1
                ? nameMatches[0]
                : new LabelItem { Id = $"label_{Guid.NewGuid():N}", Name = value, IntegrationId = IntegrationIds.Local });
        }
        return labels;
    }

    private static T Resolve<T>(string value, IReadOnlyList<T> items, Func<T, string> id, Func<T, string> name, string kind)
    {
        var exact = items.FirstOrDefault(item => string.Equals(id(item), value, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }
        var matches = items.Where(item => string.Equals(name(item), value, StringComparison.CurrentCultureIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ReferenceResolutionException($"No {kind} matches '{value}'."),
            _ => throw new ReferenceResolutionException($"More than one {kind} matches '{value}'. Use its exact id."),
        };
    }

    private static int ValidatePriority(int value) => value is >= 1 and <= 4
        ? value
        : throw new ArgumentOutOfRangeException(nameof(value), "Priority must be between 1 and 4.");
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
