using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Application.Sync;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;

return await OpenzaCli.RunAsync(args);

internal static class OpenzaCli
{
    private static readonly HttpClient SyncHttpClient = new();
    internal static HttpClient SyncHttpClientForDependencies => SyncHttpClient;
    internal const int JsonSchemaVersion = 2;
    private const int Success = 0;
    private const int UnexpectedError = 1;
    private const int InvalidArguments = 2;
    private const int NotFoundOrAmbiguous = 3;
    private const int Conflict = 4;
    private const int ConfirmationRequired = 5;
    private const int OperationRestricted = 6;

    public static Task<int> RunAsync(string[] args) => RunAsync(args, CliDependencies.Default);

    internal static async Task<int> RunAsync(string[] args, CliDependencies dependencies)
    {
        var root = BuildRootCommand(dependencies);
        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0 || parseResult.UnmatchedTokens.Count > 0)
        {
            var messages = parseResult.Errors.Select(error => error.Message)
                .Concat(parseResult.UnmatchedTokens.Select(token => $"Unrecognized command or argument '{token}'."))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            WriteError(GetRequestedFormat(args), InvalidArguments, "invalid_arguments", messages[0], messages);
            return InvalidArguments;
        }
        return await parseResult.InvokeAsync();
    }

    private static RootCommand BuildRootCommand(CliDependencies dependencies)
    {
        var format = new Option<string>("--format")
        {
            Description = "Output format: text, json, or tsv.",
            DefaultValueFactory = _ => "text",
            Recursive = true,
        };
        format.Aliases.Add("-f");
        format.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && result.Tokens[0].Value is not ("text" or "json" or "tsv"))
            {
                result.AddError("--format must be text, json, or tsv.");
            }
        });

        var root = new RootCommand("Openza Tasks command line interface");
        root.Options.Add(format);
        root.Subcommands.Add(BuildStatusCommand(format));
        root.Subcommands.Add(BuildSearchCommand(format));
        root.Subcommands.Add(BuildTaskCommand(format));
        root.Subcommands.Add(BuildSpaceCommand(format));
        root.Subcommands.Add(BuildProjectCommand(format));
        root.Subcommands.Add(BuildLabelCommand(format));
        root.Subcommands.Add(BuildSyncCommand(format, dependencies));
        return root;
    }

    private static Command BuildSyncCommand(Option<string> format, CliDependencies dependencies)
    {
        var sync = new Command("sync", "Inspect or push explicitly queued provider changes.");
        sync.Subcommands.Add(BuildSyncStatusCommand(format, dependencies));
        sync.Subcommands.Add(BuildSyncRunCommand(format, dependencies));
        return sync;
    }

    private static Command BuildSyncStatusCommand(Option<string> format, CliDependencies dependencies)
    {
        var (provider, direction, scope) = CreateSyncContractOptions();
        var command = new Command("status", "Read local Todoist sync readiness and pending-write counts; no provider request is made.");
        command.Options.Add(provider);
        command.Options.Add(direction);
        command.Options.Add(scope);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            if (!OperatingSystem.IsLinux())
            {
                const string message = "CLI provider sync is currently supported only on Linux.";
                context.Output.WriteError(OperationRestricted, "operation_restricted", message);
                return OperationRestricted;
            }
            var service = CreateSyncService(context, dependencies);
            var preflight = await service.GetPreflightAsync(
                parseResult.GetValue(provider)!,
                parseResult.GetValue(direction)!,
                parseResult.GetValue(scope)!,
                cancellationToken);
            WriteSyncPreflight(context.Output, preflight);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildSyncRunCommand(Option<string> format, CliDependencies dependencies)
    {
        var (provider, direction, scope) = CreateSyncContractOptions();
        var yes = new Option<bool>("--yes")
        {
            Description = "Confirm sending the reported queued changes to Todoist.",
        };
        var command = new Command("run", "Push queued completion, reopen, and date changes only; does not pull or configure providers.");
        command.Options.Add(provider);
        command.Options.Add(direction);
        command.Options.Add(scope);
        command.Options.Add(yes);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            if (!OperatingSystem.IsLinux())
            {
                const string message = "CLI provider sync is currently supported only on Linux.";
                context.Output.WriteError(OperationRestricted, "operation_restricted", message);
                return OperationRestricted;
            }
            var providerValue = parseResult.GetValue(provider)!;
            var directionValue = parseResult.GetValue(direction)!;
            var scopeValue = parseResult.GetValue(scope)!;
            using var syncLease = ChannelRuntimeLease.AcquireProviderSync(context.Runtime, providerValue);
            var pending = await context.Store.GetPendingProviderWriteSummaryAsync(providerValue, cancellationToken);
            if (pending.Total == 0)
            {
                var noOpResult = new ProviderPushResult(
                    providerValue,
                    directionValue,
                    scopeValue,
                    Success: true,
                    pending,
                    new PendingProviderWriteSummary(0, 0, 0),
                    pending);
                WriteSyncResult(context.Output, noOpResult);
                return Success;
            }
            if (!parseResult.GetValue(yes))
            {
                var message = $"Confirmation required to push {pending.Total} queued Todoist changes " +
                    $"({pending.Completions} completions, {pending.Reopens} reopens, " +
                    $"{pending.DateUpdates} date updates). Re-run with --yes.";
                context.Output.WriteError(ConfirmationRequired, "confirmation_required", message);
                return ConfirmationRequired;
            }
            var service = CreateSyncService(context, dependencies);
            var preflight = await service.GetPreflightAsync(providerValue, directionValue, scopeValue, cancellationToken);
            if (!preflight.Ready)
            {
                const string message = "Todoist is not ready for CLI sync. Connect and enable it in Openza Tasks Settings first.";
                context.Output.WriteError(OperationRestricted, "operation_restricted", message);
                return OperationRestricted;
            }

            var result = await service.PushPendingAsync(providerValue, directionValue, scopeValue, cancellationToken);
            if (!result.Success)
            {
                context.Output.WriteError(UnexpectedError, "sync_failed",
                    $"Todoist pending-write push failed: {result.Error} Remaining queued changes: {result.Remaining.Total}.");
                return UnexpectedError;
            }

            WriteSyncResult(context.Output, result);
            return Success;
        }, cancellationToken, readOnlyOverride: !parseResult.GetValue(yes)));
        return command;
    }

    private static (Option<string> Provider, Option<string> Direction, Option<string> Scope) CreateSyncContractOptions()
    {
        var provider = new Option<string>("--provider")
        {
            Description = "Required provider: todoist.",
            Required = true,
        };
        provider.Validators.Add(result => ValidateExactSyncValue(result, "todoist"));
        var direction = new Option<string>("--direction")
        {
            Description = "Required direction: push. Pull and bidirectional sync remain GUI-only.",
            Required = true,
        };
        direction.Validators.Add(result => ValidateExactSyncValue(result, "push"));
        var scope = new Option<string>("--scope")
        {
            Description = "Required scope: pending. Sends only queued task changes.",
            Required = true,
        };
        scope.Validators.Add(result => ValidateExactSyncValue(result, "pending"));
        return (provider, direction, scope);
    }

    private static void ValidateExactSyncValue(OptionResult result, string expected)
    {
        if (result.Tokens.Count > 0 && !string.Equals(result.Tokens[0].Value, expected, StringComparison.Ordinal))
        {
            result.AddError($"{result.Option.Name} must be {expected}.");
        }
    }

    private static ProviderSyncApplicationService CreateSyncService(CliContext context, CliDependencies dependencies) =>
        new(
            context.Store,
            dependencies.CreateCredentialStore(context.Runtime),
            dependencies.CreateProvider);

    private static void WriteSyncResult(CliOutput output, ProviderPushResult result) =>
        output.Write(
            result,
            [["Provider", result.Provider], ["Direction", result.Direction], ["Scope", result.Scope],
             ["Applied", result.Applied.Total.ToString(CultureInfo.InvariantCulture)],
             ["Remaining", result.Remaining.Total.ToString(CultureInfo.InvariantCulture)]],
            ["provider", "direction", "scope", "planned", "applied", "remaining", "success"],
            [[result.Provider, result.Direction, result.Scope,
              result.Planned.Total.ToString(CultureInfo.InvariantCulture),
              result.Applied.Total.ToString(CultureInfo.InvariantCulture),
              result.Remaining.Total.ToString(CultureInfo.InvariantCulture),
              result.Success.ToString().ToLowerInvariant()]]);

    private static void WriteSyncPreflight(CliOutput output, ProviderSyncPreflight preflight) =>
        output.Write(
            preflight,
            [["Provider", preflight.Provider], ["Direction", preflight.Direction], ["Scope", preflight.Scope],
             ["Configured", preflight.Configured.ToString()], ["Active", preflight.Active.ToString()],
             ["Credential available", preflight.CredentialAvailable.ToString()],
             ["Pending", preflight.Pending.Total.ToString(CultureInfo.InvariantCulture)]],
            ["provider", "direction", "scope", "configured", "active", "credential_available",
             "pending_completions", "pending_reopens", "pending_date_updates", "total_pending", "last_full_sync_at"],
            [[preflight.Provider, preflight.Direction, preflight.Scope,
              preflight.Configured.ToString().ToLowerInvariant(), preflight.Active.ToString().ToLowerInvariant(),
              preflight.CredentialAvailable.ToString().ToLowerInvariant(),
              preflight.Pending.Completions.ToString(CultureInfo.InvariantCulture),
              preflight.Pending.Reopens.ToString(CultureInfo.InvariantCulture),
              preflight.Pending.DateUpdates.ToString(CultureInfo.InvariantCulture),
              preflight.Pending.Total.ToString(CultureInfo.InvariantCulture),
              preflight.LastFullSyncAt?.ToString("O", CultureInfo.InvariantCulture) ?? ""]]);

    private static Command BuildStatusCommand(Option<string> format)
    {
        var command = new Command("status", "Show the active Openza environment and task counts.");
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var counts = await context.Service.GetCountsAsync(cancellationToken);
            var data = new
            {
                channel = context.Runtime.Channel.ToString().ToLowerInvariant(),
                dataDirectory = context.Runtime.DataDirectory,
                databasePath = context.Runtime.DatabasePath,
                counts,
            };
            context.Output.Write(data, [
                ["Channel", data.channel],
                ["Data directory", data.dataDirectory],
                ["Open", counts.Open.ToString()],
                ["Completed", counts.Completed.ToString()],
            ],
            ["channel", "data_directory", "database_path", "open", "completed"],
            [[data.channel, data.dataDirectory, data.databasePath, counts.Open.ToString(CultureInfo.InvariantCulture), counts.Completed.ToString(CultureInfo.InvariantCulture)]]);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildSearchCommand(Option<string> format)
    {
        var query = new Argument<string>("query") { Description = "Text to search for." };
        var includeCompleted = new Option<bool>("--include-completed") { Description = "Include completed tasks in search results." };
        var limit = new Option<int>("--limit") { Description = "Maximum combined task and project results (1-500).", DefaultValueFactory = _ => 30 };
        var command = new Command("search", "Search tasks and projects across all spaces.");
        command.Arguments.Add(query);
        command.Options.Add(includeCompleted);
        command.Options.Add(limit);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var limitValue = parseResult.GetValue(limit);
            if (limitValue is < 1 or > 500)
            {
                throw new ArgumentException("--limit must be between 1 and 500.");
            }
            var results = await context.Service.SearchAsync(new GlobalSearchQuery
            {
                SearchText = parseResult.GetValue(query) ?? string.Empty,
                IncludeAllSpaces = true,
                IncludeCompletedTasks = parseResult.GetValue(includeCompleted),
                Limit = limitValue,
            }, cancellationToken);
            var data = results.Select(item => new
            {
                kind = item.Kind.ToString().ToLowerInvariant(), item.Id, item.SpaceId, item.Title, item.Subtitle, item.Snippet,
            }).ToList();
            context.Output.Write(data,
                results.Select(item => new[] { item.Kind.ToString(), item.Id, item.Title, item.Subtitle }),
                ["kind", "id", "space_id", "title", "subtitle", "snippet"],
                data.Select(item => new[] { item.kind, item.Id, item.SpaceId, item.Title, item.Subtitle, item.Snippet }));
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildTaskCommand(Option<string> format)
    {
        var task = new Command("task", "List and manage tasks.");
        task.Subcommands.Add(BuildTaskListCommand(format));
        task.Subcommands.Add(BuildTaskShowCommand(format));
        task.Subcommands.Add(BuildTaskAddCommand(format));
        task.Subcommands.Add(BuildTaskUpdateCommand(format));
        task.Subcommands.Add(BuildTaskCompletionCommand("complete", true, format));
        task.Subcommands.Add(BuildTaskCompletionCommand("reopen", false, format));
        task.Subcommands.Add(BuildTaskDeleteCommand(format));
        return task;
    }

    private static Command BuildTaskListCommand(Option<string> format)
    {
        var view = new Option<string>("--view") { Description = "View: open, inbox, next (alias: next-actions), waiting, someday, today, calendar, overdue, completed, or all.", DefaultValueFactory = _ => "open" };
        var space = new Option<string?>("--space") { Description = "Space exact ID or unique name." };
        var project = new Option<string?>("--project") { Description = "Project exact ID or unique name." };
        var label = new Option<string?>("--label") { Description = "Label exact ID or unique name." };
        var search = new Option<string?>("--search") { Description = "Filter task title, notes, or source description." };
        var includeSubtasks = new Option<bool>("--include-subtasks") { Description = "Include nested subtasks; default lists top-level tasks only." };
        var command = new Command("list", "List tasks.");
        foreach (var option in new Option[] { view, space, project, label, search, includeSubtasks }) command.Options.Add(option);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var selectedSpace = ResolveReference(parseResult.GetValue(space), await context.Service.ListSpacesAsync(cancellationToken), x => x.Id, x => x.Name, "space");
            var spaceId = selectedSpace?.Id;
            var selectedProject = ResolveReference(parseResult.GetValue(project), await context.Service.ListProjectsAsync(spaceId, cancellationToken), x => x.Id, x => x.Name, "project");
            var selectedLabel = ResolveReference(parseResult.GetValue(label), await context.Service.ListLabelsAsync(cancellationToken), x => x.Id, x => x.Name, "label");
            var includeNestedTasks = parseResult.GetValue(includeSubtasks);
            var tasks = await context.Service.ListTasksAsync(new TaskQuery
            {
                Kind = ParseView(parseResult.GetValue(view)),
                SpaceId = spaceId,
                ProjectId = selectedProject?.Id,
                LabelId = selectedLabel?.Id,
                LabelName = selectedLabel?.Name,
                SearchText = parseResult.GetValue(search),
                IncludeSubtasks = true,
            }, cancellationToken);
            if (!includeNestedTasks)
            {
                tasks = tasks.Where(task => string.IsNullOrWhiteSpace(task.ParentId)).ToList();
            }
            WriteTaskList(context.Output, tasks);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildTaskShowCommand(Option<string> format)
    {
        var id = new Argument<string>("id");
        var command = new Command("show", "Show one task by exact id.");
        command.Arguments.Add(id);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var task = await context.Service.GetTaskAsync(parseResult.GetValue(id)!, cancellationToken)
                ?? throw new KeyNotFoundException($"Task '{parseResult.GetValue(id)}' was not found.");
            WriteTaskDetails(context.Output, task);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildTaskAddCommand(Option<string> format)
    {
        var title = new Argument<string>("title");
        var notes = new Option<string?>("--notes");
        var space = new Option<string?>("--space");
        var project = new Option<string?>("--project");
        var status = CreateStatusOption(requiredDefault: "inbox");
        var priority = CreatePriorityOption(requiredDefault: "normal");
        var date = CreateDateOption("--date");
        var deadline = CreateDateOption("--deadline");
        var labels = new Option<string[]>("--label") { AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };
        var command = new Command("add", "Create a local task.");
        command.Arguments.Add(title);
        foreach (var option in new Option[] { notes, space, project, status, priority, date, deadline, labels }) command.Options.Add(option);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var created = await context.Service.CreateTaskAsync(new CreateTaskRequest
            {
                Title = parseResult.GetValue(title)!, Notes = parseResult.GetValue(notes), Space = parseResult.GetValue(space), Project = parseResult.GetValue(project),
                Status = ParseStatus(parseResult.GetValue(status)), Priority = ParsePriority(parseResult.GetValue(priority)), PlannedOn = parseResult.GetValue(date),
                DeadlineOn = parseResult.GetValue(deadline), Labels = parseResult.GetValue(labels) ?? [],
            }, cancellationToken);
            WriteTaskDetails(context.Output, created);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildTaskUpdateCommand(Option<string> format)
    {
        var id = new Argument<string>("id");
        var title = new Option<string?>("--title");
        var notes = new Option<string?>("--notes");
        var clearNotes = new Option<bool>("--clear-notes");
        var space = new Option<string?>("--space");
        var project = new Option<string?>("--project");
        var clearProject = new Option<bool>("--clear-project");
        var status = CreateStatusOption();
        var priority = CreatePriorityOption();
        var date = CreateDateOption("--date");
        var clearDate = new Option<bool>("--clear-date");
        var deadline = CreateDateOption("--deadline");
        var clearDeadline = new Option<bool>("--clear-deadline");
        var labels = new Option<string[]>("--label") { AllowMultipleArgumentsPerToken = true };
        var clearLabels = new Option<bool>("--clear-labels");
        var revision = new Option<long?>("--revision") { Description = "Expected task revision for conflict detection." };
        var command = new Command("update", "Update specified fields on a task.");
        command.Arguments.Add(id);
        foreach (var option in new Option[] { title, notes, clearNotes, space, project, clearProject, status, priority, date, clearDate, deadline, clearDeadline, labels, clearLabels, revision }) command.Options.Add(option);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            EnsureNotBoth(parseResult, project, clearProject); EnsureNotBoth(parseResult, notes, clearNotes); EnsureNotBoth(parseResult, date, clearDate);
            EnsureNotBoth(parseResult, deadline, clearDeadline); EnsureNotBoth(parseResult, labels, clearLabels);
            var updated = await context.Service.UpdateTaskAsync(new UpdateTaskRequest
            {
                TaskId = parseResult.GetValue(id)!, ExpectedRevision = parseResult.GetValue(revision),
                Title = FromOption(parseResult, title), Notes = FromOptionOrClear(parseResult, notes, clearNotes), Space = FromOption(parseResult, space),
                Project = FromOptionOrClear(parseResult, project, clearProject),
                Status = parseResult.GetResult(status) is null ? default : OptionalValue<TaskWorkflowStatus>.Set(ParseStatus(parseResult.GetValue(status))),
                Priority = parseResult.GetResult(priority) is null ? default : OptionalValue<int>.Set(ParsePriority(parseResult.GetValue(priority))),
                PlannedOn = FromOptionOrClear(parseResult, date, clearDate), DeadlineOn = FromOptionOrClear(parseResult, deadline, clearDeadline),
                Labels = parseResult.GetValue(clearLabels) ? OptionalValue<IReadOnlyList<string>>.Set([])
                    : parseResult.GetResult(labels) is null ? default : OptionalValue<IReadOnlyList<string>>.Set(parseResult.GetValue(labels) ?? []),
            }, cancellationToken);
            WriteTaskDetails(context.Output, updated);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildTaskCompletionCommand(string name, bool completed, Option<string> format)
    {
        var id = new Argument<string>("id");
        var revision = new Option<long?>("--revision");
        var command = new Command(name, completed ? "Complete a task." : "Reopen a task.");
        command.Arguments.Add(id); command.Options.Add(revision);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var updated = await context.Service.SetCompletedAsync(parseResult.GetValue(id)!, completed, parseResult.GetValue(revision), cancellationToken);
            WriteTaskDetails(context.Output, updated);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildTaskDeleteCommand(Option<string> format)
    {
        var id = new Argument<string>("id");
        var yes = new Option<bool>("--yes");
        var revision = new Option<long?>("--revision") { Description = "Expected task revision for conflict detection." };
        var command = new Command("delete", "Permanently delete a local task.");
        command.Arguments.Add(id);
        command.Options.Add(yes);
        command.Options.Add(revision);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            if (!parseResult.GetValue(yes))
            {
                context.Output.WriteError(ConfirmationRequired, "confirmation_required", "Deletion requires --yes.");
                return ConfirmationRequired;
            }
            await context.Service.DeleteTaskAsync(parseResult.GetValue(id)!, parseResult.GetValue(revision), cancellationToken);
            context.Output.Write(new { deleted = true, id = parseResult.GetValue(id) }, [["Deleted", parseResult.GetValue(id)!]],
                ["deleted", "id"], [["true", parseResult.GetValue(id)!]]);
            return Success;
        }, cancellationToken));
        return command;
    }

    private static Command BuildSpaceCommand(Option<string> format) => BuildReferenceListCommand("space", "List active spaces.", format,
        async (service, ct) => (await service.ListSpacesAsync(ct)).Select(x => new ReferenceRow(x.Id, x.Name, x.Color)).ToList());
    private static Command BuildProjectCommand(Option<string> format) => BuildReferenceListCommand("project", "List active projects.", format,
        async (service, ct) => (await service.ListProjectsAsync(cancellationToken: ct)).Select(x => new ReferenceRow(x.Id, x.Name, x.SpaceId)).ToList());
    private static Command BuildLabelCommand(Option<string> format) => BuildReferenceListCommand("label", "List labels.", format,
        async (service, ct) => (await service.ListLabelsAsync(ct)).Select(x => new ReferenceRow(x.Id, x.Name, x.Color)).ToList());

    private static Command BuildReferenceListCommand(string name, string description, Option<string> format, Func<TaskApplicationService, CancellationToken, Task<IReadOnlyList<ReferenceRow>>> load)
    {
        var parent = new Command(name, description); var list = new Command("list", description); parent.Subcommands.Add(list);
        list.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var rows = await load(context.Service, cancellationToken);
            context.Output.Write(rows, rows.Select(x => new[] { x.Id, x.Name, x.Detail }),
                ["id", "name", "detail"], rows.Select(x => new[] { x.Id, x.Name, x.Detail })); return Success;
        }, cancellationToken));
        return parent;
    }

    private static async Task<int> ExecuteAsync(
        ParseResult parseResult,
        Option<string> format,
        Func<CliContext, Task<int>> action,
        CancellationToken cancellationToken,
        bool? readOnlyOverride = null)
    {
        var formatValue = parseResult.GetValue(format) ?? "text";
        try
        {
            var runtime = ResolveRuntime();
            var readOnly = readOnlyOverride ?? IsReadOnlyCommand(parseResult);
            using var lease = ChannelRuntimeLease.AcquireShared(runtime);
            using var databaseLease = ChannelRuntimeLease.AcquireDatabaseRead(runtime);
            var store = new SqliteTaskStore(runtime.DatabasePath, readOnly);
            var service = new TaskApplicationService(store);
            await service.InitializeAsync(cancellationToken);
            return await action(new CliContext(runtime, store, service, new CliOutput(formatValue)));
        }
        catch (TaskConflictException exception) { WriteError(formatValue, Conflict, "conflict", exception.Message); return Conflict; }
        catch (ProviderLinkedTaskDeleteException exception) { WriteError(formatValue, OperationRestricted, "operation_restricted", exception.Message); return OperationRestricted; }
        catch (Exception exception) when (exception is KeyNotFoundException or ReferenceResolutionException)
        { WriteError(formatValue, NotFoundOrAmbiguous, "not_found_or_ambiguous", exception.Message); return NotFoundOrAmbiguous; }
        catch (ArgumentException exception) { WriteError(formatValue, InvalidArguments, "invalid_arguments", exception.Message); return InvalidArguments; }
        catch (Exception exception) { WriteError(formatValue, UnexpectedError, "unexpected_error", exception.Message); return UnexpectedError; }
    }

    private static OpenzaRuntimeContext ResolveRuntime()
    {
        var channel = OpenzaRuntimeContext.ReadChannel(typeof(OpenzaCli).Assembly);
        var devOverride = channel == OpenzaChannel.Dev ? Environment.GetEnvironmentVariable("OPENZA_TASKS_DEV_DATA_DIR") : null;
        return OpenzaRuntimeContext.Create(channel, devOverride);
    }

    private static void WriteTaskList(CliOutput output, IEnumerable<TaskItem> tasks)
    {
        var rows = tasks.Select(task => new TaskRow(task.Id, task.Title, task.SpaceId, task.ProjectId, task.WorkflowStatus.ToString().ToLowerInvariant(),
            task.IsCompleted, PriorityName(task.Priority), task.Priority, task.PlannedOn, task.DeadlineOn, task.Notes,
            task.Labels.Select(label => label.Name).ToArray(), !string.IsNullOrWhiteSpace(task.RecurrenceRule), task.RecurrenceRule, task.Revision)).ToList();
        output.Write(rows,
            rows.Select(x => new[] { x.Id, x.Title, x.Status, x.Priority, x.PlannedOn?.ToString("yyyy-MM-dd") ?? "", x.Completed.ToString() }),
            ["id", "title", "status", "priority", "planned_on", "completed"],
            rows.Select(x => new[] { x.Id, x.Title, x.Status, x.Priority, x.PlannedOn?.ToString("yyyy-MM-dd") ?? "", x.Completed.ToString().ToLowerInvariant() }));
    }

    private static void WriteTaskDetails(CliOutput output, TaskItem task)
    {
        var row = new TaskRow(task.Id, task.Title, task.SpaceId, task.ProjectId, task.WorkflowStatus.ToString().ToLowerInvariant(),
            task.IsCompleted, PriorityName(task.Priority), task.Priority, task.PlannedOn, task.DeadlineOn, task.Notes,
            task.Labels.Select(label => label.Name).ToArray(), !string.IsNullOrWhiteSpace(task.RecurrenceRule), task.RecurrenceRule, task.Revision);
        var textValues = new[]
        {
            row.Id, row.Title, row.SpaceId, row.ProjectId ?? "", row.Status, row.Completed.ToString().ToLowerInvariant(), row.Priority,
            row.PlannedOn?.ToString("yyyy-MM-dd") ?? "", row.DeadlineOn?.ToString("yyyy-MM-dd") ?? "", row.Notes ?? "",
            string.Join(",", row.Labels), row.Revision.ToString(CultureInfo.InvariantCulture),
        };
        var tsvValues = (string[])textValues.Clone();
        tsvValues[10] = JsonSerializer.Serialize(row.Labels);
        output.WriteTaskDetails(
            row,
            textValues,
            tsvValues);
    }

    private static OptionalValue<T> FromOption<T>(ParseResult result, Option<T> option) => result.GetResult(option) is null ? default : OptionalValue<T>.Set(result.GetValue(option));
    private static OptionalValue<T> FromOptionOrClear<T>(ParseResult result, Option<T> option, Option<bool> clear) => result.GetValue(clear) ? OptionalValue<T>.Set(default) : FromOption(result, option);
    private static void EnsureNotBoth<T>(ParseResult result, Option<T> value, Option<bool> clear)
    { if (result.GetResult(value) is not null && result.GetValue(clear)) throw new ArgumentException($"{value.Name} and {clear.Name} cannot be used together."); }

    private static T? ResolveReference<T>(string? value, IReadOnlyList<T> items, Func<T, string> id, Func<T, string> name, string kind)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var exact = items.FirstOrDefault(x => id(x) == value); if (exact is not null) return exact;
        var matches = items.Where(x => string.Equals(name(x), value, StringComparison.CurrentCultureIgnoreCase)).ToList();
        return matches.Count switch { 1 => matches[0], 0 => throw new ReferenceResolutionException($"No {kind} matches '{value}'."), _ => throw new ReferenceResolutionException($"More than one {kind} matches '{value}'. Use its exact id.") };
    }

    private static TaskListKind ParseView(string? value) => value?.ToLowerInvariant() switch
    { "inbox" => TaskListKind.Inbox, "next" or "next-actions" => TaskListKind.NextActions, "waiting" => TaskListKind.Waiting, "someday" => TaskListKind.Someday,
      "today" => TaskListKind.Today, "calendar" => TaskListKind.Calendar, "overdue" => TaskListKind.Overdue, "completed" => TaskListKind.Completed, "all" => TaskListKind.All,
      "open" or null => TaskListKind.Open, _ => throw new ArgumentException($"Unknown task view '{value}'.") };
    private static TaskWorkflowStatus ParseStatus(string? value) => value?.ToLowerInvariant() switch
    { "none" => TaskWorkflowStatus.None, "inbox" => TaskWorkflowStatus.Inbox, "next" => TaskWorkflowStatus.Next, "waiting" => TaskWorkflowStatus.Waiting, "someday" => TaskWorkflowStatus.Someday,
      _ => throw new ArgumentException($"Unknown task status '{value}'.") };
    private static int ParsePriority(string? value) => value?.ToLowerInvariant() switch
    { "highest" => 1, "high" => 2, "normal" => 3, "low" => 4, _ => throw new ArgumentException($"Unknown priority '{value}'.") };
    private static string PriorityName(int value) => value switch { 1 => "highest", 2 => "high", 3 => "normal", _ => "low" };

    private static Option<DateOnly?> CreateDateOption(string name)
    {
        var option = new Option<DateOnly?>(name) { Description = "Date in YYYY-MM-DD format." };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && !DateOnly.TryParseExact(
                    result.Tokens[0].Value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                result.AddError($"{name} must use YYYY-MM-DD.");
            }
        });
        return option;
    }

    private static Option<string> CreateStatusOption(string? requiredDefault = null) => new("--status")
    {
        Description = "Workflow status: none, inbox, next, waiting, or someday. None leaves workflow status unassigned.",
        DefaultValueFactory = requiredDefault is null ? null : _ => requiredDefault,
    };

    private static Option<string> CreatePriorityOption(string? requiredDefault = null) => new("--priority")
    {
        Description = "Priority: highest (1), high (2), normal (3), or low (4).",
        DefaultValueFactory = requiredDefault is null ? null : _ => requiredDefault,
    };

    private static bool IsReadOnlyCommand(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;
        return command.Name is "status" or "search" or "list" or "show";
    }

    private static string GetRequestedFormat(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var value = args[index];
            if (value is "--format" or "-f")
            {
                return index + 1 < args.Count ? args[index + 1] : "text";
            }
            if (value.StartsWith("--format=", StringComparison.Ordinal)) return value[9..];
            if (value.StartsWith("-f=", StringComparison.Ordinal)) return value[3..];
        }
        return "text";
    }

    private static void WriteError(string format, int exitCode, string code, string message, IReadOnlyList<string>? details = null)
    {
        if (format == "json")
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = JsonSchemaVersion,
                error = new { code, message, exitCode, details = details ?? [message] },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            return;
        }
        foreach (var detail in details ?? [message])
        {
            Console.Error.WriteLine(detail);
        }
    }

    private sealed record CliContext(OpenzaRuntimeContext Runtime, ITaskStore Store, TaskApplicationService Service, CliOutput Output);
    private sealed record ReferenceRow(string Id, string Name, string Detail);
    private sealed record TaskRow(
        string Id,
        string Title,
        string SpaceId,
        string? ProjectId,
        string Status,
        bool Completed,
        string Priority,
        int PriorityValue,
        DateOnly? PlannedOn,
        DateOnly? DeadlineOn,
        string? Notes,
        string[] Labels,
        bool IsRecurring,
        string? RecurrenceRule,
        long Revision);
}

internal sealed record CliDependencies(
    Func<OpenzaRuntimeContext, ICredentialStore> CreateCredentialStore,
    Func<string, string, ISyncProvider> CreateProvider)
{
    internal static CliDependencies Default { get; } = new(
        runtime => new SecretToolCredentialStore(runtime.CredentialNamespace, runtime.DisplayName),
        (token, connectionId) => new TodoistProvider(OpenzaCli.SyncHttpClientForDependencies, token, connectionId));
}

internal sealed class CliOutput(string format)
{
    private static readonly string[] TaskDetailColumns =
        ["id", "title", "space_id", "project_id", "status", "completed", "priority", "planned_on", "deadline_on", "notes", "labels", "revision"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public void Write<T>(
        T data,
        IEnumerable<string[]> textRows,
        IReadOnlyList<string>? tsvColumns = null,
        IEnumerable<string[]>? tsvRows = null)
    {
        if (format == "json") { Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = OpenzaCli.JsonSchemaVersion, data }, JsonOptions)); return; }
        var rows = textRows.ToList();
        if (format == "tsv")
        {
            if (tsvColumns is null || tsvRows is null)
            {
                throw new InvalidOperationException("A versioned TSV schema is required for this command.");
            }
            WriteTsv(tsvColumns, tsvRows);
            return;
        }
        foreach (var row in rows) Console.WriteLine(string.Join("  ", row));
    }

    public void WriteTaskDetails<T>(T data, string[] textValues, string[] tsvValues)
    {
        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = OpenzaCli.JsonSchemaVersion, data }, JsonOptions));
            return;
        }
        if (format == "tsv")
        {
            WriteTsv(TaskDetailColumns, [tsvValues]);
            return;
        }
        for (var index = 0; index < TaskDetailColumns.Length; index++)
        {
            Console.WriteLine($"{TaskDetailColumns[index]}  {textValues[index]}");
        }
    }

    public void WriteError(int exitCode, string code, string message)
    {
        if (format == "json")
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = OpenzaCli.JsonSchemaVersion,
                error = new { code, message, exitCode, details = new[] { message } },
            }, JsonOptions));
            return;
        }
        Console.Error.WriteLine(message);
    }
    private static void WriteTsv(IReadOnlyList<string> columns, IEnumerable<string[]> rows)
    {
        Console.WriteLine(string.Join('\t', new[] { "schema_version" }.Concat(columns)));
        foreach (var row in rows)
        {
            Console.WriteLine(string.Join('\t', new[] { "1" }.Concat(row).Select(EscapeTsv)));
        }
    }
    private static string EscapeTsv(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}
