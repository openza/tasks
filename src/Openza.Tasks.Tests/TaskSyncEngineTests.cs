using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class TaskSyncEngineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-tasks-sync-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Sync_records_provider_source_items_and_pushes_pending_completions()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.QueueCompletionAsync(new PendingCompletion
        {
            Id = "completion_1",
            TaskId = "local",
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote_1",
            Completed = true,
        });
        var provider = new FakeProvider();
        var engine = new TaskSyncEngine(store);

        var result = await engine.SyncAsync(provider);

        Assert.True(result.Success);
        Assert.Equal(1, result.TasksAdded);
        Assert.Equal(1, result.CompletionsSynced);
        Assert.Equal(1, provider.CompletedCalls);
        Assert.Empty(await store.GetPendingCompletionsAsync(IntegrationIds.Todoist));
        Assert.Empty(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
        Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
    }

    [Fact]
    public async Task Sync_pushes_pending_todoist_date_updates_before_fetching_snapshot()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.QueueTaskDateUpdateAsync(new PendingTaskDateUpdate
        {
            Id = "date_1",
            TaskId = "local",
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote_1",
            PlannedOn = new DateOnly(2026, 6, 2),
        });
        var provider = new FakeProvider();
        var engine = new TaskSyncEngine(store);

        var result = await engine.SyncAsync(provider);

        Assert.True(result.Success);
        Assert.Equal(1, result.DateUpdatesSynced);
        var update = Assert.Single(provider.DateUpdates);
        Assert.Equal("remote_1", update.ProviderTaskId);
        Assert.Equal(new DateOnly(2026, 6, 2), update.PlannedOn);
        Assert.Empty(await store.GetPendingTaskDateUpdatesAsync(IntegrationIds.Todoist));
    }

    [Fact]
    public async Task Sync_pushes_pending_date_updates_before_completions_for_same_provider_task()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.QueueTaskDateUpdateAsync(new PendingTaskDateUpdate
        {
            Id = "date_1",
            TaskId = "local",
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote_1",
            PlannedOn = new DateOnly(2026, 6, 2),
        });
        await store.QueueCompletionAsync(new PendingCompletion
        {
            Id = "completion_1",
            TaskId = "local",
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote_1",
            Completed = true,
        });
        var provider = new FakeProvider();
        var engine = new TaskSyncEngine(store);

        var result = await engine.SyncAsync(provider);

        Assert.True(result.Success);
        Assert.Equal(1, result.DateUpdatesSynced);
        Assert.Equal(1, result.CompletionsSynced);
        Assert.Equal(["date", "completion"], provider.OutboundOperationOrder);
        Assert.Empty(await store.GetPendingTaskDateUpdatesAsync(IntegrationIds.Todoist));
        Assert.Empty(await store.GetPendingCompletionsAsync(IntegrationIds.Todoist));
    }

    [Fact]
    public async Task Sync_uses_modern_todoist_urls_without_promoting_source_labels()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider
        {
            Title = "Repair Ovi cycle",
            Labels =
            [
                new LabelItem
                {
                    Id = "todoist_label_home",
                    IntegrationId = IntegrationIds.Todoist,
                    Name = "home",
                },
            ],
        };
        var engine = new TaskSyncEngine(store);

        await engine.SyncAsync(provider);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
        var adopted = await store.AdoptProviderSourceItemAsync(source.Id);

        Assert.Equal("https://app.todoist.com/app/task/repair-ovi-cycle-remote_1", source.SourceUrl);
        Assert.NotNull(adopted);
        var task = await store.GetTaskAsync(adopted.Id);
        Assert.NotNull(task);
        Assert.Empty(task.Labels);
    }

    [Fact]
    public async Task Sync_routes_todoist_source_items_by_configured_labels()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_work", Name = "Work" });
        await store.UpsertSyncRouteAsync(new SyncRouteInfo
        {
            Id = "route_todoist_work",
            Name = "Todoist Work",
            SourceConnectionId = "todoist_default",
            IsEnabled = true,
            SettingsJson = """
                {
                  "labelRoutes": [
                    { "labels": ["work"], "spaceId": "space_work" }
                  ]
                }
                """,
        });
        var provider = new FakeProvider
        {
            Labels =
            [
                new LabelItem
                {
                    Id = "todoist_label_work",
                    IntegrationId = IntegrationIds.Todoist,
                    Name = "work",
                },
            ],
        };
        var engine = new TaskSyncEngine(store);

        await engine.SyncAsync(provider);

        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, spaceId: "space_work"));
        Assert.Equal("space_work", source.SuggestedSpaceId);
    }

    [Fact]
    public async Task Sync_routes_todoist_source_items_from_settings_rule_shape()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_personal", Name = "Personal" });
        await store.UpsertSyncRouteAsync(new SyncRouteInfo
        {
            Id = "route_todoist_label_routing",
            Name = "Todoist label rules",
            SourceConnectionId = "todoist_default",
            IsEnabled = true,
            SettingsJson = """
                {
                  "labelRoutes": [
                    {
                      "id": "todoist_rule_home",
                      "label": "home",
                      "labels": ["home"],
                      "spaceId": "space_personal",
                      "postImport": { "moveToProjectId": "processed_project" }
                    }
                  ]
                }
                """,
        });
        var provider = new FakeProvider
        {
            Labels =
            [
                new LabelItem
                {
                    Id = "todoist_label_home",
                    IntegrationId = IntegrationIds.Todoist,
                    Name = "home",
                },
            ],
        };
        var engine = new TaskSyncEngine(store);

        await engine.SyncAsync(provider);

        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, spaceId: "space_personal"));
        Assert.Equal("space_personal", source.SuggestedSpaceId);
    }

    [Fact]
    public async Task Sync_routes_unlabeled_todoist_source_items_from_settings_rule_shape()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_personal", Name = "Personal" });
        await store.UpsertSyncRouteAsync(new SyncRouteInfo
        {
            Id = "route_todoist_label_routing",
            Name = "Todoist label rules",
            SourceConnectionId = "todoist_default",
            IsEnabled = true,
            SettingsJson = """
                {
                  "unlabeledRoute": {
                    "id": "todoist_rule_no_labels",
                    "spaceId": "space_personal",
                    "postImport": { "moveToProjectId": "personal_project" }
                  }
                }
                """,
        });
        var provider = new FakeProvider();
        var engine = new TaskSyncEngine(store);

        await engine.SyncAsync(provider);

        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, spaceId: "space_personal"));
        var routes = await store.GetSyncRoutesAsync();
        var action = ProviderSourceRoutingPolicy
            .FromRoutes(routes, "todoist_default", IntegrationIds.Todoist)
            .Match(source)
            .PostImportAction;
        Assert.Equal("space_personal", source.SuggestedSpaceId);
        Assert.NotNull(action);
        Assert.Equal("personal_project", action.MoveToProjectId);
    }

    [Fact]
    public async Task Sync_applies_todoist_post_import_filing_for_recurring_tasks()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_personal", Name = "Personal" });
        await store.UpsertSyncRouteAsync(new SyncRouteInfo
        {
            Id = "route_todoist_label_routing",
            Name = "Todoist label rules",
            SourceConnectionId = "todoist_default",
            IsEnabled = true,
            SettingsJson = """
                {
                  "unlabeledRoute": {
                    "id": "todoist_rule_no_labels",
                    "spaceId": "space_personal",
                    "postImport": { "moveToProjectId": "personal_project" }
                  }
                }
                """,
        });
        var provider = new FakeProvider
        {
            PlannedOn = new DateOnly(2026, 7, 9),
            RecurrenceRule = "every day",
        };
        var engine = new TaskSyncEngine(store);

        var result = await engine.SyncAsync(provider);

        var task = Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
        var move = Assert.Single(provider.ProjectMoves);
        Assert.True(result.Success);
        Assert.Equal(TaskItemStatus.Someday, task.Status);
        Assert.Equal(("remote_1", "personal_project"), move);
    }

    [Fact]
    public async Task Sync_does_not_apply_todoist_post_import_filing_for_normal_waiting_source_items()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSyncRouteAsync(new SyncRouteInfo
        {
            Id = "route_todoist_label_routing",
            Name = "Todoist label rules",
            SourceConnectionId = "todoist_default",
            IsEnabled = true,
            SettingsJson = """
                {
                  "unlabeledRoute": {
                    "id": "todoist_rule_no_labels",
                    "postImport": { "moveToProjectId": "personal_project" }
                  }
                }
                """,
        });
        var provider = new FakeProvider();
        var engine = new TaskSyncEngine(store);

        var result = await engine.SyncAsync(provider);

        Assert.True(result.Success);
        Assert.Empty(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
        Assert.Empty(provider.ProjectMoves);
    }

    [Fact]
    public async Task Sync_skips_todoist_post_import_filing_when_recurring_task_is_already_in_target_project()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSyncRouteAsync(new SyncRouteInfo
        {
            Id = "route_todoist_label_routing",
            Name = "Todoist label rules",
            SourceConnectionId = "todoist_default",
            IsEnabled = true,
            SettingsJson = """
                {
                  "unlabeledRoute": {
                    "id": "todoist_rule_no_labels",
                    "postImport": { "moveToProjectId": "personal_project" }
                  }
                }
                """,
        });
        var provider = new FakeProvider
        {
            PlannedOn = new DateOnly(2026, 7, 9),
            ProjectId = "todoist_personal_project",
            RecurrenceRule = "every day",
        };
        var engine = new TaskSyncEngine(store);

        var result = await engine.SyncAsync(provider);

        Assert.True(result.Success);
        Assert.Empty(provider.ProjectMoves);
    }

    [Fact]
    public async Task Sync_does_not_repeat_todoist_post_import_filing_for_existing_recurring_tasks()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSyncRouteAsync(new SyncRouteInfo
        {
            Id = "route_todoist_label_routing",
            Name = "Todoist label rules",
            SourceConnectionId = "todoist_default",
            IsEnabled = true,
            SettingsJson = """
                {
                  "unlabeledRoute": {
                    "id": "todoist_rule_no_labels",
                    "postImport": { "moveToProjectId": "personal_project" }
                  }
                }
                """,
        });
        var provider = new FakeProvider
        {
            PlannedOn = new DateOnly(2026, 7, 9),
            RecurrenceRule = "every day",
        };
        var engine = new TaskSyncEngine(store);

        var firstSync = await engine.SyncAsync(provider);
        provider.ProjectId = "todoist_other_project";
        var secondSync = await engine.SyncAsync(provider);

        var move = Assert.Single(provider.ProjectMoves);
        Assert.True(firstSync.Success);
        Assert.True(secondSync.Success);
        Assert.Equal(("remote_1", "personal_project"), move);
        Assert.Equal("todoist_other_project", provider.ProjectId);
    }

    [Fact]
    public async Task Sync_retries_todoist_post_import_filing_after_transient_move_failure()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSyncRouteAsync(new SyncRouteInfo
        {
            Id = "route_todoist_label_routing",
            Name = "Todoist label rules",
            SourceConnectionId = "todoist_default",
            IsEnabled = true,
            SettingsJson = """
                {
                  "unlabeledRoute": {
                    "id": "todoist_rule_no_labels",
                    "postImport": { "moveToProjectId": "personal_project" }
                  }
                }
                """,
        });
        var provider = new FakeProvider
        {
            PlannedOn = new DateOnly(2026, 7, 9),
            RecurrenceRule = "every day",
            ThrowOnMove = true,
        };
        var engine = new TaskSyncEngine(store);

        var failedSync = await engine.SyncAsync(provider);
        provider.ThrowOnMove = false;
        var retrySync = await engine.SyncAsync(provider);

        var task = Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
        var move = Assert.Single(provider.ProjectMoves);
        Assert.False(failedSync.Success);
        Assert.True(retrySync.Success);
        Assert.NotNull(task);
        Assert.Equal(("remote_1", "personal_project"), move);
    }

    [Fact]
    public async Task Sync_prefers_todoist_label_route_over_unlabeled_route()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_work", Name = "Work" });
        await store.UpsertSpaceAsync(new SpaceItem { Id = "space_personal", Name = "Personal" });
        await store.UpsertSyncRouteAsync(new SyncRouteInfo
        {
            Id = "route_todoist_label_routing",
            Name = "Todoist label rules",
            SourceConnectionId = "todoist_default",
            IsEnabled = true,
            SettingsJson = """
                {
                  "labelRoutes": [
                    { "labels": ["work"], "spaceId": "space_work" }
                  ],
                  "unlabeledRoute": {
                    "id": "todoist_rule_no_labels",
                    "spaceId": "space_personal"
                  }
                }
                """,
        });
        var provider = new FakeProvider
        {
            Labels =
            [
                new LabelItem
                {
                    Id = "todoist_label_work",
                    IntegrationId = IntegrationIds.Todoist,
                    Name = "work",
                },
            ],
        };
        var engine = new TaskSyncEngine(store);

        await engine.SyncAsync(provider);

        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, spaceId: "space_work"));
        Assert.Equal("space_work", source.SuggestedSpaceId);
    }

    [Fact]
    public async Task Sync_stores_provider_projects_and_labels_for_rule_pickers()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider
        {
            Projects =
            [
                new ProjectItem
                {
                    Id = "todoist_project_inbox",
                    ExternalId = "project_inbox",
                    IntegrationId = IntegrationIds.Todoist,
                    Name = "Inbox",
                },
            ],
            SnapshotLabels =
            [
                new LabelItem
                {
                    Id = "todoist_label_home",
                    ExternalId = "home",
                    IntegrationId = IntegrationIds.Todoist,
                    Name = "home",
                },
            ],
        };
        var engine = new TaskSyncEngine(store);

        var result = await engine.SyncAsync(provider);

        var project = Assert.Single(await store.GetProviderProjectsAsync(IntegrationIds.Todoist));
        var label = Assert.Single(await store.GetProviderLabelsAsync(IntegrationIds.Todoist));
        Assert.Equal(1, result.ProjectsSynced);
        Assert.Equal(1, result.LabelsSynced);
        Assert.Equal("todoist_default", project.ProviderConnectionId);
        Assert.Equal("todoist_default", label.ProviderConnectionId);
        Assert.DoesNotContain(await store.GetProjectsAsync(), project => project.IntegrationId == IntegrationIds.Todoist);
        Assert.DoesNotContain(await store.GetLabelsAsync(), label => label.IntegrationId == IntegrationIds.Todoist);
    }

    [Fact]
    public async Task Sync_preserves_provider_tasks_missing_from_snapshot_by_default()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "todoist_orphan",
            ExternalId = "orphan",
            IntegrationId = IntegrationIds.Todoist,
            Title = "Local provider task",
        });
        var engine = new TaskSyncEngine(store);

        var result = await engine.SyncAsync(new EmptyProvider());

        Assert.True(result.Success);
        Assert.Equal(0, result.TasksDeleted);
        Assert.NotNull(await store.GetTaskAsync("todoist_orphan"));
        Assert.Empty(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
    }

    [Fact]
    public async Task Sync_stores_provider_parent_reference_for_subtasks()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider
        {
            IncludeChildTask = true,
        };
        var engine = new TaskSyncEngine(store);

        var result = await engine.SyncAsync(provider);

        var sources = await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true);
        var childSource = Assert.Single(sources, source => source.ExternalId == "remote_child");
        Assert.True(result.Success);
        Assert.Equal("remote_1", childSource.ParentExternalId);
    }

    [Fact]
    public async Task Sync_refreshes_adopted_wrapper_provider_fields_without_overwriting_openza_fields()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "proj_local",
            IntegrationId = IntegrationIds.Local,
            Name = "Local project",
        });
        var provider = new FakeProvider { Title = "Remote task", Description = "Remote description" };
        var engine = new TaskSyncEngine(store);

        var firstSync = await engine.SyncAsync(provider);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
        var adopted = await store.AdoptProviderSourceItemAsync(source.Id);
        Assert.True(firstSync.Success);
        Assert.NotNull(adopted);

        var localCreatedAt = adopted.CreatedAt;
        await store.UpsertTaskAsync(adopted with
        {
            Title = "Old title",
            ProjectId = "proj_local",
            Priority = 1,
            Status = TaskItemStatus.Waiting,
            Notes = "Local note",
            PlannedOn = new DateOnly(2026, 5, 13),
            DeadlineOn = new DateOnly(2026, 5, 14),
            ScheduledStart = new DateTimeOffset(2026, 5, 13, 9, 0, 0, 0, TimeSpan.Zero),
            ScheduledEnd = new DateTimeOffset(2026, 5, 13, 10, 0, 0, 0, TimeSpan.Zero),
            DurationMinutes = 60,
            RecurrenceRule = "FREQ=WEEKLY;BYDAY=WE",
            LocalMetadataJson = """{"focus":"deep"}""",
        });

        provider.Title = "Remote task updated";
        provider.Description = "Remote description updated";
        provider.PlannedOn = new DateOnly(2026, 5, 15);
        var result = await engine.SyncAsync(provider);

        var task = await store.GetTaskAsync(adopted.Id);
        Assert.True(result.Success);
        Assert.Equal(1, result.TasksUpdated);
        Assert.NotNull(task);
        Assert.Equal("Old title", task.Title);
        Assert.Equal("Remote task updated", task.SourceTitle);
        Assert.Null(task.Description);
        Assert.Equal("Remote description updated", task.SourceDescription);
        Assert.Equal("proj_local", task.ProjectId);
        Assert.Equal(1, task.Priority);
        Assert.Equal(TaskItemStatus.Waiting, task.Status);
        Assert.Equal("Local note", task.Notes);
        Assert.Equal(new DateOnly(2026, 5, 15), task.PlannedOn);
        Assert.Equal(new DateOnly(2026, 5, 14), task.DeadlineOn);
        Assert.Equal(new DateTimeOffset(2026, 5, 13, 9, 0, 0, 0, TimeSpan.Zero), task.ScheduledStart);
        Assert.Equal(new DateTimeOffset(2026, 5, 13, 10, 0, 0, 0, TimeSpan.Zero), task.ScheduledEnd);
        Assert.Equal(60, task.DurationMinutes);
        Assert.Equal("FREQ=WEEKLY;BYDAY=WE", task.RecurrenceRule);
        Assert.Equal("""{"focus":"deep"}""", task.LocalMetadataJson);
        Assert.Equal(localCreatedAt.ToUnixTimeSeconds(), task.CreatedAt.ToUnixTimeSeconds());
        Assert.Equal(IntegrationIds.Local, task.IntegrationId);
        Assert.Equal(IntegrationIds.Todoist, task.SourceIntegrationId);
        Assert.Equal("remote_1", task.SourceExternalId);
    }

    [Fact]
    public async Task Sync_detaches_adopted_source_item_missing_from_provider_snapshot()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var engine = new TaskSyncEngine(store);

        var firstSync = await engine.SyncAsync(new FakeProvider());
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
        var adopted = await store.AdoptProviderSourceItemAsync(source.Id);
        Assert.True(firstSync.Success);
        Assert.NotNull(adopted);

        var secondSync = await engine.SyncAsync(new EmptyProvider());

        var detached = await store.GetTaskAsync(adopted.Id);
        Assert.True(secondSync.Success);
        Assert.NotNull(detached);
        Assert.False(detached.HasProviderSource);
        Assert.Empty(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true, includeIgnored: true));

        await store.DeleteTaskAsync(adopted.Id);
        Assert.Null(await store.GetTaskAsync(adopted.Id));
    }

    [Fact]
    public async Task Sync_relinks_adopted_source_item_when_provider_task_returns()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var engine = new TaskSyncEngine(store);

        var firstSync = await engine.SyncAsync(new FakeProvider());
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
        var adopted = await store.AdoptProviderSourceItemAsync(source.Id);
        Assert.True(firstSync.Success);
        Assert.NotNull(adopted);

        await store.UpsertTaskAsync(adopted with { Title = "Local wrapper title" });

        var detachedSync = await engine.SyncAsync(new EmptyProvider());
        var detached = await store.GetTaskAsync(adopted.Id);
        Assert.True(detachedSync.Success);
        Assert.NotNull(detached);
        Assert.False(detached.HasProviderSource);
        Assert.Equal("Local wrapper title", detached.Title);

        var restoredSync = await engine.SyncAsync(new FakeProvider());
        var relinked = await store.GetTaskAsync(adopted.Id);
        var sourceAfterRestore = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        Assert.True(restoredSync.Success);
        Assert.NotNull(relinked);
        Assert.Equal(adopted.Id, relinked.Id);
        Assert.Equal("Local wrapper title", relinked.Title);
        Assert.True(relinked.HasProviderSource);
        Assert.Equal(ProviderSourceAdoptionStates.Adopted, sourceAfterRestore.AdoptionState);
        Assert.Equal(adopted.Id, sourceAfterRestore.AdoptedTaskId);
        Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
    }

    [Fact]
    public async Task Sync_removes_not_adopted_source_item_missing_from_provider_snapshot()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var engine = new TaskSyncEngine(store);

        var firstSync = await engine.SyncAsync(new FakeProvider());
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
        Assert.True(firstSync.Success);
        Assert.Equal(ProviderSourceAdoptionStates.NotAdopted, source.AdoptionState);

        var secondSync = await engine.SyncAsync(new EmptyProvider());

        Assert.True(secondSync.Success);
        Assert.Equal(1, secondSync.TasksDeleted);
        Assert.Empty(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true, includeIgnored: true));
        Assert.Empty(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
    }

    [Fact]
    public async Task Sync_marks_adopted_provider_task_completed_from_completed_snapshot()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider();
        var engine = new TaskSyncEngine(store);

        var firstSync = await engine.SyncAsync(provider);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
        var adopted = await store.AdoptProviderSourceItemAsync(source.Id);
        Assert.True(firstSync.Success);
        Assert.NotNull(adopted);

        provider.IncludeActiveTask = false;
        provider.IncludeCompletedTask = true;
        var secondSync = await engine.SyncAsync(provider);

        var completed = await store.GetTaskAsync(adopted.Id);
        var sourceAfterSync = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        Assert.True(secondSync.Success);
        Assert.NotNull(completed);
        Assert.True(completed.HasProviderSource);
        Assert.Equal(TaskCompletionState.Completed, completed.CompletionState);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal(TaskCompletionState.Completed, sourceAfterSync.CompletionState);
        Assert.Equal(ProviderSourceAdoptionStates.Adopted, sourceAfterSync.AdoptionState);
    }

    [Fact]
    public async Task Sync_keeps_completed_snapshot_match_linked()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider();
        var engine = new TaskSyncEngine(store);

        var firstSync = await engine.SyncAsync(provider);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
        var adopted = await store.AdoptProviderSourceItemAsync(source.Id);
        Assert.True(firstSync.Success);
        Assert.NotNull(adopted);

        provider.IncludeActiveTask = false;
        provider.IncludeCompletedTask = true;
        var secondSync = await engine.SyncAsync(provider);

        var completed = await store.GetTaskAsync(adopted.Id);
        var sourceAfterSync = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true, includeIgnored: true));
        Assert.True(secondSync.Success);
        Assert.NotNull(completed);
        Assert.True(completed.HasProviderSource);
        Assert.Equal(TaskCompletionState.Completed, completed.CompletionState);
        Assert.Equal(TaskCompletionState.Completed, sourceAfterSync.CompletionState);
        Assert.Equal(ProviderSourceAdoptionStates.Adopted, sourceAfterSync.AdoptionState);
        Assert.Equal(adopted.Id, sourceAfterSync.AdoptedTaskId);
    }

    [Fact]
    public async Task Sync_ignores_unknown_provider_task_from_completed_snapshot()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider
        {
            IncludeActiveTask = false,
            IncludeCompletedTask = true,
        };
        var engine = new TaskSyncEngine(store);

        var result = await engine.SyncAsync(provider);

        Assert.True(result.Success);
        Assert.Empty(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true, includeIgnored: true));
        Assert.Empty(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
    }

    [Fact]
    public async Task Sync_preserves_ignored_source_item_missing_from_provider_snapshot()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var engine = new TaskSyncEngine(store);

        var firstSync = await engine.SyncAsync(new FakeProvider());
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
        var skipped = await store.SkipProviderSourceItemAsync(source.Id);
        Assert.True(firstSync.Success);
        Assert.True(skipped);

        var emptySync = await engine.SyncAsync(new EmptyProvider());
        var ignoredAfterEmptySync = Assert.Single(await store.GetProviderSourceItemsAsync(
            IntegrationIds.Todoist,
            includeIgnored: true));
        var returnedSync = await engine.SyncAsync(new FakeProvider());
        var ignoredAfterReturnedSync = Assert.Single(await store.GetProviderSourceItemsAsync(
            IntegrationIds.Todoist,
            includeIgnored: true));
        var intake = await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist);

        Assert.True(emptySync.Success);
        Assert.Equal(ProviderSourceAdoptionStates.Ignored, ignoredAfterEmptySync.AdoptionState);
        Assert.True(returnedSync.Success);
        Assert.Equal(ProviderSourceAdoptionStates.Ignored, ignoredAfterReturnedSync.AdoptionState);
        Assert.Empty(intake);
    }

    [Fact]
    public async Task Sync_reopens_completed_recurring_provider_task_with_next_source_date()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider
        {
            Title = "Synthetic recurring task",
            PlannedOn = new DateOnly(2026, 6, 6),
            RecurrenceRule = "every 6th",
        };
        var engine = new TaskSyncEngine(store);

        var firstSync = await engine.SyncAsync(provider);
        var task = Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
        await store.QueueCompletionAsync(new PendingCompletion
        {
            Id = "completion_recurring",
            TaskId = task.Id,
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = task.SourceProviderTaskId!,
            Completed = true,
            CompletedAt = DateTimeOffset.UtcNow,
        });
        await store.CompleteTaskAsync(task.Id);

        provider.SnapshotDelayMilliseconds = 10;
        provider.PlannedOn = new DateOnly(2026, 7, 6);
        var secondSync = await engine.SyncAsync(provider);

        var refreshed = await store.GetTaskAsync(task.Id);
        Assert.True(firstSync.Success);
        Assert.True(secondSync.Success);
        Assert.NotNull(refreshed);
        Assert.Equal(1, provider.CompletedCalls);
        Assert.Equal(TaskCompletionState.Open, refreshed.CompletionState);
        Assert.Null(refreshed.CompletedAt);
        Assert.Equal(new DateOnly(2026, 7, 6), refreshed.PlannedOn);
        Assert.Equal(new DateOnly(2026, 7, 6), refreshed.SourcePlannedOn);
        Assert.Equal("every 6th", refreshed.SourceRecurrenceRule);
        Assert.Null(refreshed.RecurrenceRule);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sync_reopens_task_completed_only_in_provider_without_cloud_confirmation(bool storeCompletedAtAsUnixMilliseconds)
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider { Title = "Synthetic provider-only task", RecurrenceRule = "daily" };
        var engine = new TaskSyncEngine(store);
        Assert.True((await engine.SyncAsync(provider)).Success);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
        var task = await store.AdoptProviderSourceItemAsync(source.Id);
        Assert.NotNull(task);

        provider.IncludeActiveTask = false;
        provider.IncludeCompletedTask = true;
        Assert.True((await engine.SyncAsync(provider)).Success);
        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(task!.Id))!.CompletionState);

        var storedCompletionAt = DateTimeOffset.UtcNow.AddDays(-1);
        var completedAtValue = storeCompletedAtAsUnixMilliseconds
            ? storedCompletionAt.ToUnixTimeMilliseconds()
            : storedCompletionAt.ToUnixTimeSeconds();
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tasks SET completed_at=@completed_at WHERE id=@task_id";
            command.Parameters.AddWithValue("@completed_at", completedAtValue);
            command.Parameters.AddWithValue("@task_id", task.Id);
            await command.ExecuteNonQueryAsync();
        }

        provider.IncludeActiveTask = true;
        provider.IncludeCompletedTask = false;
        provider.PlannedOn = new DateOnly(2026, 12, 27);
        Assert.True((await engine.SyncAsync(provider)).Success);

        var refreshed = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(TaskCompletionState.Open, refreshed.CompletionState);
        Assert.Null(refreshed.CompletedAt);
        Assert.Equal(new DateOnly(2026, 12, 27), refreshed.PlannedOn);
        Assert.Equal(0, provider.CompletedCalls);
        Assert.Equal(0, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_commands WHERE task_id=@task_id", task.Id));
    }

    [Fact]
    public async Task Sync_reopens_cloud_linked_task_completed_only_in_provider()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider { Title = "Synthetic cloud-linked provider task", RecurrenceRule = "weekly" };
        var engine = new TaskSyncEngine(store);
        Assert.True((await engine.SyncAsync(provider)).Success);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
        var task = await store.AdoptProviderSourceItemAsync(source.Id);
        Assert.NotNull(task);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO personal_cloud_provider_sources(instance_id,task_id,source_json) VALUES('cloud_instance',@task_id,'{}')";
            command.Parameters.AddWithValue("@task_id", task!.Id);
            await command.ExecuteNonQueryAsync();
        }

        provider.IncludeActiveTask = false;
        provider.IncludeCompletedTask = true;
        Assert.True((await engine.SyncAsync(provider)).Success);
        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(task!.Id))!.CompletionState);

        provider.IncludeActiveTask = true;
        provider.IncludeCompletedTask = false;
        provider.PlannedOn = new DateOnly(2026, 12, 27);
        Assert.True((await engine.SyncAsync(provider)).Success);

        var refreshed = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(TaskCompletionState.Open, refreshed.CompletionState);
        Assert.Null(refreshed.CompletedAt);
        Assert.Equal(new DateOnly(2026, 12, 27), refreshed.PlannedOn);
        Assert.Equal(0, provider.CompletedCalls);
        Assert.Equal(0, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_commands WHERE task_id=@task_id", task.Id));
        Assert.Equal(1, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_observed_reopens WHERE task_id=@task_id", task.Id));
    }

    [Fact]
    public async Task Sync_keeps_open_task_open_while_a_cloud_reopen_is_pending_without_local_intent()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider { Title = "Synthetic pending cloud reopen" };
        var engine = new TaskSyncEngine(store);
        Assert.True((await engine.SyncAsync(provider)).Success);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist));
        var task = await store.AdoptProviderSourceItemAsync(source.Id);
        Assert.NotNull(task);
        await store.UpsertTaskAsync(task! with { CompletionState = TaskCompletionState.Open, CompletedAt = null });

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO personal_cloud_provider_sources(instance_id,task_id,source_json)
                VALUES('cloud_instance',@task_id,'{}');
                INSERT INTO personal_cloud_provider_commands(instance_id,task_id,requested_seq,command,state)
                VALUES('cloud_instance',@task_id,901,'reopen','pending_desktop');
                """;
            command.Parameters.AddWithValue("@task_id", task!.Id);
            await command.ExecuteNonQueryAsync();
        }

        provider.IncludeActiveTask = false;
        provider.IncludeCompletedTask = true;
        Assert.True((await engine.SyncAsync(provider)).Success);

        var refreshed = (await store.GetTaskAsync(task!.Id))!;
        Assert.Equal(TaskCompletionState.Open, refreshed.CompletionState);
        Assert.Null(refreshed.CompletedAt);
        Assert.Equal(0, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_outbox WHERE entity_id=@task_id AND operation IN ('task.complete','task.reopen')", task.Id));
    }

    [Fact]
    public async Task Sync_reopens_personal_cloud_completed_recurring_task_when_provider_advances()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider
        {
            Title = "Synthetic recurring task",
            PlannedOn = new DateOnly(2026, 11, 26),
            RecurrenceRule = "every month",
        };
        var engine = new TaskSyncEngine(store);

        var firstSync = await engine.SyncAsync(provider);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(
            IntegrationIds.Todoist, includeAdopted: true));
        var adopted = await store.AdoptProviderSourceItemAsync(source.Id);
        Assert.True(firstSync.Success);
        Assert.NotNull(adopted);

        await store.CompleteTaskWithPersonalCloudIntentAsync(
            adopted.Id,
            new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero),
            baseEntityVersion: 1,
            instanceId: "cloud_instance",
            deviceId: "desktop_test");

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            var confirmedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            command.CommandText = """
                UPDATE personal_cloud_outbox
                SET acknowledged_at=@confirmed_at,server_change_seq=NULL
                WHERE entity_id=@task_id AND operation='task.complete';
                INSERT INTO personal_cloud_provider_sources(instance_id,task_id,source_json)
                VALUES ('cloud_instance',@task_id,'{}');
                INSERT INTO personal_cloud_entity_versions(instance_id,product,entity_id,entity_version,updated_at)
                VALUES ('cloud_instance','tasks',@task_id,2,@confirmed_at);
                INSERT INTO personal_cloud_provider_commands
                    (instance_id, task_id, requested_seq, command, state)
                VALUES ('cloud_instance', @task_id, 2, 'complete', 'confirmed');
                INSERT INTO personal_cloud_outbox
                    (mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at,acknowledged_at)
                VALUES ('mutation_result','tasks','cloud_instance','desktop_test',2,'externalCommand.confirm',@task_id,@confirm_json,@hash,@confirmed_at,@confirmed_at);
                """;
            command.Parameters.AddWithValue("@task_id", adopted.Id);
            command.Parameters.AddWithValue("@confirmed_at", confirmedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@confirm_json", JsonSerializer.Serialize(new
            {
                payload = new { requestedChangeSeq = 2, resolvedAt = confirmedAt.ToString("O") },
            }));
            command.Parameters.AddWithValue("@hash", new string('a', 64));
            await command.ExecuteNonQueryAsync();
            Assert.Equal(0, await ScalarIntAsync("SELECT count(*) FROM provider_completion_confirmations WHERE task_id=@task_id", adopted.Id));
        }

        provider.PlannedOn = new DateOnly(2026, 12, 26);
        var secondSync = await engine.SyncAsync(provider);

        var refreshed = await store.GetTaskAsync(adopted.Id);
        var refreshedSource = Assert.Single(await store.GetProviderSourceItemsAsync(
            IntegrationIds.Todoist, includeAdopted: true));
        Assert.True(secondSync.Success);
        Assert.NotNull(refreshed);
        Assert.Equal(TaskCompletionState.Open, refreshedSource.CompletionState);
        Assert.Equal(new DateOnly(2026, 12, 26), refreshedSource.PlannedOn);
        Assert.Equal(new DateOnly(2026, 12, 26), refreshed.PlannedOn);
        Assert.Equal(
            (TaskCompletionState.Open, (DateTimeOffset?)null),
            (refreshed!.CompletionState, refreshed.CompletedAt));
        Assert.Equal(1, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_observed_reopens WHERE instance_id='cloud_instance' AND task_id=@task_id", adopted.Id));
        Assert.Equal(1, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_commands WHERE instance_id='cloud_instance' AND task_id=@task_id", adopted.Id));
        await store.PrepareProviderObservedReopensAsync("cloud_instance", "desktop_test", "artifact-v1");
        await store.PrepareProviderObservedReopensAsync("cloud_instance", "desktop_test", "artifact-v1");
        Assert.Equal(1, (await store.GetPendingPersonalCloudOutboxAsync("cloud_instance", "desktop_test")).Count(entry => entry.Operation == "task.providerObservedReopen"));
    }

    [Fact]
    public async Task Sync_reopens_confirmed_linked_nonrepeating_task_from_fresh_provider_read()
    {
        var (store, provider, task, _) = await CreateConfirmedCloudTaskAsync("Synthetic one-off task", repeating: false);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        await store.UpsertProviderSourceItemAsync(source with { CompletionState = TaskCompletionState.Open, LastSeenAt = DateTimeOffset.UtcNow });
        Assert.Equal(TaskCompletionState.Open, (await store.GetTaskAsync(task.Id))!.CompletionState);
        Assert.Equal(0, provider.CompletedCalls);
    }

    [Fact]
    public async Task Sync_keeps_completed_task_when_provider_open_read_predates_confirmation()
    {
        var (store, _, task, confirmedAt) = await CreateConfirmedCloudTaskAsync("Synthetic stale task", repeating: false);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        await store.UpsertProviderSourceItemAsync(source with { CompletionState = TaskCompletionState.Open, LastSeenAt = confirmedAt.AddSeconds(-1) });
        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(task.Id))!.CompletionState);
        Assert.Equal(0, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_observed_reopens WHERE task_id=@task_id", task.Id));
    }

    [Fact]
    public async Task Sync_requires_provider_open_read_strictly_after_completion_confirmation()
    {
        var (store, _, task, confirmedAt) = await CreateConfirmedCloudTaskAsync("Synthetic boundary task", repeating: false);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        await store.UpsertProviderSourceItemAsync(source with { CompletionState = TaskCompletionState.Open, LastSeenAt = confirmedAt });
        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(task.Id))!.CompletionState);

        await store.UpsertProviderSourceItemAsync(source with { CompletionState = TaskCompletionState.Open, LastSeenAt = confirmedAt.AddMilliseconds(1) });
        Assert.Equal(TaskCompletionState.Open, (await store.GetTaskAsync(task.Id))!.CompletionState);
        Assert.Equal(1, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_observed_reopens WHERE task_id=@task_id", task.Id));
    }

    [Fact]
    public async Task Later_legacy_completion_intent_without_exact_change_sequence_keeps_task_completed()
    {
        var (store, _, task, confirmedAt) = await CreateConfirmedCloudTaskAsync(
            "Synthetic historical confirmation task", repeating: false, legacyConfirmation: true);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO personal_cloud_outbox
                    (mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at,acknowledged_at,server_change_seq)
                VALUES ('mutation_later_completion','tasks','cloud_instance','desktop_test',3,'task.complete',@task_id,'{}',@hash,@at,@at,NULL)
                """;
            command.Parameters.AddWithValue("@task_id", task.Id);
            command.Parameters.AddWithValue("@hash", new string('b', 64));
            command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync();
        }

        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        await store.UpsertProviderSourceItemAsync(source with
        {
            CompletionState = TaskCompletionState.Open,
            LastSeenAt = confirmedAt.AddSeconds(1),
        });

        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(task.Id))!.CompletionState);
        Assert.Equal(0, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_observed_reopens WHERE task_id=@task_id", task.Id));
    }

    [Fact]
    public async Task Acknowledged_recompletion_without_its_pulled_command_keeps_task_completed()
    {
        var (store, provider, task, confirmedAt) = await CreateConfirmedCloudTaskAsync(
            "Synthetic acknowledged re-completion", repeating: true);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO personal_cloud_outbox
                    (mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at,acknowledged_at,server_change_seq)
                VALUES('mutation_acknowledged_recompletion','tasks','cloud_instance','desktop_test',3,'task.complete',@task_id,'{}',@hash,@acknowledged_at,@acknowledged_at,3);
                UPDATE personal_cloud_device_state SET next_device_sequence=4
                  WHERE instance_id='cloud_instance' AND device_id='desktop_test';
                """;
            command.Parameters.AddWithValue("@task_id", task.Id);
            command.Parameters.AddWithValue("@hash", new string('e', 64));
            command.Parameters.AddWithValue("@acknowledged_at", confirmedAt.AddSeconds(1).ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync();
        }

        provider.IncludeActiveTask = true;
        provider.IncludeCompletedTask = false;
        Assert.True((await new TaskSyncEngine(store).SyncAsync(provider)).Success);

        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(task.Id))!.CompletionState);
        Assert.Equal(0, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_observed_reopens WHERE task_id=@task_id", task.Id));
        Assert.Equal(1, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_commands WHERE task_id=@task_id AND command='complete' AND requested_seq=1 AND state='confirmed'", task.Id));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Prior_provider_completion_does_not_override_a_current_confirmation(int readOffsetSeconds)
    {
        var (store, _, task, confirmedAt) = await CreateConfirmedCloudTaskAsync(
            "Synthetic stale provider completion", repeating: true);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        await store.UpsertProviderSourceItemAsync(source with
        {
            CompletionState = TaskCompletionState.Completed,
            LastSeenAt = confirmedAt.AddSeconds(-10),
        });
        source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        await store.UpsertProviderSourceItemAsync(source with
        {
            CompletionState = TaskCompletionState.Open,
            LastSeenAt = confirmedAt.AddSeconds(readOffsetSeconds),
        });

        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(task.Id))!.CompletionState);
        Assert.Equal(0, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_observed_reopens WHERE task_id=@task_id", task.Id));
    }

    [Fact]
    public async Task Pending_recompletion_stays_guarded_after_an_older_provider_confirmation()
    {
        var (store, _, task, confirmedAt) = await CreateConfirmedCloudTaskAsync(
            "Synthetic in-flight recompletion", repeating: true);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO pending_completions(id,task_id,provider,provider_task_id,created_at)
                VALUES('pending_recompletion',@task_id,'todoist','provider_task',@created_at)
                """;
            command.Parameters.AddWithValue("@task_id", task.Id);
            command.Parameters.AddWithValue("@created_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync();
        }

        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        await store.UpsertProviderSourceItemAsync(source with
        {
            CompletionState = TaskCompletionState.Open,
            LastSeenAt = confirmedAt.AddSeconds(1),
        });

        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(task.Id))!.CompletionState);
        Assert.Equal(0, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_observed_reopens WHERE task_id=@task_id", task.Id));
    }

    [Fact]
    public async Task Higher_cancelled_provider_command_does_not_supersede_latest_completion_intent()
    {
        var (store, _, task, confirmedAt) = await CreateConfirmedCloudTaskAsync(
            "Synthetic cancelled newer command", repeating: true);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO personal_cloud_outbox
                    (mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at,acknowledged_at,server_change_seq)
                VALUES('mutation_newer_completion','tasks','cloud_instance','desktop_test',3,'task.complete',@task_id,'{}',@hash,@at,@at,3);
                UPDATE personal_cloud_provider_commands SET requested_seq=4,state='cancelled'
                  WHERE instance_id='cloud_instance' AND task_id=@task_id;
                DELETE FROM personal_cloud_entity_versions WHERE instance_id='cloud_instance' AND entity_id=@task_id;
                UPDATE personal_cloud_device_state SET next_device_sequence=4
                  WHERE instance_id='cloud_instance' AND device_id='desktop_test';
                """;
            command.Parameters.AddWithValue("@task_id", task.Id);
            command.Parameters.AddWithValue("@hash", new string('f', 64));
            command.Parameters.AddWithValue("@at", confirmedAt.AddSeconds(1).ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync();
        }

        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        await store.UpsertProviderSourceItemAsync(source with
        {
            CompletionState = TaskCompletionState.Open,
            LastSeenAt = confirmedAt.AddSeconds(2),
        });

        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(task.Id))!.CompletionState);
        Assert.Equal(0, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_observed_reopens WHERE task_id=@task_id", task.Id));
    }

    [Theory]
    [InlineData("rejected", false)]
    [InlineData("cancelled", false)]
    [InlineData("rejected", true)]
    public async Task Failed_or_cancelled_completion_without_confirmation_keeps_cloud_completion(string terminalState, bool legacyConfirmation)
    {
        var (store, _, task, confirmedAt) = await CreateConfirmedCloudTaskAsync(
            "Synthetic failed completion", repeating: false, legacyConfirmation: legacyConfirmation);
        var failedAt = confirmedAt.AddMinutes(1);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE personal_cloud_provider_commands SET state=@state WHERE task_id=@task_id";
            if (legacyConfirmation)
                command.CommandText += "; UPDATE personal_cloud_outbox SET operation='externalCommand.reject',canonical_json=json_set(canonical_json,'$.payload.resolvedAt',@failed_at) WHERE mutation_id='mutation_result'";
            command.Parameters.AddWithValue("@state", terminalState);
            command.Parameters.AddWithValue("@task_id", task.Id);
            command.Parameters.AddWithValue("@failed_at", failedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }

        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        await store.UpsertProviderSourceItemAsync(source with
        {
            CompletionState = TaskCompletionState.Open,
            LastSeenAt = failedAt.AddSeconds(-1),
        });

        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(task.Id))!.CompletionState);
        Assert.Equal(0, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_observed_reopens WHERE task_id=@task_id", task.Id));
    }

    [Fact]
    public async Task Sync_keeps_legacy_completion_on_first_sync_when_legacy_confirmation_is_newer_than_provider_read()
    {
        var (store, provider, task, _) = await CreateConfirmedCloudTaskAsync(
            "Synthetic stale upgrade task", repeating: true, legacyConfirmation: true);
        var newerConfirmation = DateTimeOffset.UtcNow.AddMinutes(1);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE personal_cloud_outbox SET canonical_json=json_set(canonical_json,'$.payload.resolvedAt',@resolved_at) WHERE mutation_id='mutation_result'";
            command.Parameters.AddWithValue("@resolved_at", newerConfirmation.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        Assert.True((await new TaskSyncEngine(store).SyncAsync(provider)).Success);
        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(task.Id))!.CompletionState);
        Assert.Equal(0, await ScalarIntAsync("SELECT count(*) FROM personal_cloud_provider_observed_reopens WHERE task_id=@task_id", task.Id));
    }

    [Fact]
    public async Task Provider_confirmation_comparisons_normalize_mixed_legacy_and_millisecond_timestamps()
    {
        var (store, _, task, confirmedAt) = await CreateConfirmedCloudTaskAsync(
            "Synthetic mixed timestamp task", repeating: false, legacyConfirmation: true);
        var newerConfirmation = confirmedAt.AddMinutes(1);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO personal_cloud_outbox
                    (mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at,acknowledged_at,server_change_seq)
                VALUES('confirmation_newer','tasks','cloud_instance','desktop_test',3,'externalCommand.confirm',@task_id,@json,@hash,@confirmed_at,@confirmed_at,3);
                INSERT INTO provider_completion_confirmations(confirmation_id,instance_id,task_id,requested_change_seq,confirmed_at)
                VALUES('confirmation_newer','cloud_instance',@task_id,1,@confirmed_at)
                """;
            command.Parameters.AddWithValue("@task_id", task.Id);
            command.Parameters.AddWithValue("@confirmed_at", newerConfirmation.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@json", System.Text.Json.JsonSerializer.Serialize(new
            {
                payload = new { requestedChangeSeq = 1, resolvedAt = newerConfirmation.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture) },
            }));
            command.Parameters.AddWithValue("@hash", new string('b', 64));
            await command.ExecuteNonQueryAsync();
        }

        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        await store.UpsertProviderSourceItemAsync(source with { CompletionState = TaskCompletionState.Open, LastSeenAt = confirmedAt.AddSeconds(30) });
        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(task.Id))!.CompletionState);
    }

    [Fact]
    public async Task Sync_keeps_completed_task_while_cloud_provider_command_is_in_flight()
    {
        var (store, _, task, _) = await CreateConfirmedCloudTaskAsync("Synthetic in-flight task", repeating: false);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE personal_cloud_provider_commands SET state='pending_desktop' WHERE task_id=@task_id";
            command.Parameters.AddWithValue("@task_id", task.Id);
            await command.ExecuteNonQueryAsync();
        }
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        await store.UpsertProviderSourceItemAsync(source with { CompletionState = TaskCompletionState.Open, LastSeenAt = DateTimeOffset.UtcNow });
        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(task.Id))!.CompletionState);
    }

    [Fact]
    public async Task Sync_repairs_legacy_confirmed_completion_from_acknowledged_provider_result()
    {
        var (store, provider, task, _) = await CreateConfirmedCloudTaskAsync("Synthetic upgrade repair task", repeating: true, legacyConfirmation: true);
        Assert.True((await new TaskSyncEngine(store).SyncAsync(provider)).Success);
        Assert.Equal(TaskCompletionState.Open, (await store.GetTaskAsync(task.Id))!.CompletionState);
    }

    [Fact]
    public async Task Rejected_provider_observed_reopen_is_deferred_until_cloud_artifact_changes()
    {
        var (store, _, task, _) = await CreateConfirmedCloudTaskAsync("Synthetic compatibility task", repeating: false);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        await store.UpsertProviderSourceItemAsync(source with { CompletionState = TaskCompletionState.Open, LastSeenAt = DateTimeOffset.UtcNow });
        await store.PrepareProviderObservedReopensAsync("cloud_instance", "desktop_test", "artifact-v1");
        var pending = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync("cloud_instance", "desktop_test"),
            entry => entry.Operation == "task.providerObservedReopen");
        var skippedVersion = await ScalarIntAsync("SELECT CAST(json_extract(canonical_json,'$.baseEntityVersion') AS INTEGER) FROM personal_cloud_outbox WHERE mutation_id=@task_id", pending.MutationId);
        var cancelMutationId = "mutation_cancel_after_reopen";
        var editMutationId = "mutation_edit_after_reopen";
        var clientCreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var cancelJson = PersonalCloudK0Codec.Canonicalize(System.Text.Json.JsonSerializer.Serialize(new
        {
            protocolVersion = 1, product = "tasks", instanceId = "cloud_instance", deviceId = "desktop_test",
            mutationId = cancelMutationId, deviceSequence = 4, operation = "externalCommand.cancel", entityId = task.Id,
            baseEntityVersion = (long?)null, payload = new { requestedChangeSeq = 2, reason = "missing_source" },
            clientCreatedAt,
        }));
        var editJson = PersonalCloudK0Codec.Canonicalize(System.Text.Json.JsonSerializer.Serialize(new
        {
            protocolVersion = 1, product = "tasks", instanceId = "cloud_instance", deviceId = "desktop_test",
            mutationId = editMutationId, deviceSequence = 5, operation = "task.updateOpenzaFields", entityId = task.Id,
            baseEntityVersion = skippedVersion + 1, payload = new { workflowStatus = "next" },
            clientCreatedAt,
        }));
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO personal_cloud_outbox(mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at)
                VALUES(@cancel_id,'tasks','cloud_instance','desktop_test',4,'externalCommand.cancel',@task_id,@cancel_json,@cancel_hash,@created_at);
                INSERT INTO personal_cloud_outbox(mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at)
                VALUES(@edit_id,'tasks','cloud_instance','desktop_test',5,'task.updateOpenzaFields',@task_id,@edit_json,@edit_hash,@created_at);
                INSERT INTO personal_cloud_provider_command_cancellations(instance_id,device_id,task_id,requested_seq,reason,mutation_id,created_at)
                VALUES('cloud_instance','desktop_test',@task_id,2,'missing_source',@cancel_id,@created_at);
                UPDATE personal_cloud_device_state SET next_device_sequence=6
                  WHERE instance_id='cloud_instance' AND device_id='desktop_test';
                """;
            command.Parameters.AddWithValue("@cancel_id", cancelMutationId);
            command.Parameters.AddWithValue("@edit_id", editMutationId);
            command.Parameters.AddWithValue("@task_id", task.Id);
            command.Parameters.AddWithValue("@cancel_json", cancelJson);
            command.Parameters.AddWithValue("@cancel_hash", PersonalCloudK0Codec.ComputeCanonicalHash(cancelJson));
            command.Parameters.AddWithValue("@edit_json", editJson);
            command.Parameters.AddWithValue("@edit_hash", PersonalCloudK0Codec.ComputeCanonicalHash(editJson));
            command.Parameters.AddWithValue("@created_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync();
        }

        Assert.True(await store.DeferRejectedProviderObservedReopenAsync("cloud_instance", "desktop_test", pending.MutationId, "artifact-v1"));
        Assert.DoesNotContain(await store.GetPendingPersonalCloudOutboxAsync("cloud_instance", "desktop_test"),
            entry => entry.Operation == "task.providerObservedReopen");
        var cancellation = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync("cloud_instance", "desktop_test"),
            entry => entry.Operation == "externalCommand.cancel");
        var edit = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync("cloud_instance", "desktop_test"),
            entry => entry.Operation == "task.updateOpenzaFields");
        Assert.NotEqual(cancelMutationId, cancellation.MutationId);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT mutation_id FROM personal_cloud_provider_command_cancellations WHERE instance_id='cloud_instance' AND task_id=@task_id AND requested_seq=2";
            command.Parameters.AddWithValue("@task_id", task.Id);
            Assert.Equal(cancellation.MutationId, await command.ExecuteScalarAsync());
        }
        Assert.Equal(skippedVersion, await ScalarIntAsync("SELECT CAST(json_extract(canonical_json,'$.baseEntityVersion') AS INTEGER) FROM personal_cloud_outbox WHERE mutation_id=@task_id", edit.MutationId));
        Assert.True(await store.HasDeferredProviderCommandCancellationsAsync("cloud_instance"));
        var acceptedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        await store.AcknowledgePersonalCloudMutationAsync("cloud_instance", "desktop_test", System.Text.Json.JsonSerializer.Serialize(new
        {
            protocolVersion = 1, product = "tasks", mutationId = cancellation.MutationId, outcome = "accepted",
            entityId = task.Id, entityVersion = 2, changeSeq = 3, serverAcceptedAt = acceptedAt,
        }));
        Assert.False(await store.HasDeferredProviderCommandCancellationsAsync("cloud_instance"));
        await store.PrepareProviderObservedReopensAsync("cloud_instance", "desktop_test", "artifact-v1");
        Assert.DoesNotContain(await store.GetPendingPersonalCloudOutboxAsync("cloud_instance", "desktop_test"),
            entry => entry.Operation == "task.providerObservedReopen");

        await store.PrepareProviderObservedReopensAsync("cloud_instance", "desktop_test", "artifact-v2");
        Assert.Single(await store.GetPendingPersonalCloudOutboxAsync("cloud_instance", "desktop_test"),
            entry => entry.Operation == "task.providerObservedReopen");
        Assert.Equal(TaskCompletionState.Open, (await store.GetTaskAsync(task.Id))!.CompletionState);
    }

    [Fact]
    public async Task Sync_refreshes_open_recurring_provider_task_with_next_source_date()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider
        {
            Title = "Pay monthly bill",
            PlannedOn = new DateOnly(2026, 6, 6),
            RecurrenceRule = "every 6th",
        };
        var engine = new TaskSyncEngine(store);

        var firstSync = await engine.SyncAsync(provider);
        var task = Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));

        provider.PlannedOn = new DateOnly(2026, 7, 6);
        provider.IncludeCompletedTask = true;
        var secondSync = await engine.SyncAsync(provider);

        var refreshed = await store.GetTaskAsync(task.Id);
        Assert.True(firstSync.Success);
        Assert.True(secondSync.Success);
        Assert.NotNull(refreshed);
        Assert.Equal(TaskCompletionState.Open, refreshed.CompletionState);
        Assert.Equal(new DateOnly(2026, 7, 6), refreshed.PlannedOn);
        Assert.Equal(new DateOnly(2026, 7, 6), refreshed.SourcePlannedOn);
        Assert.Equal("every 6th", refreshed.SourceRecurrenceRule);
        Assert.Null(refreshed.RecurrenceRule);
    }

    private SqliteTaskStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new SqliteTaskStore(Path.Combine(_directory, "openza_tasks.db"));
    }

    private async Task<(SqliteTaskStore Store, FakeProvider Provider, TaskItem Task, DateTimeOffset ConfirmedAt)> CreateConfirmedCloudTaskAsync(
        string title, bool repeating, bool legacyConfirmation = false)
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var provider = new FakeProvider
        {
            Title = title,
            PlannedOn = repeating ? new DateOnly(2026, 11, 26) : null,
            RecurrenceRule = repeating ? "daily" : null,
        };
        Assert.True((await new TaskSyncEngine(store).SyncAsync(provider)).Success);
        var source = Assert.Single(await store.GetProviderSourceItemsAsync(IntegrationIds.Todoist, includeAdopted: true));
        var task = await store.AdoptProviderSourceItemAsync(source.Id);
        Assert.NotNull(task);
        await store.CompleteTaskWithPersonalCloudIntentAsync(task!.Id, DateTimeOffset.UtcNow.AddMinutes(-5), 1, "cloud_instance", "desktop_test");
        var confirmedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE personal_cloud_outbox
                  SET acknowledged_at=@confirmed_at,server_change_seq=CASE WHEN @legacy=1 THEN NULL ELSE 1 END
                  WHERE entity_id=@task_id AND operation='task.complete';
                INSERT INTO personal_cloud_provider_sources(instance_id,task_id,source_json) VALUES('cloud_instance',@task_id,'{}');
                INSERT INTO personal_cloud_entity_versions(instance_id,product,entity_id,entity_version,updated_at)
                  VALUES('cloud_instance','tasks',@task_id,2,@updated_at)
                  ON CONFLICT(instance_id,product,entity_id) DO UPDATE SET entity_version=excluded.entity_version,updated_at=excluded.updated_at;
                INSERT INTO personal_cloud_provider_commands(instance_id,task_id,requested_seq,command,state)
                  VALUES('cloud_instance',@task_id,1,'complete','confirmed');
                """;
            command.Parameters.AddWithValue("@task_id", task.Id);
            command.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@confirmed_at", confirmedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@legacy", legacyConfirmation ? 1 : 0);
            await command.ExecuteNonQueryAsync();
            if (!legacyConfirmation)
            {
                await using var confirmation = connection.CreateCommand();
                confirmation.CommandText = """
                    INSERT INTO personal_cloud_outbox
                        (mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at,acknowledged_at,server_change_seq)
                    VALUES('confirmation_test','tasks','cloud_instance','desktop_test',2,'externalCommand.confirm',@task_id,@json,@hash,@confirmed_at,@confirmed_at,2);
                    INSERT INTO provider_completion_confirmations(confirmation_id,instance_id,task_id,requested_change_seq,confirmed_at)
                    VALUES('confirmation_test','cloud_instance',@task_id,1,@confirmed_at)
                    """;
                confirmation.Parameters.AddWithValue("@task_id", task.Id);
                confirmation.Parameters.AddWithValue("@confirmed_at", confirmedAt.ToUnixTimeMilliseconds());
                confirmation.Parameters.AddWithValue("@json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    payload = new { requestedChangeSeq = 1, resolvedAt = confirmedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture) },
                }));
                confirmation.Parameters.AddWithValue("@hash", new string('a', 64));
                await confirmation.ExecuteNonQueryAsync();
            }
            else
            {
                await using var confirmation = connection.CreateCommand();
                confirmation.CommandText = """
                    INSERT INTO personal_cloud_outbox(mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at,acknowledged_at,server_change_seq)
                    VALUES('mutation_result','tasks','cloud_instance','desktop_test',2,'externalCommand.confirm',@task_id,@json,@hash,@confirmed_at,@confirmed_at,2)
                    """;
                confirmation.Parameters.AddWithValue("@task_id", task.Id);
                confirmation.Parameters.AddWithValue("@json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    payload = new { requestedChangeSeq = 1, resolvedAt = confirmedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture) },
                }));
                confirmation.Parameters.AddWithValue("@hash", new string('a', 64));
                confirmation.Parameters.AddWithValue("@confirmed_at", confirmedAt.ToUnixTimeMilliseconds());
                await confirmation.ExecuteNonQueryAsync();
            }
        }
        return (store, provider, task, confirmedAt);
    }

    private async Task<int> ScalarIntAsync(string sql, string taskId)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "openza_tasks.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@task_id", taskId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        TestDirectory.Delete(_directory);
    }

    private sealed class FakeProvider : ITaskDateUpdateProvider, ITaskProjectMoveProvider
    {
        public string IntegrationId => IntegrationIds.Todoist;
        public string Title { get; set; } = "Remote task";
        public string? Description { get; set; }
        public string? ProjectId { get; set; }
        public DateOnly? PlannedOn { get; set; }
        public string? RecurrenceRule { get; set; }
        public IReadOnlyList<LabelItem> Labels { get; set; } = [];
        public IReadOnlyList<ProjectItem> Projects { get; set; } = [];
        public IReadOnlyList<LabelItem> SnapshotLabels { get; set; } = [];
        public bool IncludeActiveTask { get; set; } = true;
        public bool IncludeCompletedTask { get; set; }
        public bool IncludeChildTask { get; set; }
        public int SnapshotDelayMilliseconds { get; set; }
        public int CompletedCalls { get; private set; }
        public bool ThrowOnMove { get; set; }
        public List<string> OutboundOperationOrder { get; } = [];
        public List<PendingTaskDateUpdate> DateUpdates { get; } = [];
        public List<(string TaskId, string ProjectId)> ProjectMoves { get; } = [];

        public async Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (SnapshotDelayMilliseconds > 0)
                await Task.Delay(SnapshotDelayMilliseconds, cancellationToken).ConfigureAwait(false);

            List<TaskItem> tasks = [];
            if (IncludeActiveTask)
            {
                tasks.Add(new TaskItem
                {
                    Id = "todoist_remote_1",
                    ExternalId = "remote_1",
                    IntegrationId = IntegrationIds.Todoist,
                    Title = Title,
                    Description = Description,
                    ProjectId = ProjectId,
                    PlannedOn = PlannedOn,
                    RecurrenceRule = RecurrenceRule,
                    Labels = Labels,
                });
            }

            if (IncludeChildTask)
            {
                tasks.Add(new TaskItem
                {
                    Id = "todoist_remote_child",
                    ExternalId = "remote_child",
                    IntegrationId = IntegrationIds.Todoist,
                    Title = "Remote child",
                    ParentId = "todoist_remote_1",
                });
            }

            List<TaskItem> completedTasks = [];
            if (IncludeCompletedTask)
            {
                completedTasks.Add(new TaskItem
                {
                    Id = "todoist_remote_1",
                    ExternalId = "remote_1",
                    IntegrationId = IntegrationIds.Todoist,
                    Title = Title,
                    CompletionState = TaskCompletionState.Completed,
                    CompletedAt = new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero),
                });
            }

            return new ProviderSnapshot(
                tasks,
                Projects,
                SnapshotLabels)
            {
                CompletedTasks = completedTasks,
            };
        }

        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default)
        {
            OutboundOperationOrder.Add("completion");
            CompletedCalls++;
            return Task.CompletedTask;
        }

        public Task UpdateTaskDateAsync(PendingTaskDateUpdate update, CancellationToken cancellationToken = default)
        {
            OutboundOperationOrder.Add("date");
            DateUpdates.Add(update);
            PlannedOn = update.PlannedOn;
            return Task.CompletedTask;
        }

        public Task MoveTaskAsync(string taskId, string projectId, CancellationToken cancellationToken = default)
        {
            OutboundOperationOrder.Add("move");
            if (ThrowOnMove)
            {
                throw new InvalidOperationException("Move failed.");
            }

            ProjectMoves.Add((taskId, projectId));
            ProjectId = $"todoist_{projectId}";
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyProvider : ISyncProvider
    {
        public string IntegrationId => IntegrationIds.Todoist;

        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderSnapshot([], [], []));

        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
