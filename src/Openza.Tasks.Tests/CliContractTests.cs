using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Application.Sync;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class CliContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-cli-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task None_status_can_be_created_updated_and_read_without_changing_defaults()
    {
        var store = await CreateStoreAsync();
        var add = await RunAsync("task", "add", "No workflow", "--status", "none", "--format", "json");
        AssertSuccess(add);
        using var added = JsonDocument.Parse(add.Stdout);
        var id = added.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        Assert.Equal("none", added.RootElement.GetProperty("data").GetProperty("status").GetString());
        AssertSuccess(await RunAsync("task", "update", id, "--status", "next"));
        var before = (await store.GetTaskAsync(id))!;
        AssertSuccess(await RunAsync("task", "update", id, "--status", "none", "--revision", before.Revision.ToString()));
        var conflict = await RunAsync("task", "update", id, "--status", "inbox", "--revision", before.Revision.ToString());
        Assert.NotEqual(0, conflict.ExitCode);
        foreach (var args in new[]
        {
            new[] { "task", "show", id, "--format", "json" },
            new[] { "task", "list", "--view", "open", "--format", "json" },
        })
        {
            var result = await RunAsync(args);
            AssertSuccess(result);
            using var document = JsonDocument.Parse(result.Stdout);
            var data = document.RootElement.GetProperty("data");
            var task = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Single() : data;
            Assert.Equal("none", task.GetProperty("status").GetString());
        }
        AssertSuccess(await RunAsync("task", "add", "Default capture"));
        Assert.Equal(TaskWorkflowStatus.Inbox, (await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Inbox })).Single().WorkflowStatus);
    }

    [Fact]
    public async Task Commands_cover_status_references_search_and_task_lifecycle()
    {
        var store = await CreateStoreAsync();
        await store.UpsertProjectAsync(new ProjectItem { Id = "project_cli", Name = "CLI Project" });
        await store.UpsertLabelAsync(new LabelItem { Id = "label_cli", Name = "CLI Label" });

        var status = await RunAsync("status", "--format", "json");
        AssertSuccess(status);
        using (var document = JsonDocument.Parse(status.Stdout))
        {
            Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("dev", document.RootElement.GetProperty("data").GetProperty("channel").GetString());
        }

        AssertSuccess(await RunAsync("space", "list"));
        Assert.Contains("space_default", (await RunAsync("space", "list")).Stdout);
        Assert.Contains("project_cli", (await RunAsync("project", "list")).Stdout);
        Assert.Contains("label_cli", (await RunAsync("label", "list")).Stdout);

        var add = await RunAsync("task", "add", "CLI contract task", "--project", "project_cli", "--priority", "high",
            "--date", "2026-08-16", "--deadline", "2026-08-20", "--notes", "Initial notes", "--label", "label_cli", "--format", "json");
        AssertSuccess(add);
        string id;
        long revision;
        using (var document = JsonDocument.Parse(add.Stdout))
        {
            var task = document.RootElement.GetProperty("data");
            id = task.GetProperty("id").GetString()!;
            revision = task.GetProperty("revision").GetInt64();
            Assert.Equal("high", task.GetProperty("priority").GetString());
            Assert.Equal(2, task.GetProperty("priorityValue").GetInt32());
        }

        var list = await RunAsync("task", "list", "--project", "CLI Project", "--label", "CLI Label", "--format", "tsv");
        AssertSuccess(list);
        Assert.Contains(id, list.Stdout);
        var search = await RunAsync("search", "contract");
        AssertSuccess(search);
        Assert.Contains(id, search.Stdout);

        var show = await RunAsync("task", "show", id, "--format", "tsv");
        AssertSuccess(show);
        var showLines = SplitOutputLines(show.Stdout);
        Assert.Equal("schema_version\tid\ttitle\tspace_id\tproject_id\tstatus\tcompleted\tpriority\tplanned_on\tdeadline_on\tnotes\tlabels\trevision", showLines[0]);
        Assert.Equal(13, showLines[1].Split('\t').Length);
        Assert.StartsWith("1\t", showLines[1]);
        Assert.Contains("Initial notes", showLines[1]);
        var textShow = await RunAsync("task", "show", id);
        AssertSuccess(textShow);
        Assert.Contains("space_id  space_default", textShow.Stdout);
        Assert.Contains("revision  ", textShow.Stdout);
        var jsonShow = await RunAsync("task", "show", id, "--format", "json");
        AssertSuccess(jsonShow);
        using (var document = JsonDocument.Parse(jsonShow.Stdout))
        {
            Assert.Equal(id, document.RootElement.GetProperty("data").GetProperty("id").GetString());
            Assert.True(document.RootElement.GetProperty("data").TryGetProperty("revision", out _));
        }

        var update = await RunAsync("task", "update", id, "--revision", revision.ToString(), "--clear-project", "--clear-date",
            "--clear-deadline", "--clear-notes", "--clear-labels", "--status", "next", "--format", "json");
        AssertSuccess(update);
        using (var document = JsonDocument.Parse(update.Stdout))
        {
            var task = document.RootElement.GetProperty("data");
            Assert.Equal(JsonValueKind.Null, task.GetProperty("projectId").ValueKind);
            Assert.Equal(JsonValueKind.Null, task.GetProperty("plannedOn").ValueKind);
            Assert.Equal(JsonValueKind.Null, task.GetProperty("deadlineOn").ValueKind);
            Assert.Equal(JsonValueKind.Null, task.GetProperty("notes").ValueKind);
            Assert.Empty(task.GetProperty("labels").EnumerateArray());
            revision = task.GetProperty("revision").GetInt64();
        }

        var completed = await RunAsync("task", "complete", id, "--revision", revision.ToString(), "--format", "json");
        AssertSuccess(completed);
        using (var document = JsonDocument.Parse(completed.Stdout))
        {
            revision = document.RootElement.GetProperty("data").GetProperty("revision").GetInt64();
        }
        AssertSuccess(await RunAsync("task", "reopen", id, "--revision", revision.ToString()));
        var current = (await store.GetTaskAsync(id))!;
        AssertSuccess(await RunAsync("task", "delete", id, "--revision", current.Revision.ToString(), "--yes"));
        Assert.Null(await store.GetTaskAsync(id));
    }

    [Fact]
    public async Task Status_and_default_lists_count_the_same_top_level_tasks()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "task_parent", Title = "Parent" });
        await store.UpsertTaskAsync(new TaskItem { Id = "task_child", Title = "Child", ParentId = "task_parent" });
        await store.UpsertTaskAsync(new TaskItem { Id = "task_next", Title = "Next", WorkflowStatus = TaskWorkflowStatus.Next });
        await store.UpsertTaskAsync(new TaskItem { Id = "task_waiting", Title = "Waiting", WorkflowStatus = TaskWorkflowStatus.Waiting });
        await store.UpsertTaskAsync(new TaskItem { Id = "task_someday", Title = "Someday", WorkflowStatus = TaskWorkflowStatus.Someday });
        await store.UpsertTaskAsync(new TaskItem { Id = "task_today", Title = "Today", PlannedOn = DateOnly.FromDateTime(DateTime.Today) });
        await store.UpsertTaskAsync(new TaskItem { Id = "task_future", Title = "Future", DeadlineOn = DateOnly.FromDateTime(DateTime.Today.AddDays(2)) });
        await store.UpsertTaskAsync(new TaskItem { Id = "task_overdue", Title = "Overdue", PlannedOn = DateOnly.FromDateTime(DateTime.Today.AddDays(-2)) });
        await store.UpsertTaskAsync(new TaskItem { Id = "task_completed", Title = "Completed", CompletionState = TaskCompletionState.Completed });

        var status = await RunAsync("status", "--format", "json");
        var withSubtasks = await RunAsync("task", "list", "--view", "all", "--include-subtasks", "--format", "json");

        AssertSuccess(status);
        AssertSuccess(withSubtasks);
        using var statusJson = JsonDocument.Parse(status.Stdout);
        using var withSubtasksJson = JsonDocument.Parse(withSubtasks.Stdout);
        var counts = statusJson.RootElement.GetProperty("data").GetProperty("counts");
        foreach (var (view, countProperty) in new[]
        {
            ("inbox", "inbox"), ("next", "nextActions"), ("waiting", "waiting"),
            ("someday", "someday"), ("today", "today"), ("calendar", "calendar"),
            ("overdue", "overdue"), ("open", "open"), ("all", "all"),
            ("completed", "completed"),
        })
        {
            var list = await RunAsync("task", "list", "--view", view, "--format", "json");
            AssertSuccess(list);
            using var listJson = JsonDocument.Parse(list.Stdout);
            Assert.Equal(
                counts.GetProperty(countProperty).GetInt32(),
                listJson.RootElement.GetProperty("data").GetArrayLength());
        }
        Assert.Equal(9, withSubtasksJson.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task Label_filter_accepts_listed_exact_id_and_resolved_name()
    {
        var store = await CreateStoreAsync();
        var label = new LabelItem { Id = "label_filter", Name = "Filter label" };
        await store.UpsertLabelAsync(label);
        await store.UpsertTaskAsync(new TaskItem { Id = "task_labeled", Title = "Labeled", Labels = [label] });

        var byId = await RunAsync("task", "list", "--label", label.Id, "--format", "json");
        var byName = await RunAsync("task", "list", "--label", label.Name, "--format", "json");

        AssertSuccess(byId);
        AssertSuccess(byName);
        using var idJson = JsonDocument.Parse(byId.Stdout);
        using var nameJson = JsonDocument.Parse(byName.Stdout);
        Assert.Equal("task_labeled", Assert.Single(idJson.RootElement.GetProperty("data").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal("task_labeled", Assert.Single(nameJson.RootElement.GetProperty("data").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Task_list_and_show_json_expose_canonical_recurrence_metadata()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_recurring_cli",
            Title = "Recurring CLI task",
            PlannedOn = new DateOnly(2026, 8, 16),
            RecurrenceRule = "every day",
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_one_time_cli",
            Title = "One-time CLI task",
        });

        var list = await RunAsync("task", "list", "--view", "all", "--format", "json");
        var recurringShow = await RunAsync("task", "show", "task_recurring_cli", "--format", "json");
        var oneTimeShow = await RunAsync("task", "show", "task_one_time_cli", "--format", "json");

        AssertSuccess(list);
        AssertSuccess(recurringShow);
        AssertSuccess(oneTimeShow);
        using var listJson = JsonDocument.Parse(list.Stdout);
        var listedRecurring = listJson.RootElement.GetProperty("data").EnumerateArray()
            .Single(task => task.GetProperty("id").GetString() == "task_recurring_cli");
        Assert.True(listedRecurring.GetProperty("isRecurring").GetBoolean());
        Assert.Equal("every day", listedRecurring.GetProperty("recurrenceRule").GetString());
        Assert.Equal("2026-08-16", listedRecurring.GetProperty("plannedOn").GetString());

        using var recurringJson = JsonDocument.Parse(recurringShow.Stdout);
        Assert.True(recurringJson.RootElement.GetProperty("data").GetProperty("isRecurring").GetBoolean());
        Assert.Equal("every day", recurringJson.RootElement.GetProperty("data").GetProperty("recurrenceRule").GetString());

        using var oneTimeJson = JsonDocument.Parse(oneTimeShow.Stdout);
        Assert.False(oneTimeJson.RootElement.GetProperty("data").GetProperty("isRecurring").GetBoolean());
        Assert.Equal(JsonValueKind.Null, oneTimeJson.RootElement.GetProperty("data").GetProperty("recurrenceRule").ValueKind);
    }

    [Fact]
    public async Task Search_limit_caps_combined_task_and_project_results()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "task_limit_one", Title = "Limit one" });
        await store.UpsertTaskAsync(new TaskItem { Id = "task_limit_two", Title = "Limit two" });
        await store.UpsertTaskAsync(new TaskItem { Id = "task_limit_three", Title = "Limit three" });
        await store.UpsertProjectAsync(new ProjectItem { Id = "project_limit", Name = "Limit project" });

        var result = await RunAsync("search", "limit", "--limit", "3", "--format", "json");

        AssertSuccess(result);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(3, json.RootElement.GetProperty("data").GetArrayLength());

        foreach (var invalidLimit in new[] { "0", "501" })
        {
            var invalid = await RunAsync("search", "limit", "--limit", invalidLimit, "--format", "json");
            Assert.Equal(2, invalid.ExitCode);
            Assert.Empty(invalid.Stdout);
            using var error = JsonDocument.Parse(invalid.Stderr);
            Assert.Equal("invalid_arguments", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Read_commands_work_when_database_directory_is_not_writable()
    {
        if (OperatingSystem.IsWindows()) return;

        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "task_headless", Title = "Headless" });
        var originalMode = File.GetUnixFileMode(_directory);
        try
        {
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var status = await RunAsync("status", "--format", "json");
            var list = await RunAsync("task", "list", "--format", "json");
            var syncStatus = await RunInProcessAsync(new InMemoryCredentialStore(), new CliFakeProvider(),
                "sync", "status", "--provider", "todoist", "--direction", "push", "--scope", "pending", "--format", "json");

            AssertSuccess(status);
            AssertSuccess(list);
            AssertSuccess(syncStatus);
            Assert.False(File.Exists(Path.Combine(_directory, ".runtime.lock")));
        }
        finally
        {
            File.SetUnixFileMode(_directory, originalMode);
        }
    }

    [Fact]
    public async Task Schema_five_fixture_supports_every_read_only_command_without_migration()
    {
        if (OperatingSystem.IsWindows()) return;

        var store = await CreateStoreAsync();
        await store.UpsertLabelAsync(new LabelItem { Id = "label_schema_five", Name = "Schema Five" });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_schema_five",
            Title = "Schema five task",
            WorkflowStatus = TaskWorkflowStatus.Inbox,
            PlannedOn = DateOnly.FromDateTime(DateTime.Today),
            Labels = [new LabelItem { Id = "label_schema_five", Name = "Schema Five" }],
        });
        await store.SetTaskLabelsAsync("task_schema_five", [new LabelItem { Id = "label_schema_five", Name = "Schema Five" }]);
        await MakeCanonicalSchemaFiveAsync();
        var originalMode = File.GetUnixFileMode(_directory);
        try
        {
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            foreach (var arguments in new[]
            {
                new[] { "status", "--format", "json" },
                new[] { "task", "list", "--view", "open", "--format", "json" },
                new[] { "task", "list", "--view", "all", "--include-subtasks", "--format", "json" },
                new[] { "task", "list", "--view", "completed", "--format", "json" },
                new[] { "task", "list", "--view", "inbox", "--format", "json" },
                new[] { "task", "list", "--view", "next", "--format", "json" },
                new[] { "task", "list", "--view", "waiting", "--format", "json" },
                new[] { "task", "list", "--view", "someday", "--format", "json" },
                new[] { "task", "list", "--view", "today", "--format", "json" },
                new[] { "task", "list", "--view", "calendar", "--format", "json" },
                new[] { "task", "list", "--view", "overdue", "--format", "json" },
                new[] { "task", "show", "task_schema_five", "--format", "json" },
                new[] { "label", "list", "--format", "json" },
                new[] { "space", "list", "--format", "json" },
                new[] { "project", "list", "--format", "json" },
                new[] { "task", "list", "--label", "label_schema_five", "--format", "json" },
                new[] { "search", "Schema five", "--format", "json" },
            })
            {
                AssertSuccess(await RunAsync(arguments));
            }

            AssertSuccess(await RunInProcessAsync(new InMemoryCredentialStore(), new CliFakeProvider(),
                "sync", "status", "--provider", "todoist", "--direction", "push", "--scope", "pending", "--format", "json"));
            AssertSuccess(await RunInProcessAsync(new InMemoryCredentialStore(), new CliFakeProvider(),
                "sync", "run", "--provider", "todoist", "--direction", "push", "--scope", "pending", "--format", "json"));
        }
        finally
        {
            File.SetUnixFileMode(_directory, originalMode);
        }

        await AssertCanonicalSchemaFiveAsync();
    }

    [Fact]
    public async Task Help_documents_enums_and_json_errors_are_versioned()
    {
        _ = await CreateStoreAsync();

        var listHelp = await RunAsync("task", "list", "--help");
        var addHelp = await RunAsync("task", "add", "--help");
        AssertSuccess(listHelp);
        AssertSuccess(addHelp);
        Assert.Contains("open, inbox, next", listHelp.Stdout);
        Assert.Contains("next-actions", listHelp.Stdout);
        Assert.Contains("highest (1), high (2), normal (3), or low (4)", addHelp.Stdout);
        Assert.Contains("YYYY-MM-DD", addHelp.Stdout);

        var parser = await RunAsync("task", "--list", "--format", "json");
        Assert.Equal(2, parser.ExitCode);
        Assert.Empty(parser.Stdout);
        using var parserJson = JsonDocument.Parse(parser.Stderr);
        Assert.Equal(2, parserJson.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("invalid_arguments", parserJson.RootElement.GetProperty("error").GetProperty("code").GetString());

        var missing = await RunAsync("task", "show", "missing", "--format", "json");
        Assert.Equal(3, missing.ExitCode);
        Assert.Empty(missing.Stdout);
        using var missingJson = JsonDocument.Parse(missing.Stderr);
        Assert.Equal(2, missingJson.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("not_found_or_ambiguous", missingJson.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_commands_and_sync_safety_contract_use_documented_exit_codes()
    {
        _ = await CreateStoreAsync();

        foreach (var arguments in new[]
        {
            new[] { "bogus" },
            new[] { "bogus", "--help" },
        })
        {
            var unknown = await RunAsync(arguments);
            Assert.Equal(2, unknown.ExitCode);
            Assert.Empty(unknown.Stdout);
            Assert.Contains("Unrecognized command or argument 'bogus'.", unknown.Stderr);
        }

        var jsonUnknown = await RunAsync("--format", "json", "bogus", "--help");
        Assert.Equal(2, jsonUnknown.ExitCode);
        Assert.Empty(jsonUnknown.Stdout);
        using (var error = JsonDocument.Parse(jsonUnknown.Stderr))
        {
            Assert.Equal(2, error.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("invalid_arguments", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        AssertSuccess(await RunAsync("--help"));
        AssertSuccess(await RunAsync("task", "--help"));
        AssertSuccess(await RunAsync("task", "list", "--help"));
        var syncHelp = await RunAsync("sync", "run", "--help");
        AssertSuccess(syncHelp);
        Assert.Contains("Pull and bidirectional sync remain GUI-only", syncHelp.Stdout);

        foreach (var missingSelector in new[]
        {
            new[] { "sync", "status", "--direction", "push", "--scope", "pending" },
            new[] { "sync", "status", "--provider", "todoist", "--scope", "pending" },
            new[] { "sync", "status", "--provider", "todoist", "--direction", "push" },
        })
        {
            var invalid = await RunAsync(missingSelector);
            Assert.Equal(2, invalid.ExitCode);
        }
    }

    [Fact]
    public async Task Sync_cli_uses_injected_credentials_and_provider_for_status_confirmation_noop_and_push()
    {
        if (OperatingSystem.IsWindows()) return;

        var store = await CreateStoreAsync();
        var credentials = new InMemoryCredentialStore();
        var provider = new CliFakeProvider();

        var status = await RunInProcessAsync(credentials, provider,
            "sync", "status", "--provider", "todoist", "--direction", "push", "--scope", "pending", "--format", "json");
        AssertSuccess(status);
        using (var document = JsonDocument.Parse(status.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            Assert.Equal(0, data.GetProperty("pending").GetProperty("total").GetInt32());
            Assert.False(data.GetProperty("credentialAvailable").GetBoolean());
        }

        var noOp = await RunInProcessAsync(credentials, provider,
            "sync", "run", "--provider", "todoist", "--direction", "push", "--scope", "pending", "--format", "json");
        AssertSuccess(noOp);
        Assert.Empty(provider.Calls);

        await QueueCliPendingWritesAsync(store);

        var disconnected = await RunInProcessAsync(credentials, provider,
            "sync", "run", "--provider", "todoist", "--direction", "push", "--scope", "pending", "--yes", "--format", "json");
        Assert.Equal(6, disconnected.ExitCode);
        Assert.Empty(provider.Calls);

        await store.SetIntegrationConfiguredAsync(IntegrationIds.Todoist, true);
        await store.SetIntegrationActiveAsync(IntegrationIds.Todoist, true);
        await credentials.SaveAsync(ProviderCredentialKeys.TodoistToken, "disposable-test-token");

        var confirmation = await RunInProcessAsync(credentials, provider,
            "sync", "run", "--provider", "todoist", "--direction", "push", "--scope", "pending", "--format", "json");
        Assert.Equal(5, confirmation.ExitCode);
        Assert.Empty(provider.Calls);

        var pushed = await RunInProcessAsync(credentials, provider,
            "sync", "run", "--provider", "todoist", "--direction", "push", "--scope", "pending", "--yes", "--format", "json");
        AssertSuccess(pushed);
        Assert.Equal(["date", "completion"], provider.Calls);
        using (var document = JsonDocument.Parse(pushed.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("success").GetBoolean());
            Assert.Equal(2, data.GetProperty("applied").GetProperty("total").GetInt32());
            Assert.Equal(0, data.GetProperty("remaining").GetProperty("total").GetInt32());
        }

        var unsupported = await RunInProcessAsync(credentials, provider,
            "sync", "run", "--provider", "todoist", "--direction", "both", "--scope", "pending", "--yes", "--format", "json");
        Assert.Equal(2, unsupported.ExitCode);
    }

    [Fact]
    public async Task Sync_cli_tsv_schema_is_stable_with_disposable_credentials()
    {
        if (OperatingSystem.IsWindows()) return;

        var store = await CreateStoreAsync();
        var credentials = new InMemoryCredentialStore();
        var provider = new CliFakeProvider();
        var result = await RunInProcessAsync(credentials, provider,
            "sync", "status", "--provider", "todoist", "--direction", "push", "--scope", "pending", "--format", "tsv");
        AssertTsvSchema(result,
            "provider", "direction", "scope", "configured", "active", "credential_available",
            "pending_completions", "pending_reopens", "pending_date_updates", "total_pending", "last_full_sync_at");

        await store.SetIntegrationConfiguredAsync(IntegrationIds.Todoist, true);
        await store.SetIntegrationActiveAsync(IntegrationIds.Todoist, true);
        await credentials.SaveAsync(ProviderCredentialKeys.TodoistToken, "disposable-test-token");
        await QueueCliPendingWritesAsync(store);
        var pushed = await RunInProcessAsync(credentials, provider,
            "sync", "run", "--provider", "todoist", "--direction", "push", "--scope", "pending", "--yes", "--format", "tsv");
        AssertTsvSchema(pushed, "provider", "direction", "scope", "planned", "applied", "remaining", "success");
    }

    [Fact]
    public async Task Sync_cli_does_not_call_provider_while_Avalonia_provider_lease_is_held()
    {
        if (OperatingSystem.IsWindows()) return;

        var store = await CreateStoreAsync();
        await store.SetIntegrationConfiguredAsync(IntegrationIds.Todoist, true);
        await store.SetIntegrationActiveAsync(IntegrationIds.Todoist, true);
        await QueueCliPendingWritesAsync(store);
        var credentials = new InMemoryCredentialStore();
        await credentials.SaveAsync(ProviderCredentialKeys.TodoistToken, "disposable-test-token");
        var provider = new CliFakeProvider();
        var runtime = Openza.Tasks.Application.Runtime.OpenzaRuntimeContext.Create(
            Openza.Tasks.Application.Runtime.OpenzaChannel.Dev,
            _directory);

        CliResult blocked;
        using (Openza.Tasks.Application.Runtime.ChannelRuntimeLease.AcquireProviderSync(runtime, IntegrationIds.Todoist))
        {
            blocked = await RunInProcessAsync(credentials, provider,
                "sync", "run", "--provider", "todoist", "--direction", "push", "--scope", "pending", "--yes", "--format", "json");
        }

        Assert.Equal(1, blocked.ExitCode);
        Assert.Empty(provider.Calls);
        Assert.Contains("sync is already running", blocked.Stderr);
    }

    [Fact]
    public async Task Sync_cli_reports_the_documented_platform_restriction_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        _ = await CreateStoreAsync();
        var provider = new CliFakeProvider();
        foreach (var arguments in new[]
        {
            new[] { "sync", "status", "--provider", "todoist", "--direction", "push", "--scope", "pending", "--format", "json" },
            new[] { "sync", "run", "--provider", "todoist", "--direction", "push", "--scope", "pending", "--yes", "--format", "json" },
        })
        {
            var result = await RunInProcessAsync(new InMemoryCredentialStore(), provider, arguments);
            Assert.Equal(6, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Contains("currently supported only on Linux", result.Stderr);
        }
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task Sync_confirmation_on_schema_five_is_read_only_and_does_not_access_credentials_or_provider()
    {
        if (OperatingSystem.IsWindows()) return;

        var store = await CreateStoreAsync();
        await QueueCliPendingWritesAsync(store);
        await MakeCanonicalSchemaFiveAsync();
        var originalMode = File.GetUnixFileMode(_directory);
        var credentials = new ThrowingCredentialStore();
        var provider = new CliFakeProvider();
        try
        {
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var result = await RunInProcessAsync(credentials, provider,
                "sync", "run", "--provider", "todoist", "--direction", "push", "--scope", "pending", "--format", "json");

            Assert.Equal(5, result.ExitCode);
            Assert.Empty(provider.Calls);
            using var error = JsonDocument.Parse(result.Stderr);
            Assert.Equal("confirmation_required", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            File.SetUnixFileMode(_directory, originalMode);
        }

        await AssertCanonicalSchemaFiveAsync();
    }

    [Fact]
    public async Task Sync_contract_values_are_rejected_before_database_access()
    {
        var missingDirectory = Path.Combine(_directory, "does-not-exist");
        foreach (var arguments in new[]
        {
            new[] { "sync", "status", "--provider", "other", "--direction", "push", "--scope", "pending", "--format", "json" },
            new[] { "sync", "run", "--provider", "todoist", "--direction", "pull", "--scope", "pending", "--yes", "--format", "json" },
            new[] { "sync", "run", "--provider", "todoist", "--direction", "push", "--scope", "all", "--yes", "--format", "json" },
        })
        {
            var result = await RunWithDataDirectoryAsync(missingDirectory, arguments);
            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Stdout);
            using var error = JsonDocument.Parse(result.Stderr);
            Assert.Equal("invalid_arguments", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Read_command_does_not_open_database_while_replacement_holds_exclusive_lease()
    {
        _ = await CreateStoreAsync();
        var runtime = Openza.Tasks.Application.Runtime.OpenzaRuntimeContext.Create(
            Openza.Tasks.Application.Runtime.OpenzaChannel.Dev,
            _directory);

        CliResult blocked;
        using (Openza.Tasks.Application.Runtime.ChannelRuntimeLease.AcquireDatabaseReplacement(runtime))
        {
            blocked = await RunAsync("status", "--format", "json");
        }

        Assert.Equal(1, blocked.ExitCode);
        Assert.Empty(blocked.Stdout);
        using (var error = JsonDocument.Parse(blocked.Stderr))
        {
            Assert.Equal(2, error.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("unexpected_error", error.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains("database restore is in progress", error.RootElement.GetProperty("error").GetProperty("message").GetString());
        }
        AssertSuccess(await RunAsync("status", "--format", "json"));
    }

    [Fact]
    public async Task Every_tsv_leaf_command_emits_its_stable_versioned_schema()
    {
        _ = await CreateStoreAsync();

        AssertTsvSchema(await RunAsync("status", "--format", "tsv"),
            "channel", "data_directory", "database_path", "open", "completed");
        AssertTsvSchema(await RunAsync("search", "not-found", "--format", "tsv"),
            "kind", "id", "space_id", "title", "subtitle", "snippet");
        AssertTsvSchema(await RunAsync("task", "list", "--format", "tsv"),
            "id", "title", "status", "priority", "planned_on", "completed");

        var add = await RunAsync("task", "add", "TSV task", "--format", "tsv");
        AssertTaskDetailTsv(add);
        var id = SplitOutputLines(add.Stdout)[1].Split('\t')[1];
        AssertTaskDetailTsv(await RunAsync("task", "show", id, "--format", "tsv"));
        AssertTaskDetailTsv(await RunAsync("task", "update", id, "--title", "TSV updated", "--format", "tsv"));
        AssertTaskDetailTsv(await RunAsync("task", "complete", id, "--format", "tsv"));
        AssertTaskDetailTsv(await RunAsync("task", "reopen", id, "--format", "tsv"));
        AssertTsvSchema(await RunAsync("task", "delete", id, "--yes", "--format", "tsv"), "deleted", "id");

        AssertTsvSchema(await RunAsync("space", "list", "--format", "tsv"), "id", "name", "detail");
        AssertTsvSchema(await RunAsync("project", "list", "--format", "tsv"), "id", "name", "detail");
        AssertTsvSchema(await RunAsync("label", "list", "--format", "tsv"), "id", "name", "detail");
    }

    [Fact]
    public async Task Tsv_string_encoding_round_trips_control_characters_backslashes_and_label_commas()
    {
        _ = await CreateStoreAsync();
        const string title = "Path C:\\Tasks\tTabbed\rCarriage\nLine";
        const string notes = "Notes \\ root\tcolumn\r\nnext line";
        const string firstLabel = "ops,critical";
        const string secondLabel = "path\\label";

        var add = await RunAsync("task", "add", title, "--notes", notes,
            "--label", firstLabel, "--label", secondLabel, "--format", "tsv");
        AssertTaskDetailTsv(add);
        var added = ParseSingleTsvRecord(add);
        Assert.Equal("1", added["schema_version"]);
        Assert.Equal(title, added["title"]);
        Assert.Equal(notes, added["notes"]);
        AssertLabels(added["labels"], firstLabel, secondLabel);
        var id = added["id"];

        var list = ParseSingleTsvRecord(await RunAsync("task", "list", "--format", "tsv"));
        Assert.Equal(title, list["title"]);
        var search = ParseSingleTsvRecord(await RunAsync("search", "Tabbed", "--format", "tsv"));
        Assert.Equal(title, search["title"]);
        var shown = ParseSingleTsvRecord(await RunAsync("task", "show", id, "--format", "tsv"));
        Assert.Equal(notes, shown["notes"]);
        AssertLabels(shown["labels"], firstLabel, secondLabel);

        const string updatedTitle = "Updated\\value\tpart\rreturn\nline";
        var updated = ParseSingleTsvRecord(await RunAsync("task", "update", id, "--title", updatedTitle, "--format", "tsv"));
        Assert.Equal(updatedTitle, updated["title"]);
        AssertLabels(updated["labels"], firstLabel, secondLabel);

        var json = await RunAsync("task", "show", id, "--format", "json");
        AssertSuccess(json);
        using (var document = JsonDocument.Parse(json.Stdout))
        {
            var task = document.RootElement.GetProperty("data");
            Assert.Equal(updatedTitle, task.GetProperty("title").GetString());
            Assert.Equal(notes, task.GetProperty("notes").GetString());
            Assert.Equal([firstLabel, secondLabel], task.GetProperty("labels").EnumerateArray().Select(item => item.GetString()!).ToArray());
        }
        var text = await RunAsync("task", "show", id);
        AssertSuccess(text);
        Assert.Contains(updatedTitle, text.Stdout);
        Assert.Contains(notes, text.Stdout);
    }

    [Fact]
    public async Task Parser_and_domain_failures_use_stderr_and_documented_exit_codes()
    {
        var store = await CreateStoreAsync();
        var created = new TaskItem { Id = "task_conflict", Title = "Conflict", CreatedAt = DateTimeOffset.UtcNow };
        await store.UpsertTaskAsync(created);
        var original = (await store.GetTaskAsync(created.Id))!;
        await store.UpsertTaskAsync(original with { Title = "Changed" });

        var parser = await RunAsync("task", "--list");
        Assert.Equal(2, parser.ExitCode);
        Assert.Empty(parser.Stdout);
        Assert.Contains("Unrecognized", parser.Stderr);

        var confirmation = await RunAsync("task", "delete", created.Id);
        Assert.Equal(5, confirmation.ExitCode);
        Assert.Empty(confirmation.Stdout);
        Assert.Contains("--yes", confirmation.Stderr);

        var conflict = await RunAsync("task", "delete", created.Id, "--revision", original.Revision.ToString(), "--yes");
        Assert.Equal(4, conflict.ExitCode);
        Assert.Empty(conflict.Stdout);
        Assert.Contains("changed after it was loaded", conflict.Stderr);

        var missing = await RunAsync("task", "show", "missing");
        Assert.Equal(3, missing.ExitCode);
        Assert.Empty(missing.Stdout);

        var badDate = await RunAsync("task", "add", "Bad date", "--date", "16-08-2026");
        Assert.Equal(2, badDate.ExitCode);
        Assert.Empty(badDate.Stdout);
        Assert.Contains("YYYY-MM-DD", badDate.Stderr);

        var invalidDataPath = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(invalidDataPath, "file");
        var unexpected = await RunWithDataDirectoryAsync(invalidDataPath, "status");
        Assert.Equal(1, unexpected.ExitCode);
        Assert.Empty(unexpected.Stdout);
        Assert.NotEmpty(unexpected.Stderr);
    }

    [Fact]
    public async Task Label_ambiguity_requires_exact_id_but_unknown_name_creates_label()
    {
        var store = await CreateStoreAsync();
        await TaskApplicationServiceTests.InsertLabelsWithDuplicateNamesAsync(store.DatabasePath);

        var ambiguous = await RunAsync("task", "add", "Ambiguous", "--label", "focus");
        Assert.Equal(3, ambiguous.ExitCode);
        Assert.Contains("Use its exact id", ambiguous.Stderr);

        var exact = await RunAsync("task", "add", "Exact", "--label", "label_two", "--format", "json");
        AssertSuccess(exact);
        using (var document = JsonDocument.Parse(exact.Stdout))
        {
            Assert.Equal("FOCUS", document.RootElement.GetProperty("data").GetProperty("labels")[0].GetString());
        }

        AssertSuccess(await RunAsync("task", "add", "Create label", "--label", "Fresh label"));
        Assert.Contains(await store.GetLabelsAsync(), label => label.Name == "Fresh label" && label.IntegrationId == IntegrationIds.Local);
    }

    [Fact]
    public async Task Provider_linked_delete_has_stable_restriction_exit_code()
    {
        var store = await CreateStoreAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_cli_linked",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_cli_linked",
            ProviderTaskId = "remote_cli_linked",
            Title = "Linked",
        });
        var task = (await store.AdoptProviderSourceItemAsync("source_cli_linked"))!;

        var result = await RunAsync("task", "delete", task.Id, "--revision", task.Revision.ToString(), "--yes");

        Assert.Equal(6, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("linked to Todoist", result.Stderr);
        Assert.NotNull(await store.GetTaskAsync(task.Id));
    }

    private async Task<SqliteTaskStore> CreateStoreAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "openza-tasks.db"));
        await store.InitializeAsync();
        return store;
    }

    private Task<CliResult> RunAsync(params string[] arguments) => RunWithDataDirectoryAsync(_directory, arguments);

    private async Task<CliResult> RunInProcessAsync(
        ICredentialStore credentials,
        ISyncProvider provider,
        params string[] arguments)
    {
        var priorDataDirectory = Environment.GetEnvironmentVariable("OPENZA_TASKS_DEV_DATA_DIR");
        var priorOut = Console.Out;
        var priorError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("OPENZA_TASKS_DEV_DATA_DIR", _directory);
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var dependencies = new CliDependencies(_ => credentials, (_, _) => provider);
            var exitCode = await OpenzaCli.RunAsync(arguments, dependencies);
            return new CliResult(
                exitCode,
                stdout.ToString().TrimEnd('\r', '\n'),
                stderr.ToString().TrimEnd('\r', '\n'));
        }
        finally
        {
            Console.SetOut(priorOut);
            Console.SetError(priorError);
            Environment.SetEnvironmentVariable("OPENZA_TASKS_DEV_DATA_DIR", priorDataDirectory);
        }
    }

    private static async Task QueueCliPendingWritesAsync(SqliteTaskStore store)
    {
        await store.QueueTaskDateUpdateAsync(new PendingTaskDateUpdate
        {
            Id = "cli-date",
            TaskId = "cli-date-task",
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote-cli-date",
            PlannedOn = new DateOnly(2026, 8, 16),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        await store.QueueCompletionAsync(new PendingCompletion
        {
            Id = "cli-completion",
            TaskId = "cli-completion-task",
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote-cli-completion",
            Completed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private async Task MakeCanonicalSchemaFiveAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza-tasks.db")}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE tasks DROP COLUMN revision; PRAGMA user_version = 5;";
        await command.ExecuteNonQueryAsync();
    }


    private async Task AssertCanonicalSchemaFiveAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza-tasks.db")};Mode=ReadOnly");
        await connection.OpenAsync();
        var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        Assert.Equal(5L, Convert.ToInt64(await versionCommand.ExecuteScalarAsync()));
        var revisionCommand = connection.CreateCommand();
        revisionCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('tasks') WHERE name = 'revision'";
        Assert.Equal(0L, Convert.ToInt64(await revisionCommand.ExecuteScalarAsync()));
    }

    private async Task<CliResult> RunWithDataDirectoryAsync(string dataDirectory, params string[] arguments)
    {
        var cliPath = Path.Combine(AppContext.BaseDirectory, "openza.dll");
        Assert.True(File.Exists(cliPath), $"CLI test executable was not built at {cliPath}.");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(cliPath);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["OPENZA_TASKS_DEV_DATA_DIR"] = dataDirectory;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch CLI test process.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(
            process.ExitCode,
            stdout.TrimEnd('\r', '\n'),
            stderr.TrimEnd('\r', '\n'));
    }

    private static void AssertSuccess(CliResult result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
    }

    private static void AssertTaskDetailTsv(CliResult result) => AssertTsvSchema(result,
        "id", "title", "space_id", "project_id", "status", "completed", "priority", "planned_on", "deadline_on", "notes", "labels", "revision");

    private static void AssertTsvSchema(CliResult result, params string[] columns)
    {
        AssertSuccess(result);
        var lines = SplitOutputLines(result.Stdout);
        Assert.Equal(string.Join('\t', new[] { "schema_version" }.Concat(columns)), lines[0]);
        foreach (var line in lines.Skip(1))
        {
            var values = line.Split('\t');
            Assert.Equal(columns.Length + 1, values.Length);
            Assert.Equal("1", values[0]);
        }
    }

    private sealed class CliFakeProvider : ITaskDateUpdateProvider
    {
        public string IntegrationId => IntegrationIds.Todoist;
        public List<string> Calls { get; } = [];

        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Push-only sync must not fetch a snapshot.");

        public Task UpdateTaskDateAsync(PendingTaskDateUpdate update, CancellationToken cancellationToken = default)
        {
            Calls.Add("date");
            return Task.CompletedTask;
        }

        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default)
        {
            Calls.Add(completion.Completed ? "completion" : "reopen");
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingCredentialStore : ICredentialStore
    {
        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Credential store must not be accessed before confirmation.");

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Credential store must not be accessed before confirmation.");

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Credential store must not be accessed before confirmation.");
    }

    private static Dictionary<string, string> ParseSingleTsvRecord(CliResult result)
    {
        AssertSuccess(result);
        var lines = SplitOutputLines(result.Stdout);
        Assert.Equal(2, lines.Length);
        var headers = lines[0].Split('\t');
        var values = lines[1].Split('\t').Select(UnescapeTsv).ToArray();
        Assert.Equal(headers.Length, values.Length);
        return headers.Zip(values).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
    }

    private static string[] SplitOutputLines(string output) =>
        output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string UnescapeTsv(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\')
            {
                result.Append(value[index]);
                continue;
            }
            Assert.True(++index < value.Length, "TSV escape cannot end with a backslash.");
            result.Append(value[index] switch
            {
                '\\' => '\\',
                't' => '\t',
                'r' => '\r',
                'n' => '\n',
                _ => throw new Xunit.Sdk.XunitException($"Unknown TSV escape: \\{value[index]}"),
            });
        }
        return result.ToString();
    }

    private static void AssertLabels(string json, params string[] expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, document.RootElement.EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    public void Dispose() => TestDirectory.Delete(_directory);

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
