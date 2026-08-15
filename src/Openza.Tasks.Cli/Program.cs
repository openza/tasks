using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

return await OpenzaCli.RunAsync(args);

internal static class OpenzaCli
{
    private const int Success = 0;
    private const int UnexpectedError = 1;
    private const int InvalidArguments = 2;
    private const int NotFoundOrAmbiguous = 3;
    private const int Conflict = 4;
    private const int ConfirmationRequired = 5;
    private const int OperationRestricted = 6;

    public static async Task<int> RunAsync(string[] args)
    {
        var root = BuildRootCommand();
        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                Console.Error.WriteLine(error.Message);
            }
            return InvalidArguments;
        }
        return await parseResult.InvokeAsync();
    }

    private static RootCommand BuildRootCommand()
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
        return root;
    }

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
        var includeCompleted = new Option<bool>("--include-completed");
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 30 };
        var command = new Command("search", "Search tasks and projects across all spaces.");
        command.Arguments.Add(query);
        command.Options.Add(includeCompleted);
        command.Options.Add(limit);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var results = await context.Service.SearchAsync(new GlobalSearchQuery
            {
                SearchText = parseResult.GetValue(query) ?? string.Empty,
                IncludeAllSpaces = true,
                IncludeCompletedTasks = parseResult.GetValue(includeCompleted),
                Limit = Math.Clamp(parseResult.GetValue(limit), 1, 500),
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
        var view = new Option<string>("--view") { DefaultValueFactory = _ => "open" };
        var space = new Option<string?>("--space");
        var project = new Option<string?>("--project");
        var label = new Option<string?>("--label");
        var search = new Option<string?>("--search");
        var includeSubtasks = new Option<bool>("--include-subtasks");
        var command = new Command("list", "List tasks.");
        foreach (var option in new Option[] { view, space, project, label, search, includeSubtasks }) command.Options.Add(option);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(parseResult, format, async context =>
        {
            var spaceId = await ResolveIdAsync(parseResult.GetValue(space), await context.Service.ListSpacesAsync(cancellationToken), x => x.Id, x => x.Name, "space");
            var projectId = await ResolveIdAsync(parseResult.GetValue(project), await context.Service.ListProjectsAsync(spaceId, cancellationToken), x => x.Id, x => x.Name, "project");
            var labelId = await ResolveIdAsync(parseResult.GetValue(label), await context.Service.ListLabelsAsync(cancellationToken), x => x.Id, x => x.Name, "label");
            var tasks = await context.Service.ListTasksAsync(new TaskQuery
            {
                Kind = ParseView(parseResult.GetValue(view)),
                SpaceId = spaceId,
                ProjectId = projectId,
                LabelId = labelId,
                SearchText = parseResult.GetValue(search),
                IncludeSubtasks = parseResult.GetValue(includeSubtasks),
            }, cancellationToken);
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
        var status = new Option<string>("--status") { DefaultValueFactory = _ => "inbox" };
        var priority = new Option<string>("--priority") { DefaultValueFactory = _ => "normal" };
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
        var status = new Option<string?>("--status");
        var priority = new Option<string?>("--priority");
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
            if (!parseResult.GetValue(yes)) { Console.Error.WriteLine("Deletion requires --yes."); return ConfirmationRequired; }
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

    private static async Task<int> ExecuteAsync(ParseResult parseResult, Option<string> format, Func<CliContext, Task<int>> action, CancellationToken cancellationToken)
    {
        try
        {
            var runtime = ResolveRuntime();
            using var lease = ChannelRuntimeLease.AcquireShared(runtime);
            var service = new TaskApplicationService(new SqliteTaskStore(runtime.DatabasePath));
            await service.InitializeAsync(cancellationToken);
            return await action(new CliContext(runtime, service, new CliOutput(parseResult.GetValue(format) ?? "text")));
        }
        catch (TaskConflictException exception) { Console.Error.WriteLine(exception.Message); return Conflict; }
        catch (ProviderLinkedTaskDeleteException exception) { Console.Error.WriteLine(exception.Message); return OperationRestricted; }
        catch (Exception exception) when (exception is KeyNotFoundException or ReferenceResolutionException)
        { Console.Error.WriteLine(exception.Message); return NotFoundOrAmbiguous; }
        catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); return InvalidArguments; }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return UnexpectedError; }
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
            task.IsCompleted, task.Priority, task.PlannedOn, task.DeadlineOn, task.Notes, task.Labels.Select(label => label.Name).ToArray(), task.Revision)).ToList();
        output.Write(rows,
            rows.Select(x => new[] { x.Id, x.Title, x.Status, PriorityName(x.Priority), x.PlannedOn?.ToString("yyyy-MM-dd") ?? "", x.Completed.ToString() }),
            ["id", "title", "status", "priority", "planned_on", "completed"],
            rows.Select(x => new[] { x.Id, x.Title, x.Status, PriorityName(x.Priority), x.PlannedOn?.ToString("yyyy-MM-dd") ?? "", x.Completed.ToString().ToLowerInvariant() }));
    }

    private static void WriteTaskDetails(CliOutput output, TaskItem task)
    {
        var row = new TaskRow(task.Id, task.Title, task.SpaceId, task.ProjectId, task.WorkflowStatus.ToString().ToLowerInvariant(),
            task.IsCompleted, task.Priority, task.PlannedOn, task.DeadlineOn, task.Notes, task.Labels.Select(label => label.Name).ToArray(), task.Revision);
        var textValues = new[]
        {
            row.Id, row.Title, row.SpaceId, row.ProjectId ?? "", row.Status, row.Completed.ToString().ToLowerInvariant(), PriorityName(row.Priority),
            row.PlannedOn?.ToString("yyyy-MM-dd") ?? "", row.DeadlineOn?.ToString("yyyy-MM-dd") ?? "", row.Notes ?? "",
            string.Join(",", row.Labels), row.Revision.ToString(CultureInfo.InvariantCulture),
        };
        var tsvValues = (string[])textValues.Clone();
        tsvValues[10] = JsonSerializer.Serialize(row.Labels);
        output.WriteTaskDetails(
            new[] { row },
            textValues,
            tsvValues);
    }

    private static OptionalValue<T> FromOption<T>(ParseResult result, Option<T> option) => result.GetResult(option) is null ? default : OptionalValue<T>.Set(result.GetValue(option));
    private static OptionalValue<T> FromOptionOrClear<T>(ParseResult result, Option<T> option, Option<bool> clear) => result.GetValue(clear) ? OptionalValue<T>.Set(default) : FromOption(result, option);
    private static void EnsureNotBoth<T>(ParseResult result, Option<T> value, Option<bool> clear)
    { if (result.GetResult(value) is not null && result.GetValue(clear)) throw new ArgumentException($"{value.Name} and {clear.Name} cannot be used together."); }

    private static async Task<string?> ResolveIdAsync<T>(string? value, IReadOnlyList<T> items, Func<T, string> id, Func<T, string> name, string kind)
    {
        await Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(value)) return null;
        var exact = items.FirstOrDefault(x => id(x) == value); if (exact is not null) return id(exact);
        var matches = items.Where(x => string.Equals(name(x), value, StringComparison.CurrentCultureIgnoreCase)).ToList();
        return matches.Count switch { 1 => id(matches[0]), 0 => throw new ReferenceResolutionException($"No {kind} matches '{value}'."), _ => throw new ReferenceResolutionException($"More than one {kind} matches '{value}'. Use its exact id.") };
    }

    private static TaskListKind ParseView(string? value) => value?.ToLowerInvariant() switch
    { "inbox" => TaskListKind.Inbox, "next" or "next-actions" => TaskListKind.NextActions, "waiting" => TaskListKind.Waiting, "someday" => TaskListKind.Someday,
      "today" => TaskListKind.Today, "calendar" => TaskListKind.Calendar, "overdue" => TaskListKind.Overdue, "completed" => TaskListKind.Completed, "all" => TaskListKind.All,
      "open" or null => TaskListKind.Open, _ => throw new ArgumentException($"Unknown task view '{value}'.") };
    private static TaskWorkflowStatus ParseStatus(string? value) => value?.ToLowerInvariant() switch
    { "inbox" => TaskWorkflowStatus.Inbox, "next" => TaskWorkflowStatus.Next, "waiting" => TaskWorkflowStatus.Waiting, "someday" => TaskWorkflowStatus.Someday,
      _ => throw new ArgumentException($"Unknown task status '{value}'.") };
    private static int ParsePriority(string? value) => value?.ToLowerInvariant() switch
    { "highest" => 1, "high" => 2, "normal" => 3, "low" => 4, _ => throw new ArgumentException($"Unknown priority '{value}'.") };
    private static string PriorityName(int value) => value switch { 1 => "highest", 2 => "high", 3 => "normal", _ => "low" };

    private static Option<DateOnly?> CreateDateOption(string name)
    {
        var option = new Option<DateOnly?>(name);
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

    private sealed record CliContext(OpenzaRuntimeContext Runtime, TaskApplicationService Service, CliOutput Output);
    private sealed record ReferenceRow(string Id, string Name, string Detail);
    private sealed record TaskRow(string Id, string Title, string SpaceId, string? ProjectId, string Status, bool Completed, int Priority, DateOnly? PlannedOn, DateOnly? DeadlineOn, string? Notes, string[] Labels, long Revision);
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
        if (format == "json") { Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, data }, JsonOptions)); return; }
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
            Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, data }, JsonOptions));
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
