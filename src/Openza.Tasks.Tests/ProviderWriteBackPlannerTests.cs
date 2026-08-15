using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class ProviderWriteBackPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateCompletion_uses_source_provider_for_adopted_task()
    {
        var task = new TaskItem
        {
            Id = "local-wrapper",
            IntegrationId = IntegrationIds.Local,
            SourceIntegrationId = IntegrationIds.Todoist,
            SourceExternalId = "todoist-task",
            SourceProviderTaskId = "todoist-task",
        };

        var completion = ProviderWriteBackPlanner.CreateCompletion(task, completed: true, Now);

        Assert.NotNull(completion);
        Assert.Equal(IntegrationIds.Todoist, completion.Provider);
        Assert.Equal("todoist-task", completion.ProviderTaskId);
        Assert.True(completion.Completed);
        Assert.Equal(Now, completion.CompletedAt);
    }

    [Fact]
    public void CreateCompletion_uses_external_id_for_provider_task()
    {
        var task = new TaskItem
        {
            Id = "todoist_local_id",
            IntegrationId = IntegrationIds.Todoist,
            ExternalId = "todoist-task",
        };

        var completion = ProviderWriteBackPlanner.CreateCompletion(task, completed: false, Now);

        Assert.NotNull(completion);
        Assert.Equal("todoist-task", completion.ProviderTaskId);
        Assert.False(completion.Completed);
        Assert.Null(completion.CompletedAt);
    }

    [Fact]
    public void CreateCompletion_returns_null_for_local_task()
    {
        var task = new TaskItem
        {
            Id = "local-task",
            IntegrationId = IntegrationIds.Local,
        };

        Assert.Null(ProviderWriteBackPlanner.CreateCompletion(task, completed: true, Now));
    }

    [Fact]
    public void CreateCompletion_accepts_source_provider_id_without_source_external_id()
    {
        var task = new TaskItem
        {
            Id = "local-wrapper",
            IntegrationId = IntegrationIds.Local,
            SourceIntegrationId = IntegrationIds.Todoist,
            SourceProviderTaskId = "todoist-task",
        };

        var completion = ProviderWriteBackPlanner.CreateCompletion(task, completed: true, Now);

        Assert.NotNull(completion);
        Assert.Equal("todoist-task", completion.ProviderTaskId);
    }

    [Fact]
    public void CreateCompletion_builds_microsoft_list_and_task_id()
    {
        var task = new TaskItem
        {
            Id = "microsoft-local-id",
            IntegrationId = IntegrationIds.MicrosoftToDo,
            ExternalId = "task-id",
            ProjectId = "mstodo_list-id",
        };

        var completion = ProviderWriteBackPlanner.CreateCompletion(task, completed: true, Now);

        Assert.NotNull(completion);
        Assert.Equal("list-id|task-id", completion.ProviderTaskId);
    }

    [Fact]
    public void CreateTodoistDateUpdate_queues_changed_date_for_adopted_task()
    {
        var original = new TaskItem
        {
            Id = "local-wrapper",
            IntegrationId = IntegrationIds.Local,
            SourceIntegrationId = IntegrationIds.Todoist,
            SourceExternalId = "todoist-task",
            SourceProviderTaskId = "todoist-task",
            PlannedOn = new DateOnly(2026, 8, 14),
        };
        var updated = original with { PlannedOn = new DateOnly(2026, 8, 15) };

        var pending = ProviderWriteBackPlanner.CreateTodoistDateUpdate(original, updated, Now);

        Assert.NotNull(pending);
        Assert.Equal("todoist-task", pending.ProviderTaskId);
        Assert.Equal(new DateOnly(2026, 8, 15), pending.PlannedOn);
    }

    [Fact]
    public void CreateTodoistDateUpdate_accepts_source_provider_id_without_source_external_id()
    {
        var original = new TaskItem
        {
            Id = "local-wrapper",
            IntegrationId = IntegrationIds.Local,
            SourceIntegrationId = IntegrationIds.Todoist,
            SourceProviderTaskId = "todoist-task",
            PlannedOn = new DateOnly(2026, 8, 14),
        };

        var pending = ProviderWriteBackPlanner.CreateTodoistDateUpdate(
            original,
            original with { PlannedOn = new DateOnly(2026, 8, 15) },
            Now);

        Assert.NotNull(pending);
        Assert.Equal("todoist-task", pending.ProviderTaskId);
    }

    [Fact]
    public void CreateTodoistDateUpdate_queues_date_clear_and_clears_exact_time()
    {
        var original = new TaskItem
        {
            Id = "local-wrapper",
            IntegrationId = IntegrationIds.Local,
            SourceIntegrationId = IntegrationIds.Todoist,
            SourceExternalId = "todoist-task",
            SourceProviderTaskId = "todoist-task",
            PlannedOn = new DateOnly(2026, 8, 14),
            PlannedAt = Now,
        };
        var updated = original with { PlannedOn = null, PlannedAt = null };

        var pending = ProviderWriteBackPlanner.CreateTodoistDateUpdate(original, updated, Now);

        Assert.NotNull(pending);
        Assert.Null(pending.PlannedOn);
        Assert.Null(pending.PlannedAt);
    }

    [Fact]
    public void CreateTodoistDateUpdate_ignores_recurring_and_unchanged_tasks()
    {
        var original = new TaskItem
        {
            Id = "local-wrapper",
            IntegrationId = IntegrationIds.Local,
            SourceIntegrationId = IntegrationIds.Todoist,
            SourceExternalId = "todoist-task",
            SourceProviderTaskId = "todoist-task",
            PlannedOn = new DateOnly(2026, 8, 14),
        };

        Assert.Null(ProviderWriteBackPlanner.CreateTodoistDateUpdate(original, original, Now));
        Assert.Null(ProviderWriteBackPlanner.CreateTodoistDateUpdate(
            original with { RecurrenceRule = "every day" },
            original with { RecurrenceRule = "every day", PlannedOn = new DateOnly(2026, 8, 15) },
            Now));
    }

    [Fact]
    public void PreserveExactTime_keeps_only_an_unchanged_matching_date()
    {
        var existingTime = new DateTimeOffset(2026, 8, 14, 9, 30, 0, TimeSpan.Zero);

        Assert.Equal(existingTime, ProviderWriteBackPlanner.PreserveExactTime(
            new DateOnly(2026, 8, 14), new DateOnly(2026, 8, 14), existingTime));
        Assert.Null(ProviderWriteBackPlanner.PreserveExactTime(
            new DateOnly(2026, 8, 15), new DateOnly(2026, 8, 14), existingTime));
        Assert.Null(ProviderWriteBackPlanner.PreserveExactTime(
            null, new DateOnly(2026, 8, 14), existingTime));
    }
}
