using Openza.Tasks.Core.Services;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Data;

namespace Openza.Tasks.Tests;

public sealed class MarkdownTaskParserTests
{
    [Theory]
    [InlineData("Call @alex")]
    [InlineData("Review (Priority: Normal, Date: 2027-06-15, Deadline: 2027-06-20) @work")]
    [InlineData("Keep \\*literal\\* (Priority: High)")]
    public void Parse_plain_checklist_preserves_title_text(string title)
    {
        var task = Assert.Single(MarkdownTaskParser.Parse("- [ ] " + title));

        Assert.Equal(title, task.Title);
        Assert.Empty(task.Labels);
        Assert.Equal(2, task.Priority);
        Assert.Null(task.PlannedOn);
        Assert.Null(task.DeadlineOn);
        Assert.Equal(title, MarkdownTaskParser.ParseLine("- [ ] " + title)!.Title);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Export_import_preserves_all_exported_task_fields(int priority)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            var project = new ProjectItem { Id = "project", Name = "A *project*" };
            var label = new LabelItem { Id = "label", Name = "a_label with spaces", Color = "#123456" };
            foreach (var state in Enum.GetValues<TaskCompletionState>())
            foreach (var status in Enum.GetValues<TaskWorkflowStatus>())
            {
                var original = new TaskItem
                {
                    Title = "A *title* \\ [with] @text (Priority: Normal)", Priority = priority,
                    ProjectId = project.Id, PlannedOn = new(2027, 6, 15), DeadlineOn = new(2027, 6, 20),
                    Notes = "First _line_\nSecond \\ line\n- [ ] not another task", Labels = [label],
                    WorkflowStatus = status, CompletionState = state,
                };
                var parsed = Assert.Single(MarkdownTaskParser.Parse(MarkdownExporter.Export([original], [project], [label])));
                Assert.Equal(original.Title, parsed.Title);
                await MarkdownTaskImporter.ImportAsync(store, [parsed], SpaceIds.Default);
                var imported = Assert.Single(await store.GetTasksAsync(TaskQuery.ForMarkdownExport(SpaceIds.Default)));
                Assert.Equal(original.Title, imported.Title);
                Assert.Equal(priority, imported.Priority);
                Assert.Equal(original.PlannedOn, imported.PlannedOn);
                Assert.Equal(original.DeadlineOn, imported.DeadlineOn);
                Assert.Equal(original.Notes, imported.Notes);
                Assert.Equal(status, imported.WorkflowStatus);
                Assert.Equal(state, imported.CompletionState);
                Assert.Equal(project.Name, (await store.GetProjectsAsync()).Single().Name);
                Assert.Equal(label.Name, Assert.Single(imported.Labels).Name);
                Assert.Equal(label.Color, imported.Labels[0].Color);
                await store.DeleteTaskAsync(imported.Id);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Parse_legacy_export_metadata_and_escaped_notes()
    {
        var task = Assert.Single(MarkdownTaskParser.Parse("""
            # Openza Tasks Export
            ### Example
            - [x] A \*title\* (Priority: Normal, Date: 2027-06-15, Deadline: 2027-06-20) @some_label
              - _Some \_notes\__
            """));
        Assert.Equal("A *title*", task.Title);
        Assert.Equal(3, task.Priority);
        Assert.Equal(new DateOnly(2027, 6, 15), task.PlannedOn);
        Assert.Equal(new DateOnly(2027, 6, 20), task.DeadlineOn);
        Assert.Equal("Some _notes_", task.Notes);
        Assert.Equal("some label", Assert.Single(task.Labels).Name);
        Assert.Equal("Example", task.ProjectName);
        Assert.True(task.IsCompleted);
        Assert.Equal(4, Assert.Single(MarkdownTaskParser.Parse("# Openza Tasks Export\n- [ ] Low")).Priority);
    }

    [Fact]
    public void Parse_empty_and_malformed_metadata_are_safe()
    {
        Assert.Empty(MarkdownTaskParser.Parse(""));
        var task = Assert.Single(MarkdownTaskParser.Parse("- [ ] Plain\n  <!-- openza-task:{invalid} -->"));
        Assert.Equal("Plain", task.Title);
        Assert.Equal(2, task.Priority);
        Assert.Null(task.Notes);
        Assert.Empty(task.Labels);
        Assert.Null(MarkdownTaskParser.ParseLine("not a task"));
    }

    [Fact]
    public void Parse_extracts_gfm_checkboxes()
    {
        var tasks = MarkdownTaskParser.Parse("""
            # Tasks
            - [ ] Write docs
            - [x] Ship release
            * [X] Review PR
            + [ ] Follow up
            """);

        Assert.Equal(4, tasks.Count);
        Assert.Equal("Write docs", tasks[0].Title);
        Assert.False(tasks[0].IsCompleted);
        Assert.True(tasks[1].IsCompleted);
        Assert.True(tasks[2].IsCompleted);
    }

    [Fact]
    public void Parse_skips_extremely_long_lines()
    {
        var tasks = MarkdownTaskParser.Parse("- [ ] " + new string('a', 10_001));

        Assert.Empty(tasks);
    }
}
