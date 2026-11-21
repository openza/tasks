using Openza.Tasks.Core.Services;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Data;
using System.Text.Json;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class MarkdownTaskParserTests
{
    [Fact]
    public async Task Import_legacy_cloud_keeps_cancelled_task_open_to_match_create_intent()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            var cloud = new PersonalCloudConnectionProfile
            {
                ServiceOrigin = new("https://example.test"), InstanceId = "instance_cancelled_markdown", DeviceId = "device_cancelled_markdown",
                ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow,
            };
            Assert.False(await store.SupportsPersonalCloudWorkspaceAsync(cloud.InstanceId));
            var original = new TaskItem { Title = "Cancelled", CompletionState = TaskCompletionState.Cancelled, Priority = 2 };
            var markdown = MarkdownExporter.Export([original], [], []);

            await MarkdownTaskImporter.ImportAsync(store, MarkdownTaskParser.Parse(markdown), SpaceIds.Default, cloud);

            var imported = Assert.Single(await store.GetTasksAsync(TaskQuery.ForMarkdownExport(SpaceIds.Default)));
            Assert.Equal(TaskCompletionState.Open, imported.CompletionState);
            Assert.Null(imported.CompletedAt);
            var entry = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(cloud.InstanceId, cloud.DeviceId));
            Assert.Equal("task.create", entry.Operation);
            Assert.Equal(imported.Id, entry.EntityId);
            using var json = JsonDocument.Parse(entry.CanonicalJson);
            Assert.Equal("open", json.RootElement.GetProperty("payload").GetProperty("task").GetProperty("completionState").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(true, ProjectLifecycleStates.Active)]
    [InlineData(false, ProjectLifecycleStates.Archived)]
    public async Task Import_creates_a_new_project_instead_of_reusing_archived_match(bool archived, string status)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            var existing = new ProjectItem { Id = "archived", Name = "Example", IsArchived = archived, Status = status };
            await store.UpsertProjectAsync(existing);
            var cloud = new PersonalCloudConnectionProfile
            {
                ServiceOrigin = new("https://example.test"), InstanceId = "instance_archived_markdown", DeviceId = "device_archived_markdown",
                ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow,
            };
            await store.SavePersonalCloudWorkspaceCapabilitiesAsync(cloud.InstanceId, true, true);

            await MarkdownTaskImporter.ImportAsync(store, [new ParsedTask("Imported", false) { ProjectName = "Example" }], SpaceIds.Default, cloud);

            var imported = Assert.Single(await store.GetTasksAsync(TaskQuery.ForMarkdownExport(SpaceIds.Default)));
            var projects = await store.GetProjectsAsync(includeArchived: true);
            Assert.Equal(2, projects.Count);
            Assert.NotEqual(existing.Id, imported.ProjectId);
            var project = Assert.Single(projects, item => item.Id == imported.ProjectId);
            Assert.Equal(existing.Name, project.Name);
            Assert.False(project.IsArchived);
            Assert.Equal(ProjectLifecycleStates.Active, project.EffectiveStatus);
            Assert.Equal(ProjectLifecycleStates.Archived, projects.Single(item => item.Id == existing.Id).EffectiveStatus);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(true, TaskWorkflowStatus.Inbox)]
    [InlineData(false, TaskWorkflowStatus.Next)]
    [InlineData(false, TaskWorkflowStatus.Waiting)]
    public async Task Import_legacy_cloud_records_rich_task_intents_immediately(bool completed, TaskWorkflowStatus status)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            var cloud = new PersonalCloudConnectionProfile
            {
                ServiceOrigin = new("https://example.test"), InstanceId = "instance_legacy_markdown", DeviceId = "device_legacy_markdown",
                ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow,
            };
            Assert.False(await store.SupportsPersonalCloudWorkspaceAsync(cloud.InstanceId));
            var original = new TaskItem
            {
                Title = "Legacy cloud import", WorkflowStatus = status,
                CompletionState = completed ? TaskCompletionState.Completed : TaskCompletionState.Open,
                Notes = "Keep legacy notes", Priority = 3, PlannedOn = new(2027, 6, 15), DeadlineOn = new(2027, 6, 20),
                ProjectId = "project", Labels = [new LabelItem { Name = "Legacy label" }],
            };
            var markdown = MarkdownExporter.Export([original], [new ProjectItem { Id = "project", Name = "Legacy project" }], original.Labels);

            await MarkdownTaskImporter.ImportAsync(store, MarkdownTaskParser.Parse(markdown), SpaceIds.Default, cloud);

            var imported = Assert.Single(await store.GetTasksAsync(TaskQuery.ForMarkdownExport(SpaceIds.Default)));
            Assert.Equal(original.Title, imported.Title);
            Assert.Equal(original.CompletionState, imported.CompletionState);
            Assert.Equal(status, imported.WorkflowStatus);
            Assert.Equal(original.Notes, imported.Notes);
            Assert.Equal(original.Priority, imported.Priority);
            Assert.Equal(original.PlannedOn, imported.PlannedOn);
            Assert.Equal(original.DeadlineOn, imported.DeadlineOn);
            Assert.Equal(Assert.Single(await store.GetProjectsAsync()).Id, imported.ProjectId);
            Assert.Equal("Legacy label", Assert.Single(imported.Labels).Name);
            var entries = await store.GetPendingPersonalCloudOutboxAsync(cloud.InstanceId, cloud.DeviceId);
            Assert.All(entries, entry => Assert.Equal(imported.Id, entry.EntityId));
            var creation = Assert.Single(entries, entry => entry.Operation == "task.create");
            using var json = JsonDocument.Parse(creation.CanonicalJson);
            var task = json.RootElement.GetProperty("payload").GetProperty("task");
            Assert.Equal("open", task.GetProperty("completionState").GetString());
            Assert.Equal("inbox", task.GetProperty("workflowStatus").GetString());
            Assert.Single(entries, entry => entry.Operation == "task.setProject");
            Assert.Single(entries, entry => entry.Operation == "task.addLabel");
            foreach (var field in new[] { "notes", "priority", "plannedOn", "deadlineOn" }.Concat(status == TaskWorkflowStatus.Inbox ? [] : new[] { "workflowStatus" }))
            {
                Assert.Single(entries, entry =>
                {
                    using var mutation = JsonDocument.Parse(entry.CanonicalJson);
                    return entry.Operation == "task.updateOpenzaFields" && mutation.RootElement.GetProperty("payload").TryGetProperty(field, out _);
                });
            }
            Assert.Equal(completed ? 1 : 0, entries.Count(entry => entry.Operation == "task.complete"));
            Assert.Equal(0, await store.StageUnpublishedTasksForPersonalCloudAsync(cloud.InstanceId, cloud.DeviceId));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Export_import_preserves_blank_lines_between_note_paragraphs()
    {
        var original = new TaskItem { Title = "Paragraphs", Notes = "First paragraph\n\nSecond paragraph" };
        var markdown = MarkdownExporter.Export([original], [], []);

        var imported = Assert.Single(MarkdownTaskParser.Parse(markdown));

        Assert.Equal(original.Title, imported.Title);
        Assert.Equal(original.Notes, imported.Notes);
        var withoutComment = string.Join('\n', markdown.Split('\n').Where(line => !line.StartsWith("  <!-- openza-task:", StringComparison.Ordinal)));
        Assert.Equal(original.Notes, Assert.Single(MarkdownTaskParser.Parse(withoutComment)).Notes);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Parse_preserves_trimmed_blank_note_lines_and_metadata(string newline)
    {
        var original = new TaskItem { Title = "Paragraphs", Notes = "First\n\nSecond", WorkflowStatus = TaskWorkflowStatus.Next };
        var markdown = string.Join(newline, MarkdownExporter.Export([original], [], []).Split('\n').Select(line => line.TrimEnd()));

        var imported = Assert.Single(MarkdownTaskParser.Parse(markdown));

        Assert.Equal(original.Notes, imported.Notes);
        Assert.Equal(original.WorkflowStatus, imported.WorkflowStatus);
        var withoutComment = string.Join(newline, markdown.Split('\n').Where(line => !line.StartsWith("  <!-- openza-task:", StringComparison.Ordinal)));
        Assert.Equal(original.Notes, Assert.Single(MarkdownTaskParser.Parse(withoutComment)).Notes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("- [ ] Next task")]
    [InlineData("Unrelated text")]
    public void Parse_discards_pending_blank_lines_when_notes_do_not_continue(string nextLine)
    {
        var markdown = "# Openza Tasks Export\r\n- [ ] Paragraphs\r\n  - _First\r\n\r\n \t\r\n" + nextLine;

        var tasks = MarkdownTaskParser.Parse(markdown);

        Assert.Equal("First", tasks[0].Notes);
        Assert.Equal(nextLine.StartsWith("- [ ]", StringComparison.Ordinal) ? 2 : 1, tasks.Count);
    }

    [Fact]
    public void Parse_export_defaults_to_low_without_consumed_priority_metadata()
    {
        var task = Assert.Single(MarkdownTaskParser.Parse("# Openza Tasks Export\n- [ ] Review Priority: handling"));

        Assert.Equal("Review Priority: handling", task.Title);
        Assert.Equal(4, task.Priority);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_rejected_checkbox_cannot_attach_notes_or_metadata_to_previous_task(bool comment)
    {
        var exported = MarkdownExporter.Export([new TaskItem { Title = "Rejected task", Notes = "Wrong notes" }], [], []);
        var metadata = exported.Split('\n').Single(line => line.StartsWith("  <!-- openza-task:", StringComparison.Ordinal));
        var markdown = "# Openza Tasks Export\n- [ ] Keep me\n- [ ] " + new string('a', 10_001)
            + "\n" + (comment ? metadata : "  - _Wrong notes_");

        var task = Assert.Single(MarkdownTaskParser.Parse(markdown));

        Assert.Equal("Keep me", task.Title);
        Assert.Null(task.Notes);
    }

    [Fact]
    public async Task Import_records_cloud_project_task_and_label_intent()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            var cloud = new PersonalCloudConnectionProfile
            {
                ServiceOrigin = new("https://example.test"), InstanceId = "instance_markdown", DeviceId = "device_markdown",
                ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow,
            };
            await store.SavePersonalCloudWorkspaceCapabilitiesAsync(cloud.InstanceId, true, true);
            await store.ApplyPersonalCloudChangePageAsync(cloud.InstanceId, cloud.DeviceId,
                PersonalCloudWorkspaceTests.Page(1, [PersonalCloudWorkspaceTests.Space()]));
            var task = new ParsedTask("Cloud import", false)
            {
                ProjectName = "Imported project", Labels = [new LabelItem { Name = "Imported label", Color = "#123456" }],
            };

            await MarkdownTaskImporter.ImportAsync(store, [task], "space_test", cloud);

            var imported = Assert.Single(await store.GetTasksAsync(TaskQuery.ForMarkdownExport("space_test")));
            var entries = await store.GetPendingPersonalCloudOutboxAsync(cloud.InstanceId, cloud.DeviceId);
            Assert.Equal(imported.ProjectId, Assert.Single(entries, entry => entry.Operation == "project.put").EntityId);
            var creation = Assert.Single(entries, entry => entry.Operation == "task.createRich");
            Assert.Equal(imported.Id, creation.EntityId);
            using var json = JsonDocument.Parse(creation.CanonicalJson);
            var label = Assert.Single(json.RootElement.GetProperty("payload").GetProperty("task").GetProperty("labels").EnumerateArray());
            Assert.Equal(Assert.Single(imported.Labels).Id, label.GetProperty("id").GetString());
            Assert.Equal("Imported label", label.GetProperty("name").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_edited_checkbox_overrides_comment_completion(bool originallyCompleted)
    {
        var original = new TaskItem
        {
            Title = "Checkbox edit", CompletionState = originallyCompleted ? TaskCompletionState.Completed : TaskCompletionState.Open,
            WorkflowStatus = TaskWorkflowStatus.Waiting, Priority = 3,
            PlannedOn = new(2027, 6, 15), DeadlineOn = new(2027, 6, 20), Notes = "Keep these notes",
            ProjectId = "project", Labels = [new LabelItem { Name = "some label", Color = "#123456" }],
        };
        var markdown = MarkdownExporter.Export([original], [new ProjectItem { Id = "project", Name = "Example" }], original.Labels);
        var expected = Assert.Single(MarkdownTaskParser.Parse(markdown)) with
        {
            IsCompleted = !originallyCompleted,
            CompletionState = originallyCompleted ? TaskCompletionState.Open : TaskCompletionState.Completed,
        };
        var edited = markdown.Replace(originallyCompleted ? "- [x] " : "- [ ] ", originallyCompleted ? "- [ ] " : "- [x] ", StringComparison.Ordinal);
        var actual = Assert.Single(MarkdownTaskParser.Parse(edited));

        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }

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
                var importedProject = Assert.Single(await store.GetProjectsAsync());
                Assert.Equal(project.Name, importedProject.Name);
                Assert.Equal(importedProject.Id, imported.ProjectId);
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
