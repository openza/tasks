using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed class SqliteTaskStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-tasks-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Initialize_creates_clean_workspace_integrations_and_connections()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var projects = await store.GetProjectsAsync();
        var integrations = await store.GetIntegrationsAsync();
        var connections = await store.GetProviderConnectionsAsync();

        Assert.Empty(projects);
        Assert.Contains(integrations, i => i.Id == IntegrationIds.Todoist);
        Assert.Contains(integrations, i => i.Id == IntegrationIds.MicrosoftToDo);
        Assert.Contains(integrations, i => i.Id == IntegrationIds.GitHub);
        Assert.DoesNotContain(integrations, i => i.Id == IntegrationIds.Obsidian);
        Assert.Contains(connections, c => c.Id == "local_default" && c.Status == "connected");
        Assert.Contains(connections, c => c.Id == "todoist_default" && c.Status == "disconnected");
        Assert.Contains(connections, c => c.Id == "mstodo_default" && c.Status == "disconnected");
        Assert.Contains(connections, c => c.Id == "github_default" && c.Status == "disconnected");
    }

    [Fact]
    public async Task Initialize_creates_v3_schema_for_planning_and_sync_routes()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();

        Assert.Equal(4L, await ExecuteScalarAsync(connection, "PRAGMA user_version"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'provider_connections'"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'provider_source_items'"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sync_routes'"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sync_field_state'"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sync_operations'"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'task_external_links'"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('tasks') WHERE name = 'source_description'"));
        Assert.Equal("'none'", await ExecuteScalarTextAsync(connection, "SELECT dflt_value FROM pragma_table_info('tasks') WHERE name = 'workflow_status'"));
        Assert.Equal("'active'", await ExecuteScalarTextAsync(connection, "SELECT dflt_value FROM pragma_table_info('projects') WHERE name = 'status'"));
    }

    [Fact]
    public async Task Task_external_links_roundtrip_without_changing_task_state()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_linked",
            Title = "Publish issue",
            Status = TaskItemStatus.Next,
        });

        await store.UpsertTaskExternalLinkAsync(new TaskExternalLinkInfo
        {
            Id = "link_1",
            TaskId = "task_linked",
            IntegrationId = IntegrationIds.GitHub,
            ConnectionId = "github_default",
            ExternalId = "openza/tasks#123",
            Kind = TaskExternalLinkKinds.Issue,
            DisplayName = "openza/tasks#123",
            Url = "https://github.com/openza/tasks/issues/123",
            MetadataJson = """{"number":123}""",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var links = await store.GetTaskExternalLinksAsync("task_linked");
        var task = await store.GetTaskAsync("task_linked");

        var link = Assert.Single(links);
        Assert.Equal(IntegrationIds.GitHub, link.IntegrationId);
        Assert.Equal("openza/tasks#123", link.DisplayName);
        Assert.Equal(TaskItemStatus.Next, task?.Status);
        Assert.True(task?.IsOpen);
    }

    [Fact]
    public async Task DeleteTaskExternalLink_removes_stored_link()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_link_delete",
            Title = "Remove issue link",
        });
        await store.UpsertTaskExternalLinkAsync(new TaskExternalLinkInfo
        {
            Id = "link_delete",
            TaskId = "task_link_delete",
            IntegrationId = IntegrationIds.GitHub,
            ConnectionId = "github_default",
            ExternalId = "openza/tasks#124",
            Kind = TaskExternalLinkKinds.Issue,
            DisplayName = "openza/tasks#124",
            Url = "https://github.com/openza/tasks/issues/124",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await store.DeleteTaskExternalLinkAsync("link_delete");

        Assert.Empty(await store.GetTaskExternalLinksAsync("task_link_delete"));
    }

    [Fact]
    public async Task MoveTaskToSpace_moves_task_and_preserves_local_metadata()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_target", Name = "Work" });
        await store.UpsertProjectAsync(new ProjectItem { Id = "project_default", Name = "Default project" });
        await store.UpsertLabelAsync(new LabelItem { Id = "label_focus", Name = "Focus" });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_move",
            Title = "Move me",
            ProjectId = "project_default",
            Status = TaskItemStatus.Completed,
            SourceIntegrationId = IntegrationIds.Todoist,
            SourceExternalId = "todoist_1",
            SourceUrl = "https://app.todoist.com/app/task/123",
            PlannedOn = new DateOnly(2026, 5, 22),
            DeadlineOn = new DateOnly(2026, 5, 23),
            Notes = "Keep notes",
            LocalMetadataJson = """{"ack":"yes"}""",
            Labels = [new LabelItem { Id = "label_focus", Name = "Focus" }],
        });
        await store.UpsertTaskExternalLinkAsync(new TaskExternalLinkInfo
        {
            Id = "link_issue",
            TaskId = "task_move",
            IntegrationId = IntegrationIds.GitHub,
            ConnectionId = "github_default",
            ExternalId = "openza/tasks#1",
            Kind = TaskExternalLinkKinds.Issue,
            DisplayName = "openza/tasks#1",
            Url = "https://github.com/openza/tasks/issues/1",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_todoist_1",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "todoist_1",
            ProviderTaskId = "todoist_1",
            Title = "Move me",
            SourceUrl = "https://app.todoist.com/app/task/123",
            SuggestedSpaceId = SpaceIds.Default,
            CompletionState = TaskCompletionState.Completed,
            AdoptionState = ProviderSourceAdoptionStates.Adopted,
            AdoptedTaskId = "task_move",
        });

        await store.MoveTaskToSpaceAsync("task_move", "space_target");

        var moved = Assert.Single(await store.GetTasksAsync(new TaskQuery { SpaceId = "space_target", Kind = TaskListKind.All }));
        Assert.Equal("task_move", moved.Id);
        Assert.Null(moved.ProjectId);
        Assert.Equal(TaskItemStatus.Completed, moved.Status);
        Assert.Equal(IntegrationIds.Todoist, moved.SourceIntegrationId);
        Assert.Equal("todoist_1", moved.SourceExternalId);
        Assert.Equal("https://app.todoist.com/app/task/123", moved.SourceUrl);
        Assert.Equal(new DateOnly(2026, 5, 22), moved.PlannedOn);
        Assert.Equal(new DateOnly(2026, 5, 23), moved.DeadlineOn);
        Assert.Equal("Keep notes", moved.Notes);
        Assert.Equal("""{"ack":"yes"}""", moved.LocalMetadataJson);
        Assert.Single(moved.Labels, label => label.Id == "label_focus");
        Assert.Single(await store.GetTaskExternalLinksAsync("task_move"));
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        Assert.Equal("task_move", source.AdoptedTaskId);
        Assert.Equal("space_target", source.SuggestedSpaceId);
        Assert.Empty(await store.GetTasksAsync(new TaskQuery { SpaceId = SpaceIds.Default, Kind = TaskListKind.All }));
    }

    [Fact]
    public async Task MoveTaskToSpace_moves_parent_with_subtask_tree()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_target", Name = "Work" });
        await store.UpsertProjectAsync(new ProjectItem { Id = "project_default", Name = "Default project" });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_parent",
            Title = "Parent",
            ProjectId = "project_default",
            Status = TaskItemStatus.Next,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_child",
            Title = "Child",
            ParentId = "task_parent",
            ProjectId = "project_default",
            Status = TaskItemStatus.Waiting,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_grandchild",
            Title = "Grandchild",
            ParentId = "task_child",
            ProjectId = "project_default",
            Status = TaskItemStatus.Someday,
        });
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_grandchild",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "todoist_grandchild",
            ProviderTaskId = "todoist_grandchild",
            Title = "Grandchild",
            SuggestedSpaceId = SpaceIds.Default,
            AdoptionState = ProviderSourceAdoptionStates.Adopted,
            AdoptedTaskId = "task_grandchild",
        });

        await store.MoveTaskToSpaceAsync("task_parent", "space_target");

        var moved = await store.GetTasksAsync(new TaskQuery { SpaceId = "space_target", Kind = TaskListKind.All, IncludeSubtasks = true });
        var parent = Assert.Single(moved, task => task.Id == "task_parent");
        var child = Assert.Single(moved, task => task.Id == "task_child");
        var grandchild = Assert.Single(moved, task => task.Id == "task_grandchild");
        Assert.Null(parent.ProjectId);
        Assert.Null(child.ProjectId);
        Assert.Null(grandchild.ProjectId);
        Assert.Null(parent.ParentId);
        Assert.Equal("task_parent", child.ParentId);
        Assert.Equal("task_child", grandchild.ParentId);
        Assert.Equal(TaskItemStatus.Next, parent.Status);
        Assert.Equal(TaskItemStatus.Waiting, child.Status);
        Assert.Equal(TaskItemStatus.Someday, grandchild.Status);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        Assert.Equal("space_target", source.SuggestedSpaceId);
    }

    [Fact]
    public async Task MoveTaskToSpace_clears_parent_when_moving_subtask_alone()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_target", Name = "Work" });
        await store.UpsertTaskAsync(new TaskItem { Id = "task_parent", Title = "Parent" });
        await store.UpsertTaskAsync(new TaskItem { Id = "task_child", Title = "Child", ParentId = "task_parent" });

        await store.MoveTaskToSpaceAsync("task_child", "space_target");

        var parent = await store.GetTaskAsync("task_parent");
        var child = await store.GetTaskAsync("task_child");
        Assert.NotNull(parent);
        Assert.NotNull(child);
        Assert.Equal(SpaceIds.Default, parent.SpaceId);
        Assert.Equal("space_target", child.SpaceId);
        Assert.Null(child.ParentId);
    }

    [Fact]
    public async Task MoveTaskToSpace_rejects_missing_or_archived_space()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "task_move", Title = "Move me" });
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_archived", Name = "Old", IsArchived = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveTaskToSpaceAsync("task_move", "space_missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveTaskToSpaceAsync("task_move", "space_archived"));

        var task = await store.GetTaskAsync("task_move");
        Assert.NotNull(task);
        Assert.Equal(SpaceIds.Default, task.SpaceId);
    }

    [Fact]
    public async Task UpsertTask_roundtrips_labels_and_filters_today()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var label = new LabelItem { Id = "label_test", Name = "test", IntegrationId = IntegrationIds.Local };
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_1",
            Title = "Today task",
            Priority = 1,
            PlannedOn = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime),
            Labels = [label],
        });

        var today = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Today });

        var task = Assert.Single(today);
        Assert.Equal("Today task", task.Title);
        Assert.Equal("test", Assert.Single(task.Labels).Name);
    }

    [Fact]
    public async Task Date_smart_lists_include_planned_deadline_and_routine_facets()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var today = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);

        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_planned",
            Title = "Planned today",
            PlannedOn = today,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_deadline",
            Title = "Deadline today",
            DeadlineOn = today,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_routine",
            Title = "Routine today",
            PlannedOn = today,
            RecurrenceRule = "every day",
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_overdue_plan",
            Title = "Missed plan",
            PlannedOn = today.AddDays(-1),
        });

        var todayTasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Today });
        var nonRepeatingTasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Today, RepeatScope = TaskRepeatScope.Exclude });
        var routineTasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Today, RepeatScope = TaskRepeatScope.Only });
        var overdueTasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Overdue });
        var calendarTasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Calendar });

        Assert.Equal(new[] { "task_deadline", "task_planned", "task_routine" }, todayTasks.Select(task => task.Id).OrderBy(id => id));
        Assert.Equal(new[] { "task_deadline", "task_planned" }, nonRepeatingTasks.Select(task => task.Id).OrderBy(id => id));
        Assert.Equal("task_routine", Assert.Single(routineTasks).Id);
        Assert.Equal("task_overdue_plan", Assert.Single(overdueTasks).Id);
        Assert.Equal(4, calendarTasks.Count);
    }

    [Fact]
    public async Task UpsertTask_clears_stale_planned_time_when_planned_date_disagrees()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var today = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);

        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_future_date_stale_time",
            Title = "Future date with stale time",
            PlannedOn = today.AddDays(5),
            PlannedAt = DateTimeOffset.Now.AddDays(-1),
        });

        var task = await store.GetTaskAsync("task_future_date_stale_time");
        var overdueTasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Overdue });

        Assert.NotNull(task);
        Assert.Equal(today.AddDays(5), task.PlannedOn);
        Assert.Null(task.PlannedAt);
        Assert.Empty(overdueTasks);
    }

    [Fact]
    public async Task Initialize_cleans_existing_stale_planned_time_when_planned_date_disagrees()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var today = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);

        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_existing_stale_time",
            Title = "Existing stale time",
            PlannedOn = today.AddDays(5),
        });
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE tasks SET planned_at = @planned_at WHERE id = 'task_existing_stale_time'";
            command.Parameters.AddWithValue("@planned_at", DateTimeOffset.Now.AddDays(-1).ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync();
        }

        await store.InitializeAsync();

        var task = await store.GetTaskAsync("task_existing_stale_time");
        var overdueTasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Overdue });
        var counts = await store.GetTaskCountsAsync();

        Assert.NotNull(task);
        Assert.Equal(today.AddDays(5), task.PlannedOn);
        Assert.Null(task.PlannedAt);
        Assert.Empty(overdueTasks);
        Assert.Equal(0, counts.Overdue);
    }

    [Fact]
    public async Task UpsertTask_roundtrips_completion_workflow_and_planner_fields()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var planned = new DateTimeOffset(2026, 5, 12, 0, 0, 0, TimeSpan.Zero);
        var scheduledStart = new DateTimeOffset(2026, 5, 12, 9, 30, 0, TimeSpan.Zero);
        var scheduledEnd = scheduledStart.AddMinutes(45);

        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_planner",
            Title = "Plan the day",
            Notes = "Local task notes",
            CompletionState = TaskCompletionState.Open,
            WorkflowStatus = TaskWorkflowStatus.Waiting,
            PlannedOn = DateOnly.FromDateTime(planned.LocalDateTime),
            PlannedAt = planned,
            DeadlineOn = DateOnly.FromDateTime(planned.AddDays(1).LocalDateTime),
            ScheduledStart = scheduledStart,
            ScheduledEnd = scheduledEnd,
            DurationMinutes = 45,
            RecurrenceRule = "FREQ=WEEKLY;BYDAY=MO",
            SourceMetadataJson = """{"provider":"test"}""",
            LocalMetadataJson = """{"local":true}""",
        });

        var task = await store.GetTaskAsync("task_planner");

        Assert.NotNull(task);
        Assert.Null(task.Description);
        Assert.Null(task.SourceDescription);
        Assert.Equal("Local task notes", task.Notes);
        Assert.Equal(TaskCompletionState.Open, task.CompletionState);
        Assert.Equal(TaskWorkflowStatus.Waiting, task.WorkflowStatus);
        Assert.Equal(TaskItemStatus.Waiting, task.Status);
        Assert.Equal(DateOnly.FromDateTime(planned.LocalDateTime), task.PlannedOn);
        Assert.Equal(planned, task.PlannedAt);
        Assert.Equal(DateOnly.FromDateTime(planned.AddDays(1).LocalDateTime), task.DeadlineOn);
        Assert.Equal(scheduledStart, task.ScheduledStart);
        Assert.Equal(scheduledEnd, task.ScheduledEnd);
        Assert.Equal(45, task.DurationMinutes);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", task.RecurrenceRule);
        Assert.Equal("""{"provider":"test"}""", task.SourceMetadataJson);
        Assert.Equal("""{"local":true}""", task.LocalMetadataJson);
    }

    [Fact]
    public void TaskItem_defaults_to_open_inbox_workflow_status()
    {
        var task = new TaskItem
        {
            Id = "task_default",
            Title = "Default task",
        };

        Assert.Equal(TaskCompletionState.Open, task.CompletionState);
        Assert.Equal(TaskWorkflowStatus.Inbox, task.WorkflowStatus);
        Assert.Equal(TaskItemStatus.Inbox, task.Status);
    }

    [Fact]
    public async Task GetTasks_filters_by_label()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var target = new LabelItem { Id = "label_target", Name = "target", IntegrationId = IntegrationIds.Local };
        var other = new LabelItem { Id = "label_other", Name = "other", IntegrationId = IntegrationIds.Local };

        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_target",
            Title = "Target task",
            Labels = [target],
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_other",
            Title = "Other task",
            Labels = [other],
        });

        var tasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All, LabelId = "label_target" });

        var task = Assert.Single(tasks);
        Assert.Equal("task_target", task.Id);
    }

    [Fact]
    public async Task UpsertTask_reuses_existing_label_name_when_unique_name_index_exists()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertLabelAsync(new LabelItem
        {
            Id = "label_important",
            Name = "important",
            IntegrationId = IntegrationIds.Local,
        });
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_labels_name_test ON labels(name)";
            await command.ExecuteNonQueryAsync();
        }

        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_existing_label",
            Title = "Existing label",
            Labels =
            [
                new LabelItem
                {
                    Id = "label_new_important",
                    Name = "important",
                    IntegrationId = IntegrationIds.Local,
                },
            ],
        });

        var task = Assert.Single(await store.GetTasksAsync(new TaskQuery { LabelId = "label_important" }));
        Assert.Equal("task_existing_label", task.Id);
        Assert.Equal("label_important", Assert.Single(task.Labels).Id);
    }

    [Fact]
    public async Task NextActions_shows_only_explicit_next_tasks()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_next",
            Title = "Next task",
            Status = TaskItemStatus.Next,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_labeled",
            Title = "Labeled but not next",
            Labels = [new LabelItem { Id = "label_next", Name = "next", IntegrationId = IntegrationIds.Local }],
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_done",
            Title = "Completed next task",
            Status = TaskItemStatus.Completed,
        });

        var tasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.NextActions });

        var task = Assert.Single(tasks);
        Assert.Equal("task_next", task.Id);
    }

    [Fact]
    public async Task Inbox_shows_capture_tasks_without_project_only()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_inbox",
            Title = "Clarify me",
            Status = TaskItemStatus.Inbox,
        });
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "proj_work",
            Name = "Work",
            IntegrationId = IntegrationIds.Local,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_project",
            Title = "Project support",
            ProjectId = "proj_work",
            Status = TaskItemStatus.None,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_project_inbox",
            Title = "Project inbox should still need clarification",
            ProjectId = "proj_work",
            Status = TaskItemStatus.Inbox,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_waiting",
            Title = "Waiting",
            Status = TaskItemStatus.Waiting,
        });

        var tasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Inbox });

        Assert.Equal(new[] { "task_inbox" }, tasks.Select(task => task.Id).ToArray());
    }

    [Fact]
    public async Task AdoptProviderSourceItem_adds_one_time_open_source_task_to_inbox()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_todoist_1",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_1",
            ProviderTaskId = "remote_1",
            Title = "Clarify provider task",
            Description = "Remote task description",
            SourceProjectName = "Todoist Inbox",
            CompletionState = TaskCompletionState.Open,
        });

        var added = await store.AdoptProviderSourceItemAsync("source_todoist_1");
        var inbox = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Inbox });

        Assert.NotNull(added);
        var task = Assert.Single(inbox);
        Assert.Equal(added.Id, task.Id);
        Assert.Equal(IntegrationIds.Local, task.IntegrationId);
        Assert.Equal(TaskItemStatus.Inbox, task.Status);
        Assert.Null(task.ProjectId);
        Assert.Null(task.Description);
        Assert.Equal("Remote task description", task.SourceDescription);
        Assert.Equal(IntegrationIds.Todoist, task.SourceIntegrationId);
        Assert.Equal("Todoist Inbox", task.SourceProjectName);
    }

    [Fact]
    public async Task ProviderSource_refresh_updates_source_description_without_overwriting_openza_notes()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var localLabel = new LabelItem
        {
            Id = "label_local",
            IntegrationId = IntegrationIds.Local,
            Name = "local",
        };
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_todoist_description",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_description",
            ProviderTaskId = "remote_description",
            Title = "Provider task",
            Description = "Remote description v1",
        });
        var adopted = await store.AdoptProviderSourceItemAsync("source_todoist_description");
        Assert.NotNull(adopted);
        await store.UpsertTaskAsync(adopted with
        {
            Notes = "Openza notes",
            Labels = [localLabel],
        });

        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_todoist_description",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_description",
            ProviderTaskId = "remote_description",
            Title = "Provider task updated",
            Description = "Remote description v2",
        });

        var task = await store.GetTaskAsync(adopted.Id);

        Assert.NotNull(task);
        Assert.Null(task.Description);
        Assert.Equal("Remote description v2", task.SourceDescription);
        Assert.Equal("Openza notes", task.Notes);
        Assert.Equal("local", Assert.Single(task.Labels).Name);
    }

    [Fact]
    public async Task AdoptProviderSourceItem_adds_recurring_dated_source_task_to_someday()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_todoist_recurring",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_recurring",
            ProviderTaskId = "remote_recurring",
            Title = "Pay monthly bill",
            SourceProjectName = "Bills",
            CompletionState = TaskCompletionState.Open,
            PlannedOn = new DateOnly(2026, 6, 6),
            RecurrenceRule = "every 6th",
        });

        var added = await store.AdoptProviderSourceItemAsync("source_todoist_recurring");
        var inbox = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Inbox });
        var all = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All });

        Assert.NotNull(added);
        Assert.Empty(inbox);
        var task = Assert.Single(all);
        Assert.Equal(added.Id, task.Id);
        Assert.Equal(TaskWorkflowStatus.Someday, task.WorkflowStatus);
        Assert.Equal(TaskCompletionState.Open, task.CompletionState);
        Assert.Equal(new DateOnly(2026, 6, 6), task.PlannedOn);
        Assert.Equal("every 6th", task.RecurrenceRule);
    }

    [Fact]
    public async Task UpsertProviderSourceItem_auto_adds_recurring_dated_source_task_to_someday()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_todoist_auto_recurring",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_auto_recurring",
            ProviderTaskId = "remote_auto_recurring",
            Title = "Automatic recurring bill",
            SuggestedSpaceId = SpaceIds.Default,
            PlannedOn = new DateOnly(2026, 6, 6),
            RecurrenceRule = "every 6th",
        });

        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        var inbox = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Inbox, SpaceId = SpaceIds.Default });
        var all = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All, SpaceId = SpaceIds.Default });

        Assert.Equal(ProviderSourceAdoptionStates.Adopted, source.AdoptionState);
        Assert.Empty(inbox);
        var task = Assert.Single(all);
        Assert.Equal(source.AdoptedTaskId, task.Id);
        Assert.Equal(TaskWorkflowStatus.Someday, task.WorkflowStatus);
        Assert.Equal("every 6th", task.RecurrenceRule);
    }

    [Fact]
    public async Task UpsertProviderSourceItem_auto_add_uses_active_space_when_default_is_hidden()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = SpaceIds.Default, Name = "My space", IsArchived = true });
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_work", Name = "Work", SortOrder = 1 });

        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_todoist_visible_space",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_visible_space",
            ProviderTaskId = "remote_visible_space",
            Title = "Visible recurring bill",
            PlannedOn = new DateOnly(2026, 6, 6),
            RecurrenceRule = "every 6th",
        });

        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        var workTasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All, SpaceId = "space_work" });
        var defaultTasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All, SpaceId = SpaceIds.Default });

        Assert.Equal(ProviderSourceAdoptionStates.Adopted, source.AdoptionState);
        Assert.Null(source.SuggestedSpaceId);
        Assert.Empty(defaultTasks);
        var task = Assert.Single(workTasks);
        Assert.Equal(source.AdoptedTaskId, task.Id);
        Assert.Equal("space_work", task.SpaceId);
        Assert.Equal(TaskWorkflowStatus.Someday, task.WorkflowStatus);
    }

    [Fact]
    public async Task DeleteTask_deletes_local_only_task()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_local_delete",
            Title = "Local delete",
        });

        await store.DeleteTaskAsync("task_local_delete");

        Assert.Null(await store.GetTaskAsync("task_local_delete"));
    }

    [Fact]
    public async Task DeleteTask_blocks_adopted_provider_link()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_linked_delete",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_linked_delete",
            ProviderTaskId = "remote_linked_delete",
            Title = "Linked delete",
        });
        var task = await store.AdoptProviderSourceItemAsync("source_linked_delete");
        Assert.NotNull(task);

        var exception = await Assert.ThrowsAsync<ProviderLinkedTaskDeleteException>(() => store.DeleteTaskAsync(task.Id));

        Assert.Equal(task.Id, exception.TaskId);
        Assert.Equal(IntegrationIds.Todoist, exception.Provider);
        Assert.NotNull(await store.GetTaskAsync(task.Id));
    }

    [Fact]
    public async Task DeleteTask_allows_delete_after_stale_provider_link_is_detached()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_stale_delete",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_stale_delete",
            ProviderTaskId = "remote_stale_delete",
            Title = "Stale delete",
        });
        var task = await store.AdoptProviderSourceItemAsync("source_stale_delete");
        Assert.NotNull(task);

        await store.DetachProviderSourceItemAsync("source_stale_delete");
        await store.DeleteTaskAsync(task.Id);

        Assert.Null(await store.GetTaskAsync(task.Id));
        Assert.Empty(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true, includeIgnored: true));
    }

    [Fact]
    public async Task UpsertProviderSourceItem_stores_provider_parent_reference()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_child",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_child",
            ProviderTaskId = "remote_child",
            ParentExternalId = "remote_parent",
            Title = "Child source task",
        });

        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));

        Assert.Equal("remote_parent", source.ParentExternalId);
    }

    [Fact]
    public async Task UpsertProviderSourceItem_links_child_source_under_adopted_parent()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_parent",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_parent",
            ProviderTaskId = "remote_parent",
            Title = "Parent task",
        });
        var parent = await store.AdoptProviderSourceItemAsync("source_parent");

        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_child",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_child",
            ProviderTaskId = "remote_child",
            ParentExternalId = "remote_parent",
            Title = "Child task",
        });

        Assert.NotNull(parent);
        var childSource = Assert.Single(
            await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true),
            source => source.ExternalId == "remote_child");
        var child = await store.GetTaskAsync(childSource.AdoptedTaskId!);
        var visibleTasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All });

        Assert.Equal(ProviderSourceAdoptionStates.Adopted, childSource.AdoptionState);
        Assert.NotNull(child);
        Assert.Equal(parent.Id, child.ParentId);
        Assert.Equal(new[] { parent.Id, child.Id }, visibleTasks.Select(task => task.Id).ToArray());
    }

    [Fact]
    public async Task UpsertProviderSourceItem_does_not_nest_subtasks_under_subtasks()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_parent",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_parent",
            ProviderTaskId = "remote_parent",
            Title = "Parent task",
        });
        var parent = await store.AdoptProviderSourceItemAsync("source_parent");
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_child",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_child",
            ProviderTaskId = "remote_child",
            ParentExternalId = "remote_parent",
            Title = "Child task",
        });
        var childSource = Assert.Single(
            await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true),
            source => source.ExternalId == "remote_child");
        var child = await store.GetTaskAsync(childSource.AdoptedTaskId!);

        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_grandchild",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_grandchild",
            ProviderTaskId = "remote_grandchild",
            ParentExternalId = "remote_child",
            Title = "Grandchild stays in intake",
        });

        Assert.NotNull(parent);
        Assert.NotNull(child);
        Assert.Equal(parent.Id, child.ParentId);
        var grandchildSource = Assert.Single(
            await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true),
            source => source.ExternalId == "remote_grandchild");
        var visibleTasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All });

        Assert.Equal(ProviderSourceAdoptionStates.NotAdopted, grandchildSource.AdoptionState);
        Assert.Null(grandchildSource.AdoptedTaskId);
        Assert.Equal(new[] { parent.Id, child.Id }, visibleTasks.Select(task => task.Id).ToArray());
    }

    [Fact]
    public async Task Initialize_relinks_existing_imported_subtasks_from_source_snapshot()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_parent",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_parent",
            ProviderTaskId = "remote_parent",
            Title = "Parent task",
        });
        var parent = await store.AdoptProviderSourceItemAsync("source_parent");
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_child",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_child",
            ProviderTaskId = "remote_child",
            Title = "Existing child",
            SnapshotJson = """{"sourceTask":{"parentId":"remote_parent"}}""",
        });
        var child = await store.AdoptProviderSourceItemAsync("source_child");
        Assert.NotNull(parent);
        Assert.NotNull(child);
        Assert.Null(child.ParentId);

        await store.InitializeAsync();

        var relinked = await store.GetTaskAsync(child.Id);
        var childSource = Assert.Single(
            await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true),
            source => source.ExternalId == "remote_child");
        Assert.NotNull(relinked);
        Assert.Equal(parent.Id, relinked.ParentId);
        Assert.Equal("remote_parent", childSource.ParentExternalId);
    }

    [Fact]
    public async Task Initialize_relinked_subtasks_inherit_parent_workflow_context()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_parent",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_parent",
            ProviderTaskId = "remote_parent",
            Title = "Recurring parent",
            PlannedOn = new DateOnly(2026, 5, 15),
            RecurrenceRule = "every day",
        });
        var parent = await store.AdoptProviderSourceItemAsync("source_parent");
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_child",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_child",
            ProviderTaskId = "remote_child",
            Title = "Existing child",
            SnapshotJson = """{"sourceTask":{"parentId":"remote_parent"}}""",
        });
        var child = await store.AdoptProviderSourceItemAsync("source_child");
        Assert.NotNull(parent);
        Assert.NotNull(child);
        Assert.Equal(TaskWorkflowStatus.Someday, parent.WorkflowStatus);
        Assert.Equal(TaskWorkflowStatus.Inbox, child.WorkflowStatus);

        await store.InitializeAsync();

        var relinked = await store.GetTaskAsync(child.Id);
        var inbox = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Inbox });

        Assert.NotNull(relinked);
        Assert.Equal(parent.Id, relinked.ParentId);
        Assert.Equal(parent.SpaceId, relinked.SpaceId);
        Assert.Equal(parent.ProjectId, relinked.ProjectId);
        Assert.Equal(TaskWorkflowStatus.Someday, relinked.WorkflowStatus);
        Assert.Empty(inbox);
    }

    [Fact]
    public async Task Initialize_does_not_relink_existing_task_under_an_existing_subtask()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_parent",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_parent",
            ProviderTaskId = "remote_parent",
            Title = "Parent task",
        });
        var parent = await store.AdoptProviderSourceItemAsync("source_parent");
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_child",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_child",
            ProviderTaskId = "remote_child",
            Title = "Existing child",
            SnapshotJson = """{"sourceTask":{"parentId":"remote_parent"}}""",
        });
        var child = await store.AdoptProviderSourceItemAsync("source_child");
        Assert.NotNull(parent);
        Assert.NotNull(child);

        await store.InitializeAsync();
        child = await store.GetTaskAsync(child.Id);
        Assert.NotNull(child);
        Assert.Equal(parent.Id, child.ParentId);

        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_grandchild",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_grandchild",
            ProviderTaskId = "remote_grandchild",
            Title = "Existing grandchild",
            SnapshotJson = """{"sourceTask":{"parentId":"remote_child"}}""",
        });
        var grandchild = await store.AdoptProviderSourceItemAsync("source_grandchild");
        Assert.NotNull(grandchild);
        Assert.Null(grandchild.ParentId);

        await store.InitializeAsync();

        var unchangedGrandchild = await store.GetTaskAsync(grandchild.Id);
        Assert.NotNull(unchangedGrandchild);
        Assert.Null(unchangedGrandchild.ParentId);
    }

    [Fact]
    public async Task Task_counts_exclude_nested_subtasks()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_parent",
            Title = "Parent",
            Status = TaskItemStatus.Next,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_child",
            Title = "Child",
            ParentId = "task_parent",
            Status = TaskItemStatus.Next,
        });

        var counts = await store.GetTaskCountsAsync();
        var tasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All });

        Assert.Equal(1, counts.NextActions);
        Assert.Equal(1, counts.Open);
        Assert.Equal(1, counts.All);
        Assert.Equal(new[] { "task_parent", "task_child" }, tasks.Select(task => task.Id).ToArray());
    }

    [Fact]
    public async Task Initialize_moves_existing_recurring_dated_inbox_tasks_to_someday()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_recurring_inbox",
            Title = "Monthly bill",
            WorkflowStatus = TaskWorkflowStatus.Inbox,
            PlannedOn = new DateOnly(2026, 6, 6),
            RecurrenceRule = "every 6th",
        });

        await store.InitializeAsync();

        var inbox = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Inbox });
        var all = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All });

        Assert.Empty(inbox);
        Assert.Equal(TaskWorkflowStatus.Someday, Assert.Single(all).WorkflowStatus);
    }

    [Fact]
    public async Task GetProviderSourceItems_includes_unrouted_source_items_in_space_intake()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_work", Name = "Work" });
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_personal", Name = "Personal" });
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_unrouted",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_unrouted",
            ProviderTaskId = "remote_unrouted",
            Title = "Unrouted provider task",
            SuggestedSpaceId = "space_removed",
        });
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_personal",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_personal",
            ProviderTaskId = "remote_personal",
            Title = "Personal provider task",
            SuggestedSpaceId = "space_personal",
        });

        var workItems = await store.GetProviderSourceItemsAsync(spaceId: "space_work");

        var item = Assert.Single(workItems);
        Assert.Equal("source_unrouted", item.Id);
        Assert.Null(item.SuggestedSpaceId);
    }

    [Fact]
    public async Task SkipProviderSourceItem_hides_source_item_without_creating_task()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_todoist_skip",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_skip",
            ProviderTaskId = "remote_skip",
            Title = "Do not bring into Openza",
            SourceProjectName = "Todoist Inbox",
        });

        var skipped = await store.SkipProviderSourceItemAsync("source_todoist_skip");

        Assert.True(skipped);
        Assert.Empty(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
        Assert.Empty(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
        var allSourceItems = await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true, includeIgnored: true);
        Assert.Equal(ProviderSourceAdoptionStates.Ignored, Assert.Single(allSourceItems).AdoptionState);
    }

    [Fact]
    public async Task UnskipProviderSourceItem_restores_skipped_item_to_waiting_intake()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source_todoist_restore",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "remote_restore",
            ProviderTaskId = "remote_restore",
            Title = "Bring back",
        });
        Assert.True(await store.SkipProviderSourceItemAsync("source_todoist_restore"));

        var restored = await store.UnskipProviderSourceItemAsync("source_todoist_restore");

        Assert.True(restored);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
        Assert.Equal(ProviderSourceAdoptionStates.NotAdopted, source.AdoptionState);
    }

    [Fact]
    public async Task GetTaskCounts_returns_smart_list_and_project_counts()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_a",
            Name = "Project A",
            IntegrationId = IntegrationIds.Local,
        });
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_b",
            Name = "Project B",
            IntegrationId = IntegrationIds.Local,
        });

        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_today",
            Title = "Today",
            ProjectId = "project_a",
            PlannedOn = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime),
            Status = TaskItemStatus.Next,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_overdue",
            Title = "Overdue",
            ProjectId = "project_a",
            DeadlineOn = DateOnly.FromDateTime(DateTimeOffset.Now.AddDays(-1).LocalDateTime),
            Status = TaskItemStatus.None,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_inbox",
            Title = "Inbox",
            Status = TaskItemStatus.Inbox,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_project_inbox",
            Title = "Assigned inbox task",
            ProjectId = "project_b",
            Status = TaskItemStatus.Inbox,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_waiting",
            Title = "Waiting",
            Status = TaskItemStatus.Waiting,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_someday",
            Title = "Someday",
            Status = TaskItemStatus.Someday,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_done",
            Title = "Completed",
            ProjectId = "project_a",
            Status = TaskItemStatus.Completed,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_other",
            Title = "Other project",
            ProjectId = "project_b",
            Status = TaskItemStatus.None,
        });

        var counts = await store.GetTaskCountsAsync();

        Assert.Equal(1, counts.Inbox);
        Assert.Equal(1, counts.NextActions);
        Assert.Equal(1, counts.Waiting);
        Assert.Equal(1, counts.Someday);
        Assert.Equal(1, counts.Today);
        Assert.Equal(2, counts.Calendar);
        Assert.Equal(1, counts.Overdue);
        Assert.Equal(7, counts.Open);
        Assert.Equal(8, counts.All);
        Assert.Equal(1, counts.Completed);
        Assert.Equal(2, counts.ActiveByProject["project_a"]);
        Assert.Equal(2, counts.ActiveByProject["project_b"]);
        Assert.Equal(1, counts.NextByProject["project_a"]);
        Assert.Equal(0, counts.NextByProject["project_b"]);
    }

    [Fact]
    public async Task Project_lifecycle_roundtrips_and_archived_projects_are_hidden_by_default()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_active",
            Name = "Active",
            IntegrationId = IntegrationIds.Local,
            Status = ProjectLifecycleStates.Active,
        });
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_completed",
            Name = "Completed",
            IntegrationId = IntegrationIds.Local,
            Status = ProjectLifecycleStates.Completed,
        });
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_archived",
            Name = "Archived",
            IntegrationId = IntegrationIds.Local,
            Status = ProjectLifecycleStates.Archived,
            IsArchived = true,
        });

        var visibleProjects = await store.GetProjectsAsync();
        var allProjects = await store.GetProjectsAsync(includeArchived: true);

        Assert.Contains(visibleProjects, project => project.Id == "project_active" && project.IsActive);
        Assert.Contains(visibleProjects, project => project.Id == "project_completed" && project.IsCompleted);
        Assert.DoesNotContain(visibleProjects, project => project.Id == "project_archived");
        Assert.Contains(allProjects, project => project.Id == "project_archived" && project.EffectiveStatus == ProjectLifecycleStates.Archived);
    }

    [Fact]
    public async Task Project_counts_return_factual_open_and_next_counts()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_without_next",
            Name = "Project without next",
            IntegrationId = IntegrationIds.Local,
        });
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_with_next",
            Name = "Has next",
            IntegrationId = IntegrationIds.Local,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_waiting",
            Title = "Waiting",
            ProjectId = "project_without_next",
            Status = TaskItemStatus.Waiting,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_next",
            Title = "Next",
            ProjectId = "project_with_next",
            Status = TaskItemStatus.Next,
        });

        var counts = await store.GetTaskCountsAsync();

        Assert.Equal(1, counts.ActiveByProject["project_without_next"]);
        Assert.Equal(0, counts.NextByProject["project_without_next"]);
        Assert.Equal(1, counts.ActiveByProject["project_with_next"]);
        Assert.Equal(1, counts.NextByProject["project_with_next"]);
    }

    [Fact]
    public async Task Search_finds_open_tasks_by_title_notes_and_source_description()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_title",
            Title = "Prepare vendor call",
            Notes = "General notes",
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_notes",
            Title = "Plan admin work",
            Notes = "Confirm vendor contract details",
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_source",
            Title = "Imported follow-up",
            SourceDescription = "Vendor invoice came from Todoist",
        });

        var results = await store.SearchAsync(new GlobalSearchQuery { SearchText = "vendor" });

        var taskIds = results.Where(result => result.Kind == GlobalSearchResultKind.Task).Select(result => result.Id).ToArray();
        Assert.Equal("task_title", taskIds.First());
        Assert.Equal(new[] { "task_notes", "task_source", "task_title" }, taskIds.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task Search_finds_projects_by_name_and_description()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_title",
            Name = "Renovation",
            IntegrationId = IntegrationIds.Local,
        });
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_description",
            Name = "Home",
            Description = "Renovation paperwork",
            IntegrationId = IntegrationIds.Local,
        });

        var results = await store.SearchAsync(new GlobalSearchQuery { SearchText = "renovation" });

        Assert.Equal(
            new[] { "project_title", "project_description" },
            results.Where(result => result.Kind == GlobalSearchResultKind.Project).Select(result => result.Id).ToArray());
    }

    [Fact]
    public async Task Search_current_space_excludes_other_spaces()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_work", Name = "Work" });
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_home", Name = "Home" });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_work",
            SpaceId = "space_work",
            Title = "Budget review",
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_home",
            SpaceId = "space_home",
            Title = "Budget review",
        });

        var results = await store.SearchAsync(new GlobalSearchQuery
        {
            SearchText = "budget",
            SpaceId = "space_work",
        });

        var task = Assert.Single(results);
        Assert.Equal("task_work", task.Id);
        Assert.Equal("space_work", task.SpaceId);
    }

    [Fact]
    public async Task Search_all_spaces_includes_other_spaces_and_returns_space_id()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_work", Name = "Work" });
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_home", Name = "Home" });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_work",
            SpaceId = "space_work",
            Title = "Budget review",
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_home",
            SpaceId = "space_home",
            Title = "Budget review",
        });

        var results = await store.SearchAsync(new GlobalSearchQuery
        {
            SearchText = "budget",
            SpaceId = "space_work",
            IncludeAllSpaces = true,
        });

        Assert.Equal(new[] { "space_home", "space_work" }, results.Select(result => result.SpaceId).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task Search_all_spaces_excludes_archived_spaces()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_active", Name = "Active" });
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_archived", Name = "Archived", IsArchived = true });
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_active",
            SpaceId = "space_active",
            Name = "Budget planning",
            IntegrationId = IntegrationIds.Local,
        });
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_archived_space",
            SpaceId = "space_archived",
            Name = "Budget archive",
            IntegrationId = IntegrationIds.Local,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_active",
            SpaceId = "space_active",
            Title = "Budget review",
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_archived_space",
            SpaceId = "space_archived",
            Title = "Budget review hidden",
        });

        var results = await store.SearchAsync(new GlobalSearchQuery
        {
            SearchText = "budget",
            IncludeAllSpaces = true,
        });

        Assert.Contains(results, result => result.Id == "task_active");
        Assert.Contains(results, result => result.Id == "project_active");
        Assert.DoesNotContain(results, result => result.SpaceId == "space_archived");
    }

    [Fact]
    public async Task Search_excludes_completed_tasks_until_requested()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_open",
            Title = "Archive receipts",
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_completed",
            Title = "Archive receipts from April",
            Status = TaskItemStatus.Completed,
        });

        var defaultResults = await store.SearchAsync(new GlobalSearchQuery { SearchText = "archive receipts" });
        var withCompleted = await store.SearchAsync(new GlobalSearchQuery
        {
            SearchText = "archive receipts",
            IncludeCompletedTasks = true,
        });

        Assert.Equal("task_open", Assert.Single(defaultResults).Id);
        Assert.Equal(new[] { "task_open", "task_completed" }, withCompleted.Select(result => result.Id).ToArray());
    }

    [Fact]
    public async Task Search_excludes_archived_projects()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_active",
            Name = "Compliance archive",
            IntegrationId = IntegrationIds.Local,
            Status = ProjectLifecycleStates.Active,
        });
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_archived",
            Name = "Compliance archive old",
            IntegrationId = IntegrationIds.Local,
            Status = ProjectLifecycleStates.Archived,
            IsArchived = true,
        });

        var results = await store.SearchAsync(new GlobalSearchQuery { SearchText = "compliance archive" });

        Assert.Equal("project_active", Assert.Single(results).Id);
    }

    [Fact]
    public async Task Search_ranks_title_prefix_before_note_only_matches()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_notes",
            Title = "General planning",
            Notes = "Quarterly review agenda",
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_title",
            Title = "Review quarterly plan",
        });

        var results = await store.SearchAsync(new GlobalSearchQuery { SearchText = "review" });

        Assert.Equal("task_title", results.First().Id);
    }

    [Fact]
    public async Task QueueCompletion_roundtrips_and_mark_synced_removes()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.QueueCompletionAsync(new PendingCompletion
        {
            Id = "completion_1",
            TaskId = "task_1",
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote_1",
            Completed = true,
        });

        var pending = await store.GetPendingCompletionsAsync(IntegrationIds.Todoist);
        Assert.Single(pending);

        await store.MarkCompletionSyncedAsync("completion_1");

        Assert.Empty(await store.GetPendingCompletionsAsync(IntegrationIds.Todoist));
    }

    [Fact]
    public async Task SyncRoute_roundtrips_route_and_run_history()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSyncRouteAsync(new SyncRouteInfo
        {
            Id = "route_todoist_obsidian",
            Name = "Todoist to Obsidian",
            SourceConnectionId = "todoist_default",
            Mode = "one_way",
            Visibility = "headless",
            ScheduleJson = """{"trigger":"manual_and_schedule"}""",
            IsEnabled = true,
        });
        var started = DateTimeOffset.UtcNow;
        await store.RecordSyncRouteRunAsync(new SyncRouteRunInfo
        {
            Id = "run_1",
            RouteId = "route_todoist_obsidian",
            StartedAt = started,
            FinishedAt = started.AddSeconds(2),
            Status = "success",
            SummaryJson = """{"created":1}""",
        });

        var route = Assert.Single(await store.GetSyncRoutesAsync());
        var run = Assert.Single(await store.GetSyncRouteRunsAsync("route_todoist_obsidian"));

        Assert.Equal("Todoist to Obsidian", route.Name);
        Assert.Equal("todoist_default", route.SourceConnectionId);
        Assert.True(route.IsEnabled);
        Assert.Equal("headless", route.Visibility);
        Assert.Equal("success", run.Status);
        Assert.Equal("""{"created":1}""", run.SummaryJson);
    }

    [Fact]
    public async Task Initialize_upgrades_existing_sqlite_schema()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "legacy-openza.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE integrations (
                  id TEXT PRIMARY KEY NOT NULL,
                  name TEXT NOT NULL,
                  is_active INTEGER NOT NULL DEFAULT 0,
                  created_at INTEGER NOT NULL
                );

                CREATE TABLE projects (
                  id TEXT PRIMARY KEY NOT NULL,
                  name TEXT NOT NULL,
                  description TEXT,
                  color TEXT,
                  icon TEXT,
                  parent_id TEXT,
                  sort_order INTEGER NOT NULL DEFAULT 0,
                  created_at INTEGER NOT NULL,
                  updated_at INTEGER
                );

                CREATE TABLE tasks (
                  id TEXT PRIMARY KEY NOT NULL,
                  title TEXT NOT NULL,
                  description TEXT,
                  project_id TEXT,
                  parent_id TEXT,
                  priority INTEGER NOT NULL DEFAULT 2,
                  status TEXT NOT NULL DEFAULT 'pending',
                  due_date INTEGER,
                  due_time TEXT,
                  notes TEXT,
                  integrations TEXT,
                  created_at INTEGER NOT NULL,
                  updated_at INTEGER,
                  completed_at INTEGER
                );

                CREATE TABLE labels (
                  id TEXT PRIMARY KEY NOT NULL,
                  name TEXT NOT NULL,
                  color TEXT,
                  description TEXT,
                  sort_order INTEGER NOT NULL DEFAULT 0,
                  integrations TEXT,
                  created_at INTEGER NOT NULL
                );

                CREATE TABLE task_labels (
                  task_id TEXT NOT NULL,
                  label_id TEXT NOT NULL,
                  PRIMARY KEY (task_id, label_id)
                );

                INSERT INTO projects (id, name, created_at) VALUES ('proj_old', 'Old Project', 1710000000);
                INSERT INTO tasks (id, title, project_id, status, created_at)
                VALUES ('task_old', 'Legacy local task', 'proj_old', 'pending', 1710000000);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new SqliteTaskStore(databasePath);
        await store.InitializeAsync();

        var tasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All });
        var task = Assert.Single(tasks, task => task.Id == "task_old");

        Assert.Equal("Legacy local task", task.Title);
        Assert.Equal(TaskItemStatus.Inbox, task.Status);
        Assert.Equal(IntegrationIds.Local, task.IntegrationId);
    }

    [Fact]
    public async Task Initialize_moves_provider_linked_description_to_source_description()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "provider-linked-description.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE tasks (
                  id TEXT PRIMARY KEY NOT NULL,
                  title TEXT NOT NULL,
                  description TEXT,
                  source_integration_id TEXT,
                  source_external_id TEXT,
                  created_at INTEGER NOT NULL
                );

                INSERT INTO tasks (id, title, description, source_integration_id, source_external_id, created_at)
                VALUES ('task_provider_description', 'Provider task', 'Remote provider description', 'todoist', 'remote_1', 1710000000);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new SqliteTaskStore(databasePath);
        await store.InitializeAsync();

        var task = await store.GetTaskAsync("task_provider_description");

        Assert.NotNull(task);
        Assert.Null(task.Description);
        Assert.Equal("Remote provider description", task.SourceDescription);
        Assert.Equal(IntegrationIds.Todoist, task.SourceIntegrationId);
        Assert.Equal("remote_1", task.SourceExternalId);
    }

    [Fact]
    public async Task Initialize_moves_local_description_to_notes()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "local-description.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE tasks (
                  id TEXT PRIMARY KEY NOT NULL,
                  title TEXT NOT NULL,
                  description TEXT,
                  notes TEXT,
                  created_at INTEGER NOT NULL
                );

                INSERT INTO tasks (id, title, description, notes, created_at)
                VALUES
                  ('task_description_only', 'Description only', 'Old description', NULL, 1710000000),
                  ('task_description_and_notes', 'Description and notes', 'Old description', 'Existing notes', 1710000001);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new SqliteTaskStore(databasePath);
        await store.InitializeAsync();

        var descriptionOnly = await store.GetTaskAsync("task_description_only");
        var descriptionAndNotes = await store.GetTaskAsync("task_description_and_notes");

        Assert.NotNull(descriptionOnly);
        Assert.Null(descriptionOnly.Description);
        Assert.Equal("Old description", descriptionOnly.Notes);
        Assert.Null(descriptionOnly.SourceDescription);

        Assert.NotNull(descriptionAndNotes);
        Assert.Null(descriptionAndNotes.Description);
        Assert.Equal("Existing notes\n\n---\nOld description", descriptionAndNotes.Notes);
        Assert.Null(descriptionAndNotes.SourceDescription);
    }

    private SqliteTaskStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new SqliteTaskStore(Path.Combine(_directory, "openza_tasks.db"));
    }

    private static async Task<long> ExecuteScalarAsync(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<string?> ExecuteScalarTextAsync(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    public void Dispose()
    {
        TestDirectory.Delete(_directory);
    }
}
