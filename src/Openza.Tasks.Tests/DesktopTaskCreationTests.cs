using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed class DesktopTaskCreationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "openza-tasks-desktop-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateTaskAsync_parses_trims_and_deduplicates_comma_separated_labels()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());

        await viewModel.CreateTaskAsync(new AddTaskDraft(
            "Review labels",
            string.Empty,
            null,
            0,
            2,
            null,
            " work, Urgent, WORK,  ",
            false));

        var tasks = await store.GetTasksAsync(new TaskQuery
        {
            Kind = TaskListKind.All,
            IncludeSubtasks = true,
        });

        var task = Assert.Single(tasks);
        Assert.Equal(new[] { "Urgent", "work" }, task.Labels.Select(label => label.Name).Order());
    }

    [Fact]
    public async Task CreateTaskAsync_preserves_completed_status()
    {
        var store = await CreateStoreAsync();
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());

        await viewModel.CreateTaskAsync(new AddTaskDraft(
            "Already complete", string.Empty, null, 4, 2, null, string.Empty, false));

        var task = Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Completed }));
        Assert.True(task.IsCompleted);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public async Task Quick_add_defaults_to_normal_priority()
    {
        var store = await CreateStoreAsync();
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore())
        {
            QuickAddTitle = "Normal priority task",
        };

        await viewModel.AddTaskAsync();

        var task = Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
        Assert.Equal(3, task.Priority);
    }

    [Fact]
    public void List_filter_summary_and_chips_follow_the_active_filters()
    {
        var viewModel = new MainWindowViewModel(
            new SqliteTaskStore(Path.Combine(_directory, "filters.db")),
            new InMemoryCredentialStore());

        Assert.Equal("Filters", viewModel.FilterSummary);
        Assert.False(viewModel.HasActiveOptionFilters);

        viewModel.PriorityFilterIndex = 2;
        viewModel.RepeatFilterIndex = 1;
        viewModel.SelectedLabelFilter = new LabelOptionViewModel(new LabelItem
        {
            Id = "label-work",
            Name = "Work",
        });

        Assert.Equal("Filters (3)", viewModel.FilterSummary);
        Assert.Equal("Filters, 3 active", viewModel.FilterAutomationName);
        Assert.Equal("Priority: High  ×", viewModel.PriorityFilterChipText);
        Assert.Equal("Repeating: Exclude  ×", viewModel.RepeatFilterChipText);
        Assert.Equal("Label: Work  ×", viewModel.LabelFilterChipText);
        Assert.True(viewModel.HasActiveOptionFilters);

        viewModel.SearchText = "missing";

        Assert.True(viewModel.HasActiveListFilters);
        Assert.Equal("No matching tasks", viewModel.EmptyStateTitle);
        Assert.Equal("Clear filters", viewModel.EmptyStateActionText);
    }

    [Fact]
    public void Task_row_metadata_uses_the_calm_WinUI_information_hierarchy()
    {
        var task = CreateTask("metadata", "Metadata task") with
        {
            Priority = 3,
            Labels =
            [
                new LabelItem { Id = "gamma", Name = "gamma" },
                new LabelItem { Id = "alpha", Name = "alpha" },
                new LabelItem { Id = "beta", Name = "beta" },
            ],
        };

        var item = new TaskListItemViewModel(
            task,
            "Long project name",
            TaskListKind.Open,
            subtaskProgressText: "2/5 subtasks");

        Assert.False(item.HasPriority);
        Assert.Equal(string.Empty, item.PriorityText);
        Assert.Contains("Long project name", item.MetadataText);
        Assert.Contains("Inbox", item.MetadataText);
        Assert.Contains("@alpha, @beta +1", item.MetadataText);
        Assert.Contains("2/5 subtasks", item.MetadataText);
        Assert.DoesNotContain("@gamma", item.MetadataText);
    }

    [Fact]
    public void Task_row_metadata_hides_redundant_project_and_status_context()
    {
        var task = CreateTask("project-metadata", "Project task") with { Priority = 1 };

        var item = new TaskListItemViewModel(
            task,
            "Current project",
            TaskListKind.Inbox,
            isProjectView: true);

        Assert.True(item.HasPriority);
        Assert.Equal("Urgent", item.PriorityText);
        Assert.DoesNotContain("Current project", item.MetadataText);
        Assert.DoesNotContain("Inbox", item.MetadataText);
    }

    [Fact]
    public async Task Project_editor_updates_name_status_and_favorite_state()
    {
        var store = await CreateStoreAsync();
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project-edit",
            SpaceId = SpaceIds.Default,
            IntegrationId = IntegrationIds.Local,
            Name = "Original project",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.InitializeAsync();
        viewModel.SelectedProject = Assert.Single(viewModel.ProjectItems, item => item.Project.Id == "project-edit");

        await viewModel.UpdateSelectedProjectAsync(
            "Finished project",
            ProjectLifecycleStates.Completed,
            isFavorite: true);

        var project = Assert.Single(
            await store.GetProjectsAsync(SpaceIds.Default, includeArchived: true),
            item => item.Id == "project-edit");
        Assert.Equal("Finished project", project.Name);
        Assert.Equal(ProjectLifecycleStates.Completed, project.EffectiveStatus);
        Assert.True(project.IsFavorite);
    }

    [Fact]
    public async Task Selected_provider_task_exposes_source_metadata_for_the_inspector()
    {
        var store = await CreateStoreAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source-row",
            IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default",
            ExternalId = "todoist-source-task",
            ProviderTaskId = "todoist-source-task",
            Title = "Todoist title",
            Description = "Original description",
            SourceProjectName = "Work Tasks",
            Priority = 1,
            PlannedOn = new DateOnly(2026, 8, 17),
            RecurrenceRule = "every weekday",
        });
        await store.UpsertTaskAsync(CreateTask("source-task", "Local title") with
        {
            SourceIntegrationId = IntegrationIds.Todoist,
            SourceConnectionId = "todoist_default",
            SourceExternalId = "todoist-source-task",
            SourceTitle = "Todoist title",
            SourceDescription = "Original description",
            SourceProjectName = "Work Tasks",
            SourcePriority = 1,
            SourcePlannedOn = new DateOnly(2026, 8, 17),
            RecurrenceRule = "every weekday",
        });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());

        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(viewModel.Tasks.Single(item => item.Task.Id == "source-task"));

        Assert.True(viewModel.HasSourceTask);
        Assert.True(viewModel.HasSourceDescription);
        Assert.Equal("Source: Todoist", viewModel.SourceTaskHeader);
        Assert.Equal("Todoist title", viewModel.SourceTaskTitle);
        Assert.Equal("Work Tasks", viewModel.SourceTaskProject);
        Assert.Equal("Urgent", viewModel.SourceTaskPriority);
        Assert.Equal("every weekday", viewModel.SourceTaskRecurrence);
    }

    [Fact]
    public async Task Detail_label_chips_keep_serialized_labels_in_sync()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("labels", "Edit labels"));
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(Assert.Single(viewModel.Tasks));

        Assert.True(viewModel.AddDetailLabels("Work, Urgent, work"));
        Assert.Equal(new[] { "Work", "Urgent" }, viewModel.DetailLabelItems);
        Assert.Equal("Work, Urgent", viewModel.DetailLabels);
        Assert.True(viewModel.RemoveDetailLabel("work"));
        Assert.Equal("Urgent", viewModel.DetailLabels);

        Assert.True(await viewModel.SaveSelectedAsync());
        var task = (await store.GetTaskAsync("labels"))!;
        Assert.Equal("Urgent", Assert.Single(task.Labels).Name);
    }

    [Fact]
    public async Task Subtask_inspector_previews_five_with_progress_and_can_expand()
    {
        var store = await CreateStoreAsync();
        var parent = CreateTask("parent-preview", "Parent");
        await store.UpsertTaskAsync(parent);
        for (var index = 0; index < 6; index++)
        {
            await store.UpsertTaskAsync(CreateTask($"child-{index}", $"Child {index}") with
            {
                ParentId = parent.Id,
                Status = index == 0 ? TaskItemStatus.Completed : TaskItemStatus.Inbox,
            });
        }
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());

        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(viewModel.Tasks.Single(item => item.Task.Id == parent.Id));

        Assert.Equal("1/6", viewModel.SubtasksProgressText);
        Assert.Equal(5, viewModel.VisibleSubtasks.Count);
        Assert.True(viewModel.CanToggleSubtasks);
        Assert.Equal("Show all 6 subtasks", viewModel.SubtasksToggleText);

        viewModel.ToggleSubtasks();

        Assert.Equal(6, viewModel.VisibleSubtasks.Count);
        Assert.Equal("Show fewer", viewModel.SubtasksToggleText);
    }

    [Fact]
    public async Task Provider_date_mismatch_can_be_acknowledged_and_persisted()
    {
        var store = await CreateStoreAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "github-source-row",
            IntegrationId = IntegrationIds.GitHub,
            ProviderConnectionId = "github_default",
            ExternalId = "github-source-task",
            ProviderTaskId = "github-source-task",
            Title = "GitHub source task",
            PlannedOn = new DateOnly(2026, 8, 18),
        });
        await store.UpsertTaskAsync(CreateTask("github-linked", "Linked task") with
        {
            SourceIntegrationId = IntegrationIds.GitHub,
            SourceConnectionId = "github_default",
            SourceExternalId = "github-source-task",
            PlannedOn = new DateOnly(2026, 8, 17),
        });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(viewModel.Tasks.Single(item => item.Task.Id == "github-linked"));

        Assert.True(viewModel.HasSourceDateMismatch);
        Assert.Equal("GitHub date changed", viewModel.SourceDateMismatchTitle);
        Assert.Equal("Use GitHub date", viewModel.UseSourceDatesText);

        await viewModel.KeepOpenzaDatesAsync();

        Assert.False(viewModel.HasSourceDateMismatch);
        var saved = (await store.GetTaskAsync("github-linked"))!;
        Assert.Contains("sourceDateMismatchAcknowledgementKey", saved.LocalMetadataJson);
    }

    [Fact]
    public async Task Provider_date_acknowledgement_does_not_overwrite_incompatible_legacy_metadata()
    {
        var store = await CreateStoreAsync();
        await store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "legacy-source-row",
            IntegrationId = IntegrationIds.GitHub,
            ProviderConnectionId = "github_default",
            ExternalId = "legacy-source-task",
            ProviderTaskId = "legacy-source-task",
            Title = "Legacy source task",
            PlannedOn = new DateOnly(2026, 8, 18),
        });
        const string metadata = "{\"openza\":\"legacy-value\",\"keep\":true}";
        await store.UpsertTaskAsync(CreateTask("legacy-linked", "Legacy linked task") with
        {
            SourceIntegrationId = IntegrationIds.GitHub,
            SourceConnectionId = "github_default",
            SourceExternalId = "legacy-source-task",
            PlannedOn = new DateOnly(2026, 8, 17),
            LocalMetadataJson = metadata,
        });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(viewModel.Tasks.Single(item => item.Task.Id == "legacy-linked"));

        await viewModel.KeepOpenzaDatesAsync();

        Assert.True(viewModel.HasSourceDateMismatch);
        Assert.Contains("incompatible", viewModel.StatusMessage, StringComparison.CurrentCultureIgnoreCase);
        Assert.Equal(metadata, (await store.GetTaskAsync("legacy-linked"))!.LocalMetadataJson);
    }

    [Fact]
    public async Task Inspector_can_create_and_assign_a_project_without_leaving_the_task()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("project-task", "Assign project"));
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(Assert.Single(viewModel.Tasks));
        var selectedView = viewModel.SelectedNavigation;

        Assert.True(await viewModel.CreateProjectForSelectedTaskAsync("New project"));

        var project = Assert.Single(await store.GetProjectsAsync(), item => item.Name == "New project");
        var task = (await store.GetTaskAsync("project-task"))!;
        Assert.Equal(project.Id, task.ProjectId);
        Assert.Same(selectedView, viewModel.SelectedNavigation);
        Assert.Equal("project-task", viewModel.SelectedTask?.Task.Id);
    }

    [Fact]
    public async Task Global_search_respects_include_completed_option()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("open-search", "Needle open"));
        await store.UpsertTaskAsync(CreateTask("completed-search", "Needle completed") with
        {
            Status = TaskItemStatus.Completed,
        });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());

        var openOnly = await viewModel.SearchGloballyAsync("Needle", includeAllSpaces: true, includeCompletedTasks: false);
        var withCompleted = await viewModel.SearchGloballyAsync("Needle", includeAllSpaces: true, includeCompletedTasks: true);

        Assert.Contains(openOnly, result => result.Id == "open-search");
        Assert.DoesNotContain(openOnly, result => result.Id == "completed-search");
        Assert.Contains(withCompleted, result => result.Id == "completed-search");
    }

    [Fact]
    public async Task Task_row_actions_update_without_opening_the_inspector()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("row-actions", "Row actions"));
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        var row = Assert.Single(viewModel.Tasks);

        await viewModel.SetTaskDateFromRowAsync(row, new DateOnly(2026, 8, 20));
        await viewModel.SetTaskStatusFromRowAsync(row, TaskItemStatus.Waiting);
        await viewModel.SetTaskPriorityFromRowAsync(row, 1);

        var task = (await store.GetTaskAsync("row-actions"))!;
        Assert.Equal(new DateOnly(2026, 8, 20), task.PlannedOn);
        Assert.Equal(TaskItemStatus.Waiting, task.Status);
        Assert.Equal(1, task.Priority);
        Assert.Null(viewModel.SelectedTask);
    }

    [Fact]
    public async Task Completed_task_row_status_action_does_not_reopen_the_task()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("completed-row", "Completed row") with
        {
            CompletionState = TaskCompletionState.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Completed));
        var row = Assert.Single(viewModel.Tasks);

        await viewModel.SetTaskStatusFromRowAsync(row, TaskItemStatus.Waiting);

        var task = (await store.GetTaskAsync("completed-row"))!;
        Assert.True(task.IsCompleted);
        Assert.Equal(TaskWorkflowStatus.Inbox, task.WorkflowStatus);
    }

    [Fact]
    public async Task Group_headers_collapse_and_restore_their_task_rows()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("group-inbox", "Inbox task"));
        await store.UpsertTaskAsync(CreateTask("group-waiting", "Waiting task") with
        {
            WorkflowStatus = TaskWorkflowStatus.Waiting,
        });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore())
        {
            GroupIndex = 3,
        };
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        var header = viewModel.TaskEntries.First(entry => entry.IsHeader);
        var taskCount = viewModel.TaskEntries.Count(entry => entry.IsTask);

        viewModel.ToggleTaskGroup(header.GroupKey);

        Assert.Contains(viewModel.TaskEntries, entry => entry.IsHeader && entry.GroupKey == header.GroupKey && !entry.IsGroupExpanded);
        Assert.True(viewModel.TaskEntries.Count(entry => entry.IsTask) < taskCount);

        viewModel.ToggleTaskGroup(header.GroupKey);

        Assert.Contains(viewModel.TaskEntries, entry => entry.IsHeader && entry.GroupKey == header.GroupKey && entry.IsGroupExpanded);
        Assert.Equal(taskCount, viewModel.TaskEntries.Count(entry => entry.IsTask));
    }

    [Fact]
    public async Task Collapsed_group_state_does_not_leak_to_another_task_view()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("group-inbox-context", "Inbox task"));
        await store.UpsertTaskAsync(CreateTask("group-waiting-context", "Waiting task") with
        {
            WorkflowStatus = TaskWorkflowStatus.Waiting,
        });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore())
        {
            GroupIndex = 3,
        };
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        var waitingHeader = viewModel.TaskEntries.Single(entry => entry.IsHeader && entry.GroupTitle == "Waiting For");

        viewModel.ToggleTaskGroup(waitingHeader.GroupKey);
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Waiting));

        Assert.Contains(viewModel.TaskEntries, entry => entry.IsHeader && entry.GroupKey == waitingHeader.GroupKey && entry.IsGroupExpanded);
        Assert.Contains(viewModel.TaskEntries, entry => entry.IsTask && entry.Task?.Task.Id == "group-waiting-context");
    }

    [Fact]
    public async Task Collapsed_group_state_resets_when_space_project_or_group_mode_changes()
    {
        var store = await CreateStoreAsync();
        var otherSpace = new SpaceItem { Id = "group-other-space", Name = "Other space" };
        var firstProject = new ProjectItem
        {
            Id = "group-first-project",
            SpaceId = SpaceIds.Default,
            IntegrationId = IntegrationIds.Local,
            Name = "First project",
        };
        var secondProject = firstProject with
        {
            Id = "group-second-project",
            Name = "Second project",
        };
        await store.UpsertSpaceAsync(otherSpace);
        await store.UpsertProjectAsync(firstProject);
        await store.UpsertProjectAsync(secondProject);
        await store.UpsertTaskAsync(CreateTask("group-default-space", "Default-space task") with
        {
            WorkflowStatus = TaskWorkflowStatus.Waiting,
        });
        await store.UpsertTaskAsync(CreateTask("group-other-space-task", "Other-space task") with
        {
            SpaceId = otherSpace.Id,
            WorkflowStatus = TaskWorkflowStatus.Waiting,
        });
        await store.UpsertTaskAsync(CreateTask("group-first-project-task", "First-project task") with
        {
            ProjectId = firstProject.Id,
            WorkflowStatus = TaskWorkflowStatus.Waiting,
        });
        await store.UpsertTaskAsync(CreateTask("group-second-project-task", "Second-project task") with
        {
            ProjectId = secondProject.Id,
            WorkflowStatus = TaskWorkflowStatus.Waiting,
        });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore())
        {
            GroupIndex = 3,
        };
        var openView = viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open);
        var defaultSpace = new SpaceNavigationItemViewModel(new SpaceItem { Id = SpaceIds.Default, Name = "My space" });

        await viewModel.SelectSpaceAsync(defaultSpace);
        await viewModel.SelectNavigationAsync(openView);
        var waitingKey = viewModel.TaskEntries.Single(entry => entry.IsHeader && entry.GroupTitle == "Waiting For").GroupKey;
        viewModel.ToggleTaskGroup(waitingKey);

        await viewModel.SelectSpaceAsync(new SpaceNavigationItemViewModel(otherSpace));

        Assert.Contains(viewModel.TaskEntries, entry => entry.IsHeader && entry.GroupKey == waitingKey && entry.IsGroupExpanded);
        Assert.Contains(viewModel.TaskEntries, entry => entry.IsTask && entry.Task?.Task.Id == "group-other-space-task");

        await viewModel.SelectSpaceAsync(defaultSpace);
        await viewModel.SelectProjectAsync(new ProjectNavigationItemViewModel(firstProject, 1));
        viewModel.ToggleTaskGroup(waitingKey);

        await viewModel.SelectProjectAsync(new ProjectNavigationItemViewModel(secondProject, 1));

        Assert.Contains(viewModel.TaskEntries, entry => entry.IsHeader && entry.GroupKey == waitingKey && entry.IsGroupExpanded);
        Assert.Contains(viewModel.TaskEntries, entry => entry.IsTask && entry.Task?.Task.Id == "group-second-project-task");

        viewModel.ToggleTaskGroup(waitingKey);
        viewModel.GroupIndex = 4;
        await viewModel.ApplyListOptionsAsync();
        viewModel.GroupIndex = 3;
        await viewModel.ApplyListOptionsAsync();

        Assert.Contains(viewModel.TaskEntries, entry => entry.IsHeader && entry.GroupKey == waitingKey && entry.IsGroupExpanded);
        Assert.Contains(viewModel.TaskEntries, entry => entry.IsTask && entry.Task?.Task.Id == "group-second-project-task");
    }

    [Fact]
    public async Task Task_row_more_actions_change_project_labels_and_space_in_place()
    {
        var store = await CreateStoreAsync();
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "row-project",
            SpaceId = SpaceIds.Default,
            IntegrationId = IntegrationIds.Local,
            Name = "Row project",
        });
        await store.UpsertSpaceAsync(new SpaceItem { Id = "other-space", Name = "Other space" });
        await store.UpsertTaskAsync(CreateTask("row-more", "More actions"));
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        var row = Assert.Single(viewModel.Tasks);

        await viewModel.SetTaskProjectFromRowAsync(row, "row-project");
        await viewModel.SetTaskLabelsFromRowAsync(row, "Work, Urgent");
        await viewModel.MoveTaskFromRowAsync(row, "other-space");

        var task = (await store.GetTaskAsync("row-more"))!;
        Assert.Equal("other-space", task.SpaceId);
        Assert.Null(task.ProjectId);
        Assert.Equal(new[] { "Urgent", "Work" }, task.Labels.Select(label => label.Name).Order());
        Assert.Null(viewModel.SelectedTask);
    }

    [Fact]
    public async Task Row_project_choices_are_limited_to_the_task_space()
    {
        var store = await CreateStoreAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "other-space", Name = "Other space" });
        await store.UpsertProjectAsync(new ProjectItem { Id = "default-project", SpaceId = SpaceIds.Default, Name = "Default project" });
        await store.UpsertProjectAsync(new ProjectItem { Id = "other-project", SpaceId = "other-space", Name = "Other project" });
        await store.UpsertTaskAsync(CreateTask("project-options", "Project options"));
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        var row = Assert.Single(viewModel.Tasks);

        var options = viewModel.GetProjectOptionsForSpace(row.Task.SpaceId);

        Assert.Contains(options, option => option.ProjectId is null);
        Assert.Contains(options, option => option.ProjectId == "default-project");
        Assert.DoesNotContain(options, option => option.ProjectId == "other-project");
    }

    [Fact]
    public async Task Task_list_shows_only_top_level_tasks_and_keeps_subtasks_in_details()
    {
        var store = await CreateStoreAsync();
        var parent = CreateTask("parent", "Parent task");
        await store.UpsertTaskAsync(parent);
        await store.UpsertTaskAsync(CreateTask("child", "Child task") with { ParentId = parent.Id });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());

        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        var visibleParent = Assert.Single(viewModel.Tasks);
        await viewModel.SelectTaskAsync(visibleParent);

        Assert.Equal("parent", visibleParent.Task.Id);
        Assert.Equal("child", Assert.Single(viewModel.Subtasks).Task.Id);
    }

    [Fact]
    public async Task Saving_changed_Todoist_date_queues_one_write_back()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("adopted", "Adopted task") with
        {
            SourceIntegrationId = IntegrationIds.Todoist,
            SourceProviderTaskId = "todoist-task",
            PlannedOn = new DateOnly(2026, 8, 14),
        });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(Assert.Single(viewModel.Tasks));
        viewModel.DetailCalendarDate = new DateTime(2026, 8, 15);

        Assert.Equal(new DateTime(2026, 8, 15), viewModel.DetailCalendarDate);
        Assert.Equal(new DateOnly(2026, 8, 15), DateOnly.FromDateTime(viewModel.DetailDate!.Value.LocalDateTime));

        Assert.True(await viewModel.SaveSelectedAsync());

        var update = Assert.Single(await store.GetPendingTaskDateUpdatesAsync(IntegrationIds.Todoist));
        Assert.Equal("todoist-task", update.ProviderTaskId);
        Assert.Equal(new DateOnly(2026, 8, 15), update.PlannedOn);
    }

    [Fact]
    public async Task Saving_completed_status_completes_and_queues_Todoist_write_back()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("adopted-completion", "Complete in editor") with
        {
            SourceIntegrationId = IntegrationIds.Todoist,
            SourceExternalId = "todoist-completion",
            SourceProviderTaskId = "todoist-completion",
        });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(Assert.Single(viewModel.Tasks));
        viewModel.DetailStatusIndex = 4;

        Assert.True(await viewModel.SaveSelectedAsync());

        var completed = (await store.GetTaskAsync("adopted-completion"))!;
        Assert.True(completed.IsCompleted);
        Assert.Equal(TaskWorkflowStatus.Inbox, completed.WorkflowStatus);
        var pending = Assert.Single(await store.GetPendingCompletionsAsync(IntegrationIds.Todoist));
        Assert.True(pending.Completed);
        Assert.Equal("todoist-completion", pending.ProviderTaskId);
    }

    [Fact]
    public async Task Saving_unchanged_details_does_not_rewrite_the_task()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("unchanged", "Leave unchanged") with
        {
            PlannedOn = new DateOnly(2026, 8, 15),
            Notes = "Existing notes",
        });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(Assert.Single(viewModel.Tasks));
        var before = (await store.GetTaskAsync("unchanged"))!;

        Assert.True(await viewModel.SaveSelectedAsync());

        var after = (await store.GetTaskAsync("unchanged"))!;
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Theory]
    [InlineData("{ \"foo\": 1, \"openza\": { \"keep\": true } }")]
    [InlineData("42")]
    [InlineData("not-json")]
    public async Task Saving_unchanged_details_preserves_metadata_verbatim(string metadata)
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("metadata", "Preserve metadata") with { LocalMetadataJson = metadata });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(Assert.Single(viewModel.Tasks));
        var before = (await store.GetTaskAsync("metadata"))!;

        Assert.True(await viewModel.SaveSelectedAsync());

        var after = (await store.GetTaskAsync("metadata"))!;
        Assert.Equal(metadata, after.LocalMetadataJson);
        Assert.Equal(before.Revision, after.Revision);
    }

    [Fact]
    public async Task Queued_detail_saves_preserve_the_newest_edit()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("queued-save", "Original"));
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(Assert.Single(viewModel.Tasks));
        var gate = (SemaphoreSlim)typeof(MainWindowViewModel)
            .GetField("_operationGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
        await gate.WaitAsync();

        viewModel.DetailTitle = "First edit";
        var firstSave = viewModel.SaveSelectedAsync();
        viewModel.DetailTitle = "Newest edit";
        var newestSave = viewModel.SaveSelectedAsync();
        gate.Release();

        Assert.True(await firstSave);
        Assert.True(await newestSave);
        Assert.Equal("Newest edit", (await store.GetTaskAsync("queued-save"))!.Title);
        Assert.Equal("Newest edit", viewModel.DetailTitle);
    }

    [Fact]
    public async Task In_flight_save_does_not_refresh_over_a_new_edit_before_lost_focus()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("in-flight-save", "Original"));
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(Assert.Single(viewModel.Tasks));
        var gate = (SemaphoreSlim)typeof(MainWindowViewModel)
            .GetField("_operationGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
        await gate.WaitAsync();

        viewModel.DetailTitle = "First edit";
        var firstSave = viewModel.SaveSelectedAsync();
        viewModel.DetailTitle = "Typed while saving";
        gate.Release();

        Assert.True(await firstSave);
        Assert.Equal("Typed while saving", viewModel.DetailTitle);
        Assert.Equal("Typed while saving", (await store.GetTaskAsync("in-flight-save"))!.Title);
    }

    [Fact]
    public async Task Refresh_marks_programmatic_detail_changes_as_suppressed()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(CreateTask("task", "Refresh task"));
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        var detailEventsWereSuppressed = true;
        var detailEventCount = 0;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(MainWindowViewModel.DetailProject) or
                nameof(MainWindowViewModel.DetailStatusIndex) or
                nameof(MainWindowViewModel.DetailDate))
            {
                detailEventCount++;
                detailEventsWereSuppressed &= viewModel.IsUpdatingDetails;
            }
        };

        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await viewModel.SelectTaskAsync(Assert.Single(viewModel.Tasks));

        Assert.True(detailEventCount > 0);
        Assert.True(detailEventsWereSuppressed);
        Assert.False(viewModel.IsUpdatingDetails);
    }

    private async Task<SqliteTaskStore> CreateStoreAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        return store;
    }

    private static TaskItem CreateTask(string id, string title) => new()
    {
        Id = id,
        SpaceId = SpaceIds.Default,
        IntegrationId = IntegrationIds.Local,
        Title = title,
        Status = TaskItemStatus.Inbox,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    public void Dispose() => TestDirectory.Delete(_directory);
}
