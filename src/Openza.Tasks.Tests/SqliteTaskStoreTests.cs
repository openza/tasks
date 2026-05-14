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
        Assert.DoesNotContain(integrations, i => i.Id == IntegrationIds.Obsidian);
        Assert.Contains(connections, c => c.Id == "local_default" && c.Status == "connected");
        Assert.Contains(connections, c => c.Id == "todoist_default" && c.Status == "disconnected");
        Assert.Contains(connections, c => c.Id == "mstodo_default" && c.Status == "disconnected");
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

        Assert.Equal(3L, await ExecuteScalarAsync(connection, "PRAGMA user_version"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'provider_connections'"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'provider_source_items'"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sync_routes'"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sync_field_state'"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sync_operations'"));
        Assert.Equal("'none'", await ExecuteScalarTextAsync(connection, "SELECT dflt_value FROM pragma_table_info('tasks') WHERE name = 'workflow_status'"));
        Assert.Equal("'active'", await ExecuteScalarTextAsync(connection, "SELECT dflt_value FROM pragma_table_info('projects') WHERE name = 'status'"));
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
    public void TaskItem_defaults_to_open_without_workflow_status()
    {
        var task = new TaskItem
        {
            Id = "task_default",
            Title = "Default task",
        };

        Assert.Equal(TaskCompletionState.Open, task.CompletionState);
        Assert.Equal(TaskWorkflowStatus.None, task.WorkflowStatus);
        Assert.Equal(TaskItemStatus.None, task.Status);
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
    public async Task Inbox_shows_unlisted_tasks_without_project()
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
            Title = "Project inbox should not pollute capture inbox",
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

        var task = Assert.Single(tasks);
        Assert.Equal("task_inbox", task.Id);
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
        Assert.Equal(IntegrationIds.Todoist, task.SourceIntegrationId);
        Assert.Equal("Todoist Inbox", task.SourceProjectName);
    }

    [Fact]
    public async Task AdoptProviderSourceItem_adds_recurring_dated_source_task_without_inbox_workflow()
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
        Assert.Equal(TaskWorkflowStatus.None, task.WorkflowStatus);
        Assert.Equal(TaskCompletionState.Open, task.CompletionState);
        Assert.Equal(new DateOnly(2026, 6, 6), task.PlannedOn);
        Assert.Equal("every 6th", task.RecurrenceRule);
    }

    [Fact]
    public async Task UpsertProviderSourceItem_auto_adds_recurring_dated_source_task_outside_inbox()
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
        Assert.Equal(TaskWorkflowStatus.None, task.WorkflowStatus);
        Assert.Equal("every 6th", task.RecurrenceRule);
    }

    [Fact]
    public async Task Initialize_moves_existing_recurring_dated_inbox_tasks_out_of_inbox()
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
        Assert.Equal(TaskWorkflowStatus.None, Assert.Single(all).WorkflowStatus);
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
