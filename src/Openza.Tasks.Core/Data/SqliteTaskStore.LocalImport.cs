using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed record LocalImportResult(int ProjectsAdded, int TasksAdded);

public sealed partial class SqliteTaskStore
{
    public async Task<LocalImportResult> AddLocalCopiesAsync(string spaceId, IEnumerable<ProjectItem> projects,
        IEnumerable<TaskItem> tasks, PersonalCloudConnectionProfile? cloud, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await ExistsAsync("SELECT EXISTS(SELECT 1 FROM spaces WHERE id = @id AND is_archived = 0)", spaceId))
            throw new InvalidOperationException("Choose an active Space to import into.");
        if (cloud is not null && !await WorkspaceEnabledAsync(connection, cloud.InstanceId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Update Personal Cloud before importing Microsoft To Do tasks into this workspace.");

        int projectsAdded = 0, tasksAdded = 0;
        var projectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            if (project.SpaceId != spaceId || project.IntegrationId != IntegrationIds.Local)
                throw new InvalidOperationException("Import requires projects owned by the destination Space.");
            if (!await ExistsAsync("SELECT EXISTS(SELECT 1 FROM projects WHERE id = @id)", project.Id))
            {
                if (cloud is not null &&
                    await WorkspaceVersionAsync(connection, cloud.InstanceId, "projects", project.Id, cancellationToken).ConfigureAwait(false) is not null)
                    continue;
                await UpsertProjectCoreAsync(connection, project, cancellationToken).ConfigureAwait(false);
                if (cloud is not null)
                    await AppendWorkspaceMutationAsync(connection, cloud.InstanceId, cloud.DeviceId, "project.put", project.Id,
                        null, ProjectPayload(project), cancellationToken).ConfigureAwait(false);
                projectsAdded++;
            }
            projectIds.Add(project.Id);
        }
        foreach (var task in tasks)
        {
            if (task.SpaceId != spaceId || task.IsProviderTask || task.HasProviderSource)
                throw new InvalidOperationException("Import requires local tasks with no provider link.");
            if (await ExistsAsync("SELECT EXISTS(SELECT 1 FROM tasks WHERE id = @id)", task.Id) ||
                cloud is not null && await ReadPersonalCloudEntityVersionAsync(connection, cloud.InstanceId, task.Id, cancellationToken).ConfigureAwait(false) is not null)
                continue;

            var copy = task.ProjectId is not null && !projectIds.Contains(task.ProjectId) ? task with { ProjectId = null } : task;
            await UpsertTaskCoreAsync(connection, copy, cancellationToken).ConfigureAwait(false);
            if (cloud is not null)
                await AppendRichTaskAsync(connection, copy, cloud.InstanceId, cloud.DeviceId, source: null, cancellationToken).ConfigureAwait(false);
            tasksAdded++;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(projectsAdded, tasksAdded);

        async Task<bool> ExistsAsync(string sql, string id)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@id", id);
            return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 0;
        }
    }
}
