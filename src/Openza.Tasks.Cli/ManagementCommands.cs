using System.CommandLine;
using System.Globalization;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Services;

internal static partial class OpenzaCli
{
    private static Command BuildSpaceManagementCommand(Option<string> format)
    {
        var command = BuildReferenceListCommand("space", "List and manage spaces.", format,
            async (service, ct) => (await service.ListSpacesAsync(ct)).Select(space => new ReferenceRow(space.Id, space.Name, space.Color)).ToList());
        command.Subcommands.Add(BuildSpaceCreateCommand(format));
        command.Subcommands.Add(BuildSpaceRenameCommand(format));
        command.Subcommands.Add(BuildSpaceArchiveCommand(format));
        return command;
    }

    private static Command BuildSpaceCreateCommand(Option<string> format)
    {
        var name = new Argument<string>("name");
        var color = new Option<string?>("--color") { Description = "Space color in the desktop-supported color value." };
        var icon = new Option<string?>("--icon") { Description = "Optional space icon." };
        var command = new Command("create", "Create a space.");
        command.Arguments.Add(name);
        command.Options.Add(color);
        command.Options.Add(icon);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var space = await context.Service.CreateSpaceAsync(parseResult.GetValue(name)!, parseResult.GetValue(color), parseResult.GetValue(icon), cancellationToken);
            WriteSpace(context.Output, space);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildSpaceRenameCommand(Option<string> format)
    {
        var id = new Argument<string>("id");
        var name = new Argument<string>("name");
        var command = new Command("rename", "Rename a space.");
        command.Arguments.Add(id);
        command.Arguments.Add(name);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var space = await context.Service.RenameSpaceAsync(parseResult.GetValue(id)!, parseResult.GetValue(name)!, cancellationToken);
            WriteSpace(context.Output, space);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildSpaceArchiveCommand(Option<string> format)
    {
        var id = new Argument<string>("id");
        var yes = new Option<bool>("--yes") { Description = "Confirm archiving this space." };
        var command = new Command("archive", "Archive a space.");
        command.Arguments.Add(id);
        command.Options.Add(yes);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            if (!parseResult.GetValue(yes))
            {
                context.Output.WriteError(ConfirmationRequired, "confirmation_required", "Archiving a space requires --yes.");
                return ConfirmationRequired;
            }
            var space = await context.Service.ArchiveSpaceAsync(parseResult.GetValue(id)!, cancellationToken);
            WriteSpace(context.Output, space);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildProjectManagementCommand(Option<string> format)
    {
        var command = BuildReferenceListCommand("project", "List and manage projects.", format,
            async (service, ct) => (await service.ListProjectsAsync(cancellationToken: ct)).Select(project => new ReferenceRow(project.Id, project.Name, project.SpaceId)).ToList());
        command.Subcommands.Add(BuildProjectCreateCommand(format));
        command.Subcommands.Add(BuildProjectRenameCommand(format));
        command.Subcommands.Add(BuildProjectUpdateCommand(format));
        command.Subcommands.Add(BuildProjectDeleteCommand(format));
        return command;
    }

    private static Command BuildProjectCreateCommand(Option<string> format)
    {
        var name = new Argument<string>("name");
        var space = new Option<string?>("--space") { Description = "Target space exact ID or unique name." };
        var description = new Option<string?>("--description");
        var color = new Option<string?>("--color") { Description = "Project color in the desktop-supported color value." };
        var icon = new Option<string?>("--icon");
        var command = new Command("create", "Create a project in a specific space.");
        command.Arguments.Add(name);
        foreach (var option in new Option[] { space, description, color, icon }) command.Options.Add(option);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var project = await context.Service.CreateProjectAsync(
                parseResult.GetValue(space), parseResult.GetValue(name)!, parseResult.GetValue(description),
                parseResult.GetValue(color), parseResult.GetValue(icon), cancellationToken);
            WriteProject(context.Output, project);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildProjectRenameCommand(Option<string> format)
    {
        var id = new Argument<string>("id");
        var name = new Argument<string>("name");
        var command = new Command("rename", "Rename a project.");
        command.Arguments.Add(id);
        command.Arguments.Add(name);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var project = await context.Service.RenameProjectAsync(parseResult.GetValue(id)!, parseResult.GetValue(name)!, cancellationToken);
            WriteProject(context.Output, project);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildProjectUpdateCommand(Option<string> format)
    {
        var id = new Argument<string>("id");
        var name = new Option<string?>("--name");
        var description = new Option<string?>("--description");
        var clearDescription = new Option<bool>("--clear-description");
        var color = new Option<string?>("--color");
        var icon = new Option<string?>("--icon");
        var clearIcon = new Option<bool>("--clear-icon");
        var status = new Option<string?>("--status") { Description = "Project lifecycle status: active, completed, or archived." };
        var favorite = new Option<bool>("--favorite") { Description = "Mark the project as a favorite." };
        var unfavorite = new Option<bool>("--unfavorite") { Description = "Remove the project from favorites." };
        var command = new Command("update", "Update project details, color, status, or favorite state.");
        command.Arguments.Add(id);
        foreach (var option in new Option[] { name, description, clearDescription, color, icon, clearIcon, status, favorite, unfavorite }) command.Options.Add(option);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            EnsureNotBoth(parseResult, description, clearDescription);
            EnsureNotBoth(parseResult, icon, clearIcon);
            if (parseResult.GetValue(favorite) && parseResult.GetValue(unfavorite))
                throw new ArgumentException("--favorite and --unfavorite cannot be used together.");
            var request = new UpdateProjectRequest
            {
                ProjectId = parseResult.GetValue(id)!,
                Name = FromOption(parseResult, name),
                Description = FromOptionOrClear(parseResult, description, clearDescription),
                Color = FromOption(parseResult, color),
                Icon = FromOptionOrClear(parseResult, icon, clearIcon),
                Status = FromOption(parseResult, status),
                IsFavorite = parseResult.GetValue(favorite) ? OptionalValue<bool>.Set(true)
                    : parseResult.GetValue(unfavorite) ? OptionalValue<bool>.Set(false) : default,
            };
            if (!request.Name.IsSpecified && !request.Description.IsSpecified && !request.Color.IsSpecified &&
                !request.Icon.IsSpecified && !request.Status.IsSpecified && !request.IsFavorite.IsSpecified)
                throw new ArgumentException("Specify at least one project field to update.");
            var project = await context.Service.UpdateProjectAsync(request, cancellationToken);
            WriteProject(context.Output, project);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildProjectDeleteCommand(Option<string> format)
    {
        var id = new Argument<string>("id");
        var yes = new Option<bool>("--yes") { Description = "Confirm deleting the project and moving its tasks to Inbox." };
        var command = new Command("delete", "Delete a project and move its tasks to Inbox.");
        command.Arguments.Add(id);
        command.Options.Add(yes);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            if (!parseResult.GetValue(yes))
            {
                context.Output.WriteError(ConfirmationRequired, "confirmation_required", "Deleting a project requires --yes.");
                return ConfirmationRequired;
            }
            var projectId = parseResult.GetValue(id)!;
            await context.Service.DeleteProjectAsync(projectId, cancellationToken);
            WriteDeleted(context.Output, projectId);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildLabelManagementCommand(Option<string> format)
    {
        var command = BuildReferenceListCommand("label", "List and manage labels.", format,
            async (service, ct) => (await service.ListLabelsAsync(ct)).Select(label => new ReferenceRow(label.Id, label.Name, label.Color)).ToList());
        command.Subcommands.Add(BuildLabelCreateCommand(format));
        command.Subcommands.Add(BuildLabelRenameCommand(format));
        command.Subcommands.Add(BuildLabelDeleteCommand(format));
        return command;
    }

    private static Command BuildLabelCreateCommand(Option<string> format)
    {
        var name = new Argument<string>("name");
        var color = new Option<string?>("--color");
        var description = new Option<string?>("--description");
        var command = new Command("create", "Create a label; an existing case-insensitive name is returned unchanged.");
        command.Arguments.Add(name);
        command.Options.Add(color);
        command.Options.Add(description);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var label = await context.Service.CreateLabelAsync(parseResult.GetValue(name)!, parseResult.GetValue(color), parseResult.GetValue(description), cancellationToken);
            WriteLabel(context.Output, label);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildLabelRenameCommand(Option<string> format)
    {
        var id = new Argument<string>("id");
        var name = new Argument<string>("name");
        var command = new Command("rename", "Rename a label.");
        command.Arguments.Add(id);
        command.Arguments.Add(name);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var label = await context.Service.RenameLabelAsync(parseResult.GetValue(id)!, parseResult.GetValue(name)!, cancellationToken);
            WriteLabel(context.Output, label);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildLabelDeleteCommand(Option<string> format)
    {
        var id = new Argument<string>("id");
        var yes = new Option<bool>("--yes") { Description = "Confirm deleting the label and removing it from tasks." };
        var command = new Command("delete", "Delete a label and remove it from tasks.");
        command.Arguments.Add(id);
        command.Options.Add(yes);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            if (!parseResult.GetValue(yes))
            {
                context.Output.WriteError(ConfirmationRequired, "confirmation_required", "Deleting a label requires --yes.");
                return ConfirmationRequired;
            }
            var labelId = parseResult.GetValue(id)!;
            await context.Service.DeleteLabelAsync(labelId, cancellationToken);
            WriteDeleted(context.Output, labelId);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildRestorePointCommand(Option<string> format)
    {
        var command = new Command("restore-point", "Create and restore local SQLite restore points.");
        command.Subcommands.Add(BuildRestorePointCreateCommand(format));
        command.Subcommands.Add(BuildRestorePointListCommand(format));
        command.Subcommands.Add(BuildRestorePointRestoreCommand(format));
        return command;
    }

    private static Command BuildRestorePointCreateCommand(Option<string> format)
    {
        var note = new Option<string?>("--note") { Description = "Optional note stored with the restore point." };
        var command = new Command("create", "Create a restore point.");
        command.Options.Add(note);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var backupService = CreateBackupService(context);
            var path = await backupService.CreateBackupAsync(BackupReasons.Manual, cancellationToken, parseResult.GetValue(note));
            var backup = backupService.ListBackupInfo().First(item => item.Path == path);
            WriteBackupInfo(context.Output, backup);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildRestorePointListCommand(Option<string> format)
    {
        var command = new Command("list", "List restore points, newest first.");
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, context =>
        {
            var backups = CreateBackupService(context).ListBackupInfo(ensureStoragePermissions: false);
            WriteBackupInfoList(context.Output, backups);
            return Task.FromResult(Success);
        }, cancellationToken, readOnlyOverride: true));
        return command;
    }

    private static Command BuildRestorePointRestoreCommand(Option<string> format)
    {
        var fileName = new Argument<string>("file-name") { Description = "Exact restore-point filename from restore-point list." };
        var yes = new Option<bool>("--yes") { Description = "Confirm replacing the current local database." };
        var command = new Command("restore", "Restore a listed restore point; a pre-restore safety point is created automatically.");
        command.Arguments.Add(fileName);
        command.Options.Add(yes);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            if (!parseResult.GetValue(yes))
            {
                context.Output.WriteError(ConfirmationRequired, "confirmation_required", "Restoring a database requires --yes.");
                return ConfirmationRequired;
            }
            var backupService = CreateBackupService(context);
            var requestedFileName = parseResult.GetValue(fileName)!;
            var backup = backupService.ListBackupInfo().FirstOrDefault(item => string.Equals(item.FileName, requestedFileName, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException($"Restore point '{requestedFileName}' was not found.");
            await backupService.RestoreBackupAsync(backup.Path, cancellationToken);
            context.Output.Write(new { restored = true, fileName = backup.FileName }, [["Restored", backup.FileName]],
                ["restored", "file_name"], [["true", backup.FileName]]);
            return Success;
        }, cancellationToken, readOnlyOverride: false, databaseReplacement: true));
        return command;
    }

    private static BackupService CreateBackupService(CliContext context) =>
        new(context.Runtime.DatabasePath, context.Runtime.RestorePointDirectory);

    private static void WriteSpace(CliOutput output, SpaceItem space)
    {
        var data = new { space.Id, space.Name, space.Color, space.Icon, space.SortOrder, space.IsArchived };
        output.Write(data, [[space.Id, space.Name, space.Color, space.Icon ?? "", space.SortOrder.ToString(CultureInfo.InvariantCulture), space.IsArchived.ToString()]],
            ["id", "name", "color", "icon", "sort_order", "is_archived"],
            [[space.Id, space.Name, space.Color, space.Icon ?? "", space.SortOrder.ToString(CultureInfo.InvariantCulture), space.IsArchived.ToString().ToLowerInvariant()]]);
    }

    private static void WriteProject(CliOutput output, ProjectItem project)
    {
        var data = new { project.Id, project.SpaceId, project.Name, project.Description, project.Color, project.Icon, project.Status, project.IsArchived, project.IsFavorite };
        output.Write(data, [[project.Id, project.SpaceId, project.Name, project.Description ?? "", project.Color, project.Icon ?? "", project.Status,
                project.IsArchived.ToString(), project.IsFavorite.ToString()]],
            ["id", "space_id", "name", "description", "color", "icon", "status", "is_archived", "is_favorite"],
            [[project.Id, project.SpaceId, project.Name, project.Description ?? "", project.Color, project.Icon ?? "", project.Status,
                project.IsArchived.ToString().ToLowerInvariant(), project.IsFavorite.ToString().ToLowerInvariant()]]);
    }

    private static void WriteLabel(CliOutput output, LabelItem label)
    {
        var data = new { label.Id, label.Name, label.Color, label.Description };
        output.Write(data, [[label.Id, label.Name, label.Color, label.Description ?? ""]],
            ["id", "name", "color", "description"], [[label.Id, label.Name, label.Color, label.Description ?? ""]]);
    }

    private static void WriteDeleted(CliOutput output, string id) =>
        output.Write(new { deleted = true, id }, [["Deleted", id]], ["deleted", "id"], [["true", id]]);

    private static void WriteBackupInfo(CliOutput output, BackupInfo backup)
    {
        output.Write(ToRestorePointRow(backup), [[backup.FileName, backup.CreatedAt.ToString("O", CultureInfo.InvariantCulture), backup.SizeBytes.ToString(CultureInfo.InvariantCulture),
                backup.Reason, backup.Note ?? "", backup.TaskCount?.ToString(CultureInfo.InvariantCulture) ?? "", backup.ProjectCount?.ToString(CultureInfo.InvariantCulture) ?? "",
                backup.SpaceCount?.ToString(CultureInfo.InvariantCulture) ?? "", backup.IntegrityStatus]],
            BackupColumns, [BackupValues(backup)]);
    }

    private static void WriteBackupInfoList(CliOutput output, IReadOnlyList<BackupInfo> backups)
    {
        var rows = backups.Select(ToRestorePointRow).ToList();
        output.Write(rows, backups.Select(backup => new[] { backup.FileName, backup.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                backup.SizeBytes.ToString(CultureInfo.InvariantCulture), backup.Reason, backup.Note ?? "", backup.TaskCount?.ToString(CultureInfo.InvariantCulture) ?? "",
                backup.ProjectCount?.ToString(CultureInfo.InvariantCulture) ?? "", backup.SpaceCount?.ToString(CultureInfo.InvariantCulture) ?? "", backup.IntegrityStatus }),
            BackupColumns, backups.Select(BackupValues));
    }

    private static RestorePointRow ToRestorePointRow(BackupInfo backup) =>
        new(backup.FileName, backup.CreatedAt, backup.SizeBytes, backup.Reason, backup.Note, backup.TaskCount,
            backup.ProjectCount, backup.SpaceCount, backup.IntegrityStatus);

    private sealed record RestorePointRow(
        string FileName,
        DateTime CreatedAt,
        long SizeBytes,
        string Reason,
        string? Note,
        int? TaskCount,
        int? ProjectCount,
        int? SpaceCount,
        string IntegrityStatus);

    private static readonly string[] BackupColumns =
        ["file_name", "created_at", "size_bytes", "reason", "note", "task_count", "project_count", "space_count", "integrity_status"];

    private static string[] BackupValues(BackupInfo backup) =>
        [backup.FileName, backup.CreatedAt.ToString("O", CultureInfo.InvariantCulture), backup.SizeBytes.ToString(CultureInfo.InvariantCulture), backup.Reason,
            backup.Note ?? "", backup.TaskCount?.ToString(CultureInfo.InvariantCulture) ?? "", backup.ProjectCount?.ToString(CultureInfo.InvariantCulture) ?? "",
            backup.SpaceCount?.ToString(CultureInfo.InvariantCulture) ?? "", backup.IntegrityStatus];
}
