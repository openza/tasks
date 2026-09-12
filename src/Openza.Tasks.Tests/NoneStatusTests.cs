using Microsoft.Data.Sqlite;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed class NoneStatusTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-none-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "tasks.db");

    [Theory]
    [InlineData(TaskListKind.Open, false)]
    [InlineData(TaskListKind.Open, true)]
    [InlineData(TaskListKind.Today, false)]
    public void None_and_native_source_are_omitted_from_row_metadata(TaskListKind view, bool isProject)
    {
        var task = new TaskItem { Id = "task", Title = "Task", WorkflowStatus = TaskWorkflowStatus.None };
        var row = new TaskListItemViewModel(task, isProject ? "Project" : null, view, isProject);
        Assert.Empty(row.StatusText);
        Assert.Empty(row.MetadataText);
        Assert.Equal("None", row.Status);
        Assert.Equal("Openza Tasks", row.SourceText);
        foreach (var source in new[] { IntegrationIds.Todoist, IntegrationIds.MicrosoftToDo, IntegrationIds.GitHub })
        {
            var providerRow = new TaskListItemViewModel(task with { SourceIntegrationId = source }, null, view, isProject);
            Assert.Equal(IntegrationIds.DisplayName(source), providerRow.MetadataText);
        }
        var completedRow = new TaskListItemViewModel(task with { CompletionState = TaskCompletionState.Completed }, null, TaskListKind.Completed);
        Assert.Empty(completedRow.StatusText);
    }

    [Fact]
    public async Task Failed_legacy_conversion_rolls_back_columns_and_can_retry()
    {
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE tasks (id TEXT PRIMARY KEY, title TEXT, status TEXT, created_at INTEGER);
                INSERT INTO tasks VALUES ('task', 'Legacy next action', 'next', 1710000000);
                CREATE TRIGGER fail_conversion BEFORE UPDATE ON tasks
                BEGIN SELECT RAISE(ABORT, 'Simulated conversion failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var store = new SqliteTaskStore(DatabasePath);
        var failure = await Assert.ThrowsAsync<SqliteException>(() => store.InitializeAsync());
        Assert.Contains("Simulated conversion failure", failure.Message);
        await using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('tasks') WHERE name IN ('workflow_status', 'completion_state')";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "DROP TRIGGER fail_conversion";
            await command.ExecuteNonQueryAsync();
        }
        await store.InitializeAsync();
        Assert.Equal(TaskWorkflowStatus.Next, (await store.GetTaskAsync("task"))!.WorkflowStatus);
    }

    [Theory]
    [InlineData("none", TaskWorkflowStatus.Inbox, TaskCompletionState.Open)]
    [InlineData("next", TaskWorkflowStatus.Next, TaskCompletionState.Open)]
    [InlineData("done", TaskWorkflowStatus.None, TaskCompletionState.Completed)]
    [InlineData("cancelled", TaskWorkflowStatus.None, TaskCompletionState.Cancelled)]
    public async Task Legacy_conversion_runs_once_and_modern_edits_survive_restart(
        string legacyStatus, TaskWorkflowStatus expectedWorkflow, TaskCompletionState expectedCompletion)
    {
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE tasks (id TEXT PRIMARY KEY, title TEXT NOT NULL, status TEXT, created_at INTEGER);
                INSERT INTO tasks VALUES ('task', 'Legacy', @status, 1710000000);
                """;
            command.Parameters.AddWithValue("@status", legacyStatus);
            await command.ExecuteNonQueryAsync();
        }
        var store = new SqliteTaskStore(DatabasePath);
        await store.InitializeAsync();
        var converted = (await store.GetTaskAsync("task"))!;
        Assert.Equal(expectedWorkflow, converted.WorkflowStatus);
        Assert.Equal(expectedCompletion, converted.CompletionState);
        await store.UpsertTaskAsync(converted with { WorkflowStatus = TaskWorkflowStatus.None, CompletionState = TaskCompletionState.Open });
        await store.InitializeAsync();
        await store.InitializeAsync();
        var reloaded = (await store.GetTaskAsync("task"))!;
        Assert.Equal(TaskWorkflowStatus.None, reloaded.WorkflowStatus);
        Assert.Equal(TaskCompletionState.Open, reloaded.CompletionState);
    }

    [Theory]
    [InlineData(0, TaskWorkflowStatus.Inbox, false)]
    [InlineData(1, TaskWorkflowStatus.Next, false)]
    [InlineData(2, TaskWorkflowStatus.Waiting, false)]
    [InlineData(3, TaskWorkflowStatus.Someday, false)]
    [InlineData(4, TaskWorkflowStatus.None, true)]
    [InlineData(5, TaskWorkflowStatus.None, false)]
    public async Task Desktop_creation_keeps_existing_indices_and_supports_none(int index, TaskWorkflowStatus status, bool completed)
    {
        var store = await CreateStoreAsync();
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await vm.CreateTaskAsync(new AddTaskDraft("Task", "", null, index, 2, null, "", false));
        var task = Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
        Assert.Equal(status, task.WorkflowStatus);
        Assert.Equal(completed, task.IsCompleted);
    }

    [Fact]
    public async Task Desktop_editing_none_preserves_status_and_completion_round_trip()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "task", Title = "Task", WorkflowStatus = TaskWorkflowStatus.None });
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await vm.SelectNavigationAsync(vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await vm.SelectTaskAsync(Assert.Single(vm.Tasks));
        Assert.Equal(5, vm.DetailStatusIndex);
        vm.DetailTitle = "Edited";
        vm.DetailNotes = "Notes";
        Assert.True(await vm.SaveSelectedAsync());
        Assert.Equal(TaskWorkflowStatus.None, (await store.GetTaskAsync("task"))!.WorkflowStatus);
        await store.CompleteTaskAsync("task");
        await store.ReopenTaskAsync("task");
        Assert.Equal(TaskWorkflowStatus.None, (await store.GetTaskAsync("task"))!.WorkflowStatus);
    }

    [Fact]
    public async Task Project_review_flow_preserves_membership_dates_and_provider_queues()
    {
        var store = await CreateStoreAsync();
        await store.UpsertProjectAsync(new ProjectItem { Id = "project", Name = "Project" });
        var service = new TaskApplicationService(store);
        var task = await service.CreateTaskAsync(new CreateTaskRequest { Title = "Task", Status = TaskWorkflowStatus.Next });
        task = await service.UpdateTaskAsync(new UpdateTaskRequest { TaskId = task.Id, Project = OptionalValue<string?>.Set("project") });
        Assert.Equal(TaskWorkflowStatus.Next, task.WorkflowStatus);
        task = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = task.Id, Status = OptionalValue<TaskWorkflowStatus>.Set(TaskWorkflowStatus.None),
            PlannedOn = OptionalValue<DateOnly?>.Set(DateOnly.FromDateTime(DateTime.Now)),
        });
        Assert.Equal("project", task.ProjectId);
        Assert.Empty(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.NextActions }));
        Assert.Empty(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Inbox }));
        Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Open, ProjectId = "project" }));
        Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Today }));
        var group = Assert.Single(TaskGroupBuilder.GetAssignments(task, null, TaskGroupMode.Status));
        Assert.Equal("None", group.Title);
        Assert.Equal("status:none", group.Key);
        Assert.Empty(await store.GetPendingCompletionsAsync(IntegrationIds.Todoist));
        Assert.Empty(await store.GetPendingTaskDateUpdatesAsync(IntegrationIds.Todoist));
        await service.UpdateTaskAsync(new UpdateTaskRequest { TaskId = task.Id, Status = OptionalValue<TaskWorkflowStatus>.Set(TaskWorkflowStatus.Next) });
        Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.NextActions }));
    }

    [Fact]
    public async Task Provider_refresh_preserves_none_and_status_changes_do_not_queue_remote_updates()
    {
        var store = await CreateStoreAsync();
        var source = new ProviderSourceItem
        {
            Id = "source", IntegrationId = IntegrationIds.Todoist, ProviderConnectionId = "todoist_default",
            ExternalId = "remote", ProviderTaskId = "remote", Title = "Provider task",
            CompletionState = TaskCompletionState.Open,
        };
        await store.UpsertProviderSourceItemAsync(source);
        var task = (await store.AdoptProviderSourceItemAsync(source.Id))!;
        var service = new TaskApplicationService(store);
        await service.UpdateTaskAsync(new UpdateTaskRequest { TaskId = task.Id, Status = OptionalValue<TaskWorkflowStatus>.Set(TaskWorkflowStatus.None) });
        Assert.Empty(await store.GetPendingCompletionsAsync(IntegrationIds.Todoist));
        Assert.Empty(await store.GetPendingTaskDateUpdatesAsync(IntegrationIds.Todoist));
        await store.UpsertProviderSourceItemAsync(source with { Title = "Refreshed" });
        Assert.Equal(TaskWorkflowStatus.None, (await store.GetTaskAsync(task.Id))!.WorkflowStatus);

        var childSource = source with { Id = "child-source", ExternalId = "remote-child", ProviderTaskId = "remote-child", ParentExternalId = source.ExternalId };
        await store.UpsertProviderSourceItemAsync(childSource);
        var child = (await store.AdoptProviderSourceItemAsync(childSource.Id))!;
        Assert.Equal(task.Id, child.ParentId);
        Assert.Equal(TaskWorkflowStatus.None, child.WorkflowStatus);
        await store.UpsertProviderSourceItemAsync(childSource with { Title = "Refreshed child" });
        await store.InitializeAsync();
        Assert.Equal(TaskWorkflowStatus.None, (await store.GetTaskAsync(child.Id))!.WorkflowStatus);
    }

    [Theory]
    [InlineData("next", "none", "open", TaskWorkflowStatus.None, TaskCompletionState.Open)]
    [InlineData("completed", "inbox", "open", TaskWorkflowStatus.Inbox, TaskCompletionState.Open)]
    [InlineData("none", "waiting", "cancelled", TaskWorkflowStatus.Waiting, TaskCompletionState.Cancelled)]
    [InlineData("next", null, null, TaskWorkflowStatus.Next, TaskCompletionState.Open)]
    [InlineData("done", "", "", TaskWorkflowStatus.None, TaskCompletionState.Completed)]
    public async Task Mixed_schema_preserves_modern_values_and_repairs_only_missing_values(
        string legacy, string? workflow, string? completion, TaskWorkflowStatus expectedWorkflow, TaskCompletionState expectedCompletion)
    {
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE tasks (id TEXT PRIMARY KEY, title TEXT, status TEXT, workflow_status TEXT, completion_state TEXT, created_at INTEGER);
                INSERT INTO tasks VALUES ('task', 'Mixed schema', @legacy, @workflow, @completion, 1710000000);
                """;
            command.Parameters.AddWithValue("@legacy", legacy);
            command.Parameters.AddWithValue("@workflow", (object?)workflow ?? DBNull.Value);
            command.Parameters.AddWithValue("@completion", (object?)completion ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
        var store = new SqliteTaskStore(DatabasePath);
        await store.InitializeAsync();
        await store.InitializeAsync();
        var task = (await store.GetTaskAsync("task"))!;
        Assert.Equal(expectedWorkflow, task.WorkflowStatus);
        Assert.Equal(expectedCompletion, task.CompletionState);
    }

    [Fact]
    public async Task Backup_restore_preserves_none_after_initialization()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "task", Title = "Task", WorkflowStatus = TaskWorkflowStatus.None });
        var backups = new BackupService(DatabasePath, Path.Combine(_directory, "backups"));
        var backup = await backups.CreateBackupAsync();
        await store.UpsertTaskAsync((await store.GetTaskAsync("task"))! with { WorkflowStatus = TaskWorkflowStatus.Next });
        await backups.RestoreBackupAsync(backup);
        await store.InitializeAsync();
        Assert.Equal(TaskWorkflowStatus.None, (await store.GetTaskAsync("task"))!.WorkflowStatus);
    }

    private async Task<SqliteTaskStore> CreateStoreAsync()
    {
        var store = new SqliteTaskStore(DatabasePath);
        await store.InitializeAsync();
        return store;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
