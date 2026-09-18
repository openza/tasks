using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Application.Tasks;

public sealed class TaskApplicationService(
    ITaskStore store,
    Func<PersonalCloudConnectionProfile?>? personalCloudConnection = null)
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
        if (PersonalCloudTarget() is { } target && Store is SqliteTaskStore sqliteStore)
        {
            var cloudBaseTask = task with
            {
                SpaceId = SpaceIds.Default,
                ProjectId = null,
                Notes = null,
                WorkflowStatus = TaskWorkflowStatus.Inbox,
                CompletionState = TaskCompletionState.Open,
                CompletedAt = null,
                Priority = 2,
                PlannedOn = null,
                PlannedAt = null,
                DeadlineOn = null,
                DeadlineAt = null,
                Labels = [],
            };
            await sqliteStore.CreateTaskWithPersonalCloudIntentAsync(
                cloudBaseTask, target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
            var created = await RequireTaskAsync(task.Id, cancellationToken).ConfigureAwait(false);
            var hasExtendedState = task.SpaceId != cloudBaseTask.SpaceId || task.ProjectId is not null || task.Notes is not null ||
                task.WorkflowStatus != cloudBaseTask.WorkflowStatus || task.IsCompleted || task.Priority != cloudBaseTask.Priority ||
                task.PlannedOn is not null || task.DeadlineOn is not null || task.Labels.Count > 0;
            if (hasExtendedState)
            {
                return await UpdateTaskAsync(new UpdateTaskRequest
                {
                    TaskId = task.Id,
                    ExpectedRevision = created.Revision,
                    Space = task.SpaceId == cloudBaseTask.SpaceId ? default : OptionalValue<string?>.Set(task.SpaceId),
                    Project = task.ProjectId is null ? default : OptionalValue<string?>.Set(task.ProjectId),
                    Notes = task.Notes is null ? default : OptionalValue<string?>.Set(task.Notes),
                    Status = task.WorkflowStatus == cloudBaseTask.WorkflowStatus ? default : OptionalValue<TaskWorkflowStatus>.Set(task.WorkflowStatus),
                    Completed = task.IsCompleted ? OptionalValue<bool>.Set(true) : default,
                    Priority = task.Priority == cloudBaseTask.Priority ? default : OptionalValue<int>.Set(task.Priority),
                    PlannedOn = task.PlannedOn is null ? default : OptionalValue<DateOnly?>.Set(task.PlannedOn),
                    DeadlineOn = task.DeadlineOn is null ? default : OptionalValue<DateOnly?>.Set(task.DeadlineOn),
                    Labels = task.Labels.Count == 0 ? default : OptionalValue<IReadOnlyList<string>>.Set(task.Labels.Select(label => label.Name).ToArray()),
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await Store.UpsertTaskAsync(task, cancellationToken).ConfigureAwait(false);
        }
        return await Store.GetTaskAsync(task.Id, cancellationToken).ConfigureAwait(false) ?? task;
    }

    public async Task<TaskItem> UpdateTaskAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default)
    {
        var original = await RequireTaskAsync(request.TaskId, cancellationToken).ConfigureAwait(false);
        var expectedRevision = request.ExpectedRevision ?? original.Revision;
        var targetSpace = request.Space.IsSpecified
            ? await ResolveSpaceAsync(request.Space.Value, cancellationToken).ConfigureAwait(false)
            : null;
        var targetSpaceId = targetSpace?.Id ?? original.SpaceId;
        ProjectItem? targetProject = null;
        string? targetProjectId;
        if (request.Project.IsSpecified)
        {
            targetProject = await ResolveProjectAsync(request.Project.Value, targetSpaceId, cancellationToken).ConfigureAwait(false);
            targetProjectId = targetProject?.Id;
        }
        else if (!request.Space.IsSpecified || original.ProjectId is null)
        {
            targetProjectId = original.ProjectId;
        }
        else
        {
            targetProject = (await Store.GetProjectsAsync(targetSpaceId, includeArchived: true, cancellationToken: cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(project => project.Id == original.ProjectId);
            targetProjectId = targetProject?.Id;
        }

        var title = request.Title.IsSpecified ? request.Title.Value?.Trim() : original.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A task title is required.", nameof(request));
        }

        var plannedOn = request.PlannedOn.IsSpecified ? request.PlannedOn.Value : original.PlannedOn;
        var deadlineOn = request.DeadlineOn.IsSpecified ? request.DeadlineOn.Value : original.DeadlineOn;
        var notes = request.Notes.IsSpecified ? NullIfEmpty(request.Notes.Value) : original.Notes;
        var labels = request.Labels.IsSpecified
            ? await ResolveLabelsAsync(request.Labels.Value ?? [], cancellationToken).ConfigureAwait(false)
            : original.Labels;
        var targetCompletionState = request.Completed.IsSpecified
            ? request.Completed.Value ? TaskCompletionState.Completed : TaskCompletionState.Open
            : original.CompletionState;
        var completed = targetCompletionState == TaskCompletionState.Completed;
        var now = DateTimeOffset.UtcNow;
        var cloudScalarChangeCount =
            (request.Title.IsSpecified ? 1 : 0) +
            (request.Status.IsSpecified ? 1 : 0) +
            (request.Priority.IsSpecified ? 1 : 0) +
            (request.PlannedOn.IsSpecified ? 1 : 0) +
            (request.Notes.IsSpecified ? 1 : 0) +
            (request.DeadlineOn.IsSpecified ? 1 : 0) +
            (request.Completed.IsSpecified ? 1 : 0);
        var cloudRelationshipChangeCount =
            (request.Space.IsSpecified ? 1 : 0) +
            (request.Project.IsSpecified ? 1 : 0) +
            (request.Labels.IsSpecified ? 1 : 0);
        var hasUnsupportedChanges = request.LocalMetadataJson.IsSpecified;
        if (cloudScalarChangeCount + cloudRelationshipChangeCount > 0 && !hasUnsupportedChanges &&
            PersonalCloudTarget() is { } target && Store is SqliteTaskStore sqliteStore)
        {
            if (expectedRevision != original.Revision)
                throw new TaskConflictException(original.Id);

            var entityVersion = await ResolvePersonalCloudEntityVersionAsync(
                sqliteStore, target, original.Id, cancellationToken).ConfigureAwait(false);
            var changed = false;
            var reopening = request.Completed.IsSpecified && original.IsCompleted && !completed;
            if (reopening)
            {
                await sqliteStore.ReopenTaskWithPersonalCloudIntentAsync(
                    original.Id, now, entityVersion, target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
                entityVersion = checked(entityVersion + 1);
                changed = true;
            }
            if (targetSpace is not null && !string.Equals(original.SpaceId, targetSpace.Id, StringComparison.Ordinal))
            {
                var treeVersions = await ResolvePersonalCloudTreeVersionsAsync(
                    sqliteStore, target, original, entityVersion, cancellationToken).ConfigureAwait(false);
                await sqliteStore.MoveTaskTreeToSpaceWithPersonalCloudIntentAsync(
                    original.Id, targetSpace.Id, targetSpace.Name, treeVersions,
                    target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
                entityVersion = checked(treeVersions[original.Id] + 1);
                changed = true;
            }
            if (targetProject is not null && !string.Equals(original.ProjectId, targetProject.Id, StringComparison.Ordinal))
            {
                await sqliteStore.MutateTaskRelationshipWithPersonalCloudIntentAsync(
                    original.Id, "task.setProject", targetProject.Id, targetProject.Name, entityVersion,
                    target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
                entityVersion = checked(entityVersion + 1);
                changed = true;
            }
            else if (request.Project.IsSpecified && targetProjectId is null && original.ProjectId is not null &&
                     string.Equals(targetSpaceId, original.SpaceId, StringComparison.Ordinal))
            {
                await sqliteStore.MutateTaskRelationshipWithPersonalCloudIntentAsync(
                    original.Id, "task.setProject", null, null, entityVersion,
                    target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
                entityVersion = checked(entityVersion + 1);
                changed = true;
            }
            if (request.Labels.IsSpecified)
            {
                var originalLabelIds = original.Labels.Select(label => label.Id).ToHashSet(StringComparer.Ordinal);
                var targetLabelIds = labels.Select(label => label.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var removed in original.Labels.Where(label => !targetLabelIds.Contains(label.Id)))
                {
                    await sqliteStore.MutateTaskRelationshipWithPersonalCloudIntentAsync(
                        original.Id, "task.removeLabel", removed.Id, null, entityVersion,
                        target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
                    entityVersion = checked(entityVersion + 1);
                    changed = true;
                }
                foreach (var added in labels.Where(label => !originalLabelIds.Contains(label.Id)))
                {
                    await sqliteStore.MutateTaskRelationshipWithPersonalCloudIntentAsync(
                        original.Id, "task.addLabel", added.Id, added.Name, entityVersion,
                        target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
                    entityVersion = checked(entityVersion + 1);
                    changed = true;
                }
            }
            if (request.Title.IsSpecified && !string.Equals(original.Title, title, StringComparison.Ordinal))
            {
                await sqliteStore.UpdateTaskTitleWithPersonalCloudIntentAsync(
                    original.Id, title!, entityVersion, target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
                entityVersion = checked(entityVersion + 1);
                changed = true;
            }
            if (request.Status.IsSpecified && original.WorkflowStatus != request.Status.Value)
            {
                await sqliteStore.UpdateTaskWorkflowStatusWithPersonalCloudIntentAsync(
                    original.Id, request.Status.Value, entityVersion, target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
                entityVersion = checked(entityVersion + 1);
                changed = true;
            }
            if (request.Priority.IsSpecified)
            {
                var priority = ValidatePriority(request.Priority.Value);
                if (original.Priority != priority)
                {
                    await sqliteStore.UpdateTaskPriorityWithPersonalCloudIntentAsync(
                        original.Id, priority, entityVersion, target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
                    entityVersion = checked(entityVersion + 1);
                    changed = true;
                }
            }
            if (request.PlannedOn.IsSpecified && (original.PlannedOn != plannedOn || original.PlannedAt is not null))
            {
                await sqliteStore.UpdateTaskPlannedOnWithPersonalCloudIntentAsync(
                    original.Id, plannedOn, entityVersion, target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
                entityVersion = checked(entityVersion + 1);
                changed = true;
            }
            if (request.Notes.IsSpecified && !string.Equals(original.Notes, notes, StringComparison.Ordinal))
            {
                await sqliteStore.UpdateTaskNotesWithPersonalCloudIntentAsync(
                    original.Id, notes, entityVersion, target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
                entityVersion = checked(entityVersion + 1);
                changed = true;
            }
            if (request.DeadlineOn.IsSpecified && (original.DeadlineOn != deadlineOn || original.DeadlineAt is not null))
            {
                await sqliteStore.UpdateTaskDeadlineOnWithPersonalCloudIntentAsync(
                    original.Id, deadlineOn, entityVersion, target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
                entityVersion = checked(entityVersion + 1);
                changed = true;
            }
            if (request.Completed.IsSpecified && original.IsCompleted != completed && !reopening)
            {
                if (completed)
                {
                    await sqliteStore.CompleteTaskWithPersonalCloudIntentAsync(
                        original.Id, now, entityVersion, target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await sqliteStore.ReopenTaskWithPersonalCloudIntentAsync(
                        original.Id, now, entityVersion, target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
                }
                changed = true;
            }
            return changed
                ? await RequireTaskAsync(original.Id, cancellationToken).ConfigureAwait(false)
                : original;
        }
        var updated = original with
        {
            Title = title,
            Notes = notes,
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

    public async Task<TaskItem> MoveTaskToSpaceAsync(
        string id,
        string targetSpace,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        var original = await RequireTaskAsync(id, cancellationToken).ConfigureAwait(false);
        if (expectedRevision is not null && expectedRevision.Value != original.Revision)
            throw new TaskConflictException(id);

        var target = await ResolveSpaceAsync(targetSpace, cancellationToken).ConfigureAwait(false);
        if (string.Equals(original.SpaceId, target.Id, StringComparison.Ordinal))
            return original;

        if (PersonalCloudTarget() is { } cloud && Store is SqliteTaskStore sqliteStore)
        {
            var rootVersion = await ResolvePersonalCloudEntityVersionAsync(
                sqliteStore, cloud, original.Id, cancellationToken).ConfigureAwait(false);
            var treeVersions = await ResolvePersonalCloudTreeVersionsAsync(
                sqliteStore, cloud, original, rootVersion, cancellationToken).ConfigureAwait(false);
            await sqliteStore.MoveTaskTreeToSpaceWithPersonalCloudIntentAsync(
                original.Id, target.Id, target.Name, treeVersions,
                cloud.InstanceId, cloud.DeviceId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Store.MoveTaskToSpaceAsync(original.Id, target.Id, cancellationToken).ConfigureAwait(false);
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
        if (expectedRevision is not null && expectedRevision.Value != original.Revision)
            throw new TaskConflictException(id);
        var now = DateTimeOffset.UtcNow;
        if (PersonalCloudTarget() is { } target && Store is SqliteTaskStore sqliteStore)
        {
            var entityVersion = await ResolvePersonalCloudEntityVersionAsync(
                sqliteStore, target, original.Id, cancellationToken).ConfigureAwait(false);
            if (completed)
            {
                await sqliteStore.CompleteTaskWithPersonalCloudIntentAsync(
                    original.Id, now, entityVersion, target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await sqliteStore.ReopenTaskWithPersonalCloudIntentAsync(
                    original.Id, now, entityVersion, target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
            }
            return await RequireTaskAsync(id, cancellationToken).ConfigureAwait(false);
        }
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
        if (expectedRevision is not null && expectedRevision.Value != original.Revision)
            throw new TaskConflictException(id);
        if (PersonalCloudTarget() is { } target && Store is SqliteTaskStore sqliteStore)
        {
            var entityVersion = await ResolvePersonalCloudEntityVersionAsync(
                sqliteStore, target, original.Id, cancellationToken).ConfigureAwait(false);
            await sqliteStore.DeleteTaskWithPersonalCloudIntentAsync(
                original.Id, DateTimeOffset.UtcNow, entityVersion, target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!await Store.TryDeleteTaskAsync(id, expectedRevision ?? original.Revision, cancellationToken).ConfigureAwait(false))
        {
            throw new TaskConflictException(id);
        }
    }

    private async Task<TaskItem> RequireTaskAsync(string id, CancellationToken cancellationToken) =>
        await Store.GetTaskAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Task '{id}' was not found.");

    private PersonalCloudConnectionProfile? PersonalCloudTarget() => personalCloudConnection?.Invoke();

    private static async Task<long> ResolvePersonalCloudEntityVersionAsync(
        SqliteTaskStore store,
        PersonalCloudConnectionProfile target,
        string taskId,
        CancellationToken cancellationToken)
    {
        var version = await store.GetPersonalCloudEntityVersionAsync(
            target.InstanceId, taskId, cancellationToken).ConfigureAwait(false);
        if (version is not null)
            return version.Value;

        await store.EnqueueExistingTaskForPersonalCloudAsync(
            taskId, target.InstanceId, target.DeviceId, cancellationToken).ConfigureAwait(false);
        return 1;
    }

    private async Task<IReadOnlyDictionary<string, long>> ResolvePersonalCloudTreeVersionsAsync(
        SqliteTaskStore store,
        PersonalCloudConnectionProfile target,
        TaskItem root,
        long rootVersion,
        CancellationToken cancellationToken)
    {
        var tasks = await Store.GetTasksAsync(new TaskQuery
        {
            SpaceId = root.SpaceId,
            Kind = TaskListKind.All,
            IncludeSubtasks = true,
        }, cancellationToken).ConfigureAwait(false);
        var children = tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.ParentId))
            .GroupBy(task => task.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var ordered = new List<TaskItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<TaskItem>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var task = pending.Dequeue();
            if (!seen.Add(task.Id))
                throw new InvalidOperationException("The task hierarchy contains a cycle.");
            ordered.Add(task);
            if (children.TryGetValue(task.Id, out var childTasks))
            {
                foreach (var child in childTasks.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id, StringComparer.Ordinal))
                    pending.Enqueue(child);
            }
        }

        var versions = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [root.Id] = rootVersion,
        };
        foreach (var task in ordered.Skip(1))
        {
            versions[task.Id] = await ResolvePersonalCloudEntityVersionAsync(
                store, target, task.Id, cancellationToken).ConfigureAwait(false);
        }
        return versions;
    }

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
