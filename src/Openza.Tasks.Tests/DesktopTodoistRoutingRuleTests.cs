using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed class DesktopTodoistRoutingRuleTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "openza-tasks-desktop-routing-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Saving_label_rule_populates_settings_and_routes_matching_tasks()
    {
        var store = await CreateStoreAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_work", Name = "Work" });
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "todoist_project_processed",
            ExternalId = "processed",
            IntegrationId = IntegrationIds.Todoist,
            Name = "Processed",
        });
        await store.UpsertLabelAsync(new LabelItem
        {
            Id = "todoist_label_work",
            ExternalId = "work",
            IntegrationId = IntegrationIds.Todoist,
            Name = "work",
        });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());

        await viewModel.RefreshTodoistRoutingRulesAsync();
        await viewModel.SaveTodoistRoutingRuleAsync(new TodoistRoutingRuleDraft(
            null,
            "@work",
            "space_work",
            "processed"));

        var route = Assert.Single(await store.GetSyncRoutesAsync());
        var settings = TodoistRoutingRuleCodec.Read(route.SettingsJson);
        var savedRule = Assert.Single(settings.LabelRoutes);
        var displayedRule = Assert.Single(viewModel.TodoistRoutingRules);
        var routeMatch = ProviderSourceRoutingPolicy
            .FromRoutes([route], "todoist_default", IntegrationIds.Todoist)
            .Match(new TaskItem
            {
                Labels = [new LabelItem { Name = "WORK" }],
            });

        Assert.True(route.IsEnabled);
        Assert.Equal("work", savedRule.Label);
        Assert.Equal("space_work", routeMatch.SpaceId);
        Assert.Equal("processed", routeMatch.PostImportAction?.MoveToProjectId);
        Assert.Equal("@work", displayedRule.LabelText);
        Assert.Equal("Send to Work, then move in Todoist to Processed", displayedRule.SummaryText);
        Assert.Contains(viewModel.TodoistRoutingLabelChoices, choice => choice.Id == "work");
        Assert.Contains(viewModel.TodoistRoutingProjectChoices, choice => choice.Id == "processed");
    }

    [Fact]
    public async Task Unlabeled_rule_can_be_edited_then_deleted_without_leaving_route_enabled()
    {
        var store = await CreateStoreAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_personal", Name = "Personal" });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());

        await viewModel.SaveTodoistRoutingRuleAsync(new TodoistRoutingRuleDraft(
            null,
            string.Empty,
            "space_personal",
            null,
            MatchNoLabels: true));

        var displayedRule = Assert.Single(viewModel.TodoistRoutingRules);
        Assert.Equal("No Todoist labels", displayedRule.LabelText);
        Assert.False(viewModel.HasNoTodoistRoutingRules);

        await viewModel.DeleteTodoistRoutingRuleAsync(displayedRule.Id);

        var route = Assert.Single(await store.GetSyncRoutesAsync());
        var settings = TodoistRoutingRuleCodec.Read(route.SettingsJson);
        Assert.False(route.IsEnabled);
        Assert.Empty(settings.LabelRoutes);
        Assert.Null(settings.UnlabeledRoute);
        Assert.Empty(viewModel.TodoistRoutingRules);
        Assert.True(viewModel.HasNoTodoistRoutingRules);
    }

    [Fact]
    public async Task Adopting_matching_Todoist_task_applies_post_import_filing_immediately()
    {
        var store = await CreateStoreAsync();
        var credentials = new InMemoryCredentialStore();
        await credentials.SaveAsync("todoist-token", "test-token");
        var provider = new RecordingMoveProvider();
        var viewModel = new MainWindowViewModel(store, credentials, (_, _) => provider);
        await viewModel.SaveTodoistRoutingRuleAsync(new TodoistRoutingRuleDraft(
            null,
            string.Empty,
            SpaceIds.Default,
            "processed",
            MatchNoLabels: true));
        await store.UpsertProviderSourceItemAsync(CreateTodoistSource());
        await viewModel.LoadConnectedTasksAsync();

        await viewModel.AdoptConnectedTaskAsync(Assert.Single(viewModel.FilteredConnectedTasks));

        Assert.Equal(("todoist-task", "processed"), Assert.Single(provider.Moves));
        Assert.Contains("filed in Todoist", viewModel.StatusMessage);
        Assert.False(viewModel.IsStatusMessagePersistent);
    }

    [Fact]
    public async Task Todoist_post_import_filing_failure_remains_visible_until_dismissed()
    {
        var store = await CreateStoreAsync();
        var credentials = new InMemoryCredentialStore();
        await credentials.SaveAsync("todoist-token", "test-token");
        var viewModel = new MainWindowViewModel(store, credentials, (_, _) => new FailingMoveProvider());
        await viewModel.SaveTodoistRoutingRuleAsync(new TodoistRoutingRuleDraft(
            null,
            string.Empty,
            SpaceIds.Default,
            "processed",
            MatchNoLabels: true));
        await store.UpsertProviderSourceItemAsync(CreateTodoistSource());
        await viewModel.LoadConnectedTasksAsync();

        await viewModel.AdoptConnectedTaskAsync(Assert.Single(viewModel.FilteredConnectedTasks));

        Assert.Contains("Todoist filing failed", viewModel.StatusMessage);
        Assert.True(viewModel.IsStatusMessagePersistent);

        viewModel.DismissStatusMessage();

        Assert.Empty(viewModel.StatusMessage);
        Assert.False(viewModel.IsStatusMessagePersistent);
    }

    [Fact]
    public async Task Connected_task_intake_exposes_waiting_count_and_summary_without_review_click()
    {
        var store = await CreateStoreAsync();
        await store.UpsertProviderSourceItemAsync(CreateTodoistSource());
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());

        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Inbox));
        await viewModel.LoadConnectedTasksAsync();

        Assert.True(viewModel.HasConnectedTasks);
        Assert.Equal(1, viewModel.ConnectedTaskCount);
        Assert.Equal(1, viewModel.WaitingConnectedTaskCount);
        Assert.Equal(0, viewModel.SkippedConnectedTaskCount);
        Assert.Equal("1 task is waiting. Add it to Inbox first, then clarify it like any other task.", viewModel.ConnectedTasksSummary);
        Assert.Equal("Clarify captured tasks here, or review tasks waiting from connected apps.", viewModel.EmptyStateMessage);
        Assert.Single(viewModel.FilteredConnectedTasks);
    }

    [Fact]
    public async Task Connected_task_intake_keeps_skipped_items_recoverable_when_none_are_waiting()
    {
        var store = await CreateStoreAsync();
        await store.UpsertProviderSourceItemAsync(CreateTodoistSource());
        Assert.True(await store.SkipProviderSourceItemAsync("source-todoist-task"));
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.SelectNavigationAsync(viewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Inbox));

        await viewModel.LoadConnectedTasksAsync();

        var skipped = Assert.Single(viewModel.FilteredConnectedTasks);
        Assert.Equal(0, viewModel.WaitingConnectedTaskCount);
        Assert.Equal(1, viewModel.SkippedConnectedTaskCount);
        Assert.False(viewModel.CanAddAllConnectedTasks);
        Assert.Equal("Unskip", skipped.PrimaryActionText);
        Assert.Equal("Skipped", skipped.SecondaryActionText);
        Assert.False(skipped.CanSkip);
    }

    [Fact]
    public async Task Add_all_connected_tasks_ignores_display_filters_and_refreshes_the_intake()
    {
        var store = await CreateStoreAsync();
        await store.UpsertProviderSourceItemAsync(CreateTodoistSource() with { SourceProjectName = "Inbox" });
        await store.UpsertProviderSourceItemAsync(CreateTodoistSource() with
        {
            Id = "source-second",
            ExternalId = "todoist-second",
            ProviderTaskId = "todoist-second",
            Title = "Second Todoist task",
            SourceProjectId = "work",
            SourceProjectName = "Work",
        });
        var viewModel = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await viewModel.LoadConnectedTasksAsync();
        viewModel.ConnectedProjectFilterIndex = viewModel.ConnectedProjectOptions.IndexOf("Work");
        viewModel.ApplyConnectedFilters();
        Assert.Single(viewModel.FilteredConnectedTasks);

        await viewModel.AdoptAllConnectedTasksAsync();

        Assert.Equal(2, (await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All })).Count);
        Assert.Equal(0, viewModel.WaitingConnectedTaskCount);
        Assert.Empty(viewModel.FilteredConnectedTasks);
    }

    private static ProviderSourceItem CreateTodoistSource() => new()
    {
        Id = "source-todoist-task",
        IntegrationId = IntegrationIds.Todoist,
        ProviderConnectionId = "todoist_default",
        ExternalId = "todoist-task",
        ProviderTaskId = "todoist-task",
        Title = "Todoist task",
        SourceProjectId = "inbox",
        SuggestedSpaceId = SpaceIds.Default,
        SnapshotJson = "{}",
    };

    private sealed class RecordingMoveProvider : ITaskProjectMoveProvider
    {
        public List<(string TaskId, string ProjectId)> Moves { get; } = [];
        public string IntegrationId => IntegrationIds.Todoist;
        public string ProviderConnectionId => "todoist_default";

        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderSnapshot([], [], []));

        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task MoveTaskAsync(string taskId, string projectId, CancellationToken cancellationToken = default)
        {
            Moves.Add((taskId, projectId));
            return Task.CompletedTask;
        }
    }

    private sealed class FailingMoveProvider : ITaskProjectMoveProvider
    {
        public string IntegrationId => IntegrationIds.Todoist;
        public string ProviderConnectionId => "todoist_default";

        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderSnapshot([], [], []));

        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task MoveTaskAsync(string taskId, string projectId, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Todoist rejected the move"));
    }

    private async Task<SqliteTaskStore> CreateStoreAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        return store;
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
