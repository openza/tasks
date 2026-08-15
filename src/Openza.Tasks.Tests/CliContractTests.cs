using System.Diagnostics;
using System.Text.Json;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed class CliContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-cli-tests", Guid.NewGuid().ToString("N"));

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
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
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
            var task = document.RootElement.GetProperty("data")[0];
            id = task.GetProperty("id").GetString()!;
            revision = task.GetProperty("revision").GetInt64();
        }

        var list = await RunAsync("task", "list", "--project", "CLI Project", "--label", "CLI Label", "--format", "tsv");
        AssertSuccess(list);
        Assert.Contains(id, list.Stdout);
        var search = await RunAsync("search", "contract");
        AssertSuccess(search);
        Assert.Contains(id, search.Stdout);

        var show = await RunAsync("task", "show", id, "--format", "tsv");
        AssertSuccess(show);
        var showLines = show.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
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
            Assert.Equal(id, document.RootElement.GetProperty("data")[0].GetProperty("id").GetString());
            Assert.True(document.RootElement.GetProperty("data")[0].TryGetProperty("revision", out _));
        }

        var update = await RunAsync("task", "update", id, "--revision", revision.ToString(), "--clear-project", "--clear-date",
            "--clear-deadline", "--clear-notes", "--clear-labels", "--status", "next", "--format", "json");
        AssertSuccess(update);
        using (var document = JsonDocument.Parse(update.Stdout))
        {
            var task = document.RootElement.GetProperty("data")[0];
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
            revision = document.RootElement.GetProperty("data")[0].GetProperty("revision").GetInt64();
        }
        AssertSuccess(await RunAsync("task", "reopen", id, "--revision", revision.ToString()));
        var current = (await store.GetTaskAsync(id))!;
        AssertSuccess(await RunAsync("task", "delete", id, "--revision", current.Revision.ToString(), "--yes"));
        Assert.Null(await store.GetTaskAsync(id));
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
        var id = add.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].Split('\t')[1];
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
            var task = document.RootElement.GetProperty("data")[0];
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
            Assert.Equal("FOCUS", document.RootElement.GetProperty("data")[0].GetProperty("labels")[0].GetString());
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
        var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(string.Join('\t', new[] { "schema_version" }.Concat(columns)), lines[0]);
        foreach (var line in lines.Skip(1))
        {
            var values = line.Split('\t');
            Assert.Equal(columns.Length + 1, values.Length);
            Assert.Equal("1", values[0]);
        }
    }

    private static Dictionary<string, string> ParseSingleTsvRecord(CliResult result)
    {
        AssertSuccess(result);
        var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        var headers = lines[0].Split('\t');
        var values = lines[1].Split('\t').Select(UnescapeTsv).ToArray();
        Assert.Equal(headers.Length, values.Length);
        return headers.Zip(values).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
    }

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
