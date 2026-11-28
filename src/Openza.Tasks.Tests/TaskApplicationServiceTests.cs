using Openza.Tasks.Application.Tasks;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed class TaskApplicationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-application-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Update_omitting_project_preserves_archived_project()
    {
        var (store, service) = await CreateAsync();
        await store.UpsertSpaceAsync(new SpaceItem
        {
            Id = "space_archived",
            Name = "Archived space",
            IsArchived = true,
        });
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_archived",
            SpaceId = "space_archived",
            Name = "Archived",
            IsArchived = true,
        });
        var created = await service.CreateTaskAsync(new CreateTaskRequest { Title = "Keep project" });
        await store.UpsertTaskAsync(created with { SpaceId = "space_archived", ProjectId = "project_archived" });
        var original = (await service.GetTaskAsync(created.Id))!;

        var updated = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = original.Id,
            ExpectedRevision = original.Revision,
            Priority = OptionalValue<int>.Set(2),
        });

        Assert.Equal("project_archived", updated.ProjectId);
        Assert.Equal("space_archived", updated.SpaceId);
    }

    [Fact]
    public async Task Labels_resolve_exact_id_before_name_and_reject_ambiguous_names()
    {
        var (store, service) = await CreateAsync();
        await InsertLabelsWithDuplicateNamesAsync(store.DatabasePath);

        var exact = await service.CreateTaskAsync(new CreateTaskRequest { Title = "Exact", Labels = ["label_two"] });
        Assert.Equal("label_two", Assert.Single(exact.Labels).Id);

        var error = await Assert.ThrowsAsync<ReferenceResolutionException>(() =>
            service.CreateTaskAsync(new CreateTaskRequest { Title = "Ambiguous", Labels = ["focus"] }));
        Assert.Contains("Use its exact id", error.Message);
    }

    [Fact]
    public async Task Unknown_label_name_intentionally_creates_local_label()
    {
        var (_, service) = await CreateAsync();

        var created = await service.CreateTaskAsync(new CreateTaskRequest { Title = "New label", Labels = ["Brand new"] });

        var label = Assert.Single(created.Labels);
        Assert.StartsWith("label_", label.Id);
        Assert.Equal("Brand new", label.Name);
        Assert.Equal(IntegrationIds.Local, label.IntegrationId);
    }

    [Fact]
    public async Task Update_completion_is_atomic_and_queues_provider_write_back()
    {
        var (store, service) = await CreateAsync();
        var created = await service.CreateTaskAsync(new CreateTaskRequest { Title = "Provider task" });
        await store.UpsertTaskAsync(created with
        {
            SourceIntegrationId = IntegrationIds.Todoist,
            SourceExternalId = "remote-1",
            SourceProviderTaskId = "remote-1",
        });
        var original = (await service.GetTaskAsync(created.Id))!;

        var completed = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = original.Id,
            ExpectedRevision = original.Revision,
            Title = OptionalValue<string?>.Set("Provider task edited"),
            Completed = OptionalValue<bool>.Set(true),
        });

        Assert.True(completed.IsCompleted);
        Assert.Equal("Provider task edited", completed.Title);
        Assert.Equal(original.Revision + 1, completed.Revision);
        var pending = Assert.Single(await store.GetPendingCompletionsAsync(IntegrationIds.Todoist));
        Assert.True(pending.Completed);
        Assert.Equal("remote-1", pending.ProviderTaskId);
    }

    [Fact]
    public async Task Delete_rejects_stale_revision_without_deleting()
    {
        var (_, service) = await CreateAsync();
        var created = await service.CreateTaskAsync(new CreateTaskRequest { Title = "Keep on conflict" });
        _ = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.Id,
            ExpectedRevision = created.Revision,
            Title = OptionalValue<string?>.Set("Changed"),
        });

        await Assert.ThrowsAsync<TaskConflictException>(() => service.DeleteTaskAsync(created.Id, created.Revision));
        Assert.NotNull(await service.GetTaskAsync(created.Id));
    }

    [Fact]
    public async Task Schedule_edits_recalculate_duration_and_keep_planned_date_unchanged()
    {
        var (_, service) = await CreateAsync();
        var initialStart = new DateTimeOffset(2030, 1, 17, 8, 0, 0, TimeSpan.Zero);
        var created = await service.CreateTaskAsync(new CreateTaskRequest
        {
            Title = "Write draft",
            Status = TaskWorkflowStatus.Next,
            PlannedOn = new DateOnly(2030, 1, 17),
            ScheduledStart = initialStart,
            DurationMinutes = 30,
        });
        var start = new DateTimeOffset(2030, 1, 17, 9, 0, 0, TimeSpan.FromHours(5.5));

        var scheduled = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.Id,
            ScheduledStart = OptionalValue<DateTimeOffset?>.Set(start),
            ScheduledEnd = OptionalValue<DateTimeOffset?>.Set(start.AddHours(1)),
        });
        Assert.Equal(start, scheduled.ScheduledStart);
        Assert.Equal(start.AddHours(1), scheduled.ScheduledEnd);
        Assert.Equal(TaskWorkflowStatus.Next, scheduled.WorkflowStatus);
        Assert.Equal(created.PlannedOn, scheduled.PlannedOn);
        Assert.Equal(60, scheduled.DurationMinutes);

        var renamed = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.Id,
            Title = OptionalValue<string?>.Set("Write first draft"),
        });
        Assert.Equal(start, renamed.ScheduledStart);
        Assert.Equal(start.AddHours(1), renamed.ScheduledEnd);
        Assert.Equal(60, renamed.DurationMinutes);

        var cleared = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.Id,
            ScheduledStart = OptionalValue<DateTimeOffset?>.Set(null),
            ScheduledEnd = OptionalValue<DateTimeOffset?>.Set(null),
        });
        Assert.Null(cleared.ScheduledStart);
        Assert.Null(cleared.ScheduledEnd);
        Assert.Null(cleared.DurationMinutes);
        Assert.Equal(TaskWorkflowStatus.Next, cleared.WorkflowStatus);
        Assert.Equal(created.PlannedOn, cleared.PlannedOn);
    }

    [Fact]
    public async Task Schedule_edits_reject_an_end_without_a_later_start()
    {
        var (_, service) = await CreateAsync();
        var created = await service.CreateTaskAsync(new CreateTaskRequest { Title = "Invalid block" });
        var start = new DateTimeOffset(2030, 1, 17, 9, 0, 0, TimeSpan.Zero);

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.Id,
            ScheduledEnd = OptionalValue<DateTimeOffset?>.Set(start),
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.Id,
            ScheduledStart = OptionalValue<DateTimeOffset?>.Set(start),
            ScheduledEnd = OptionalValue<DateTimeOffset?>.Set(start),
        }));
        var unchanged = (await service.GetTaskAsync(created.Id))!;
        Assert.Null(unchanged.ScheduledStart);
        Assert.Null(unchanged.ScheduledEnd);
    }

    private async Task<(SqliteTaskStore Store, TaskApplicationService Service)> CreateAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, $"{Guid.NewGuid():N}.db"));
        var service = new TaskApplicationService(store);
        await service.InitializeAsync();
        return (store, service);
    }

    internal static async Task InsertLabelsWithDuplicateNamesAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO labels (id, integration_id, name) VALUES ('label_one', 'openza_tasks', 'Focus');
            INSERT INTO labels (id, integration_id, name) VALUES ('label_two', 'openza_tasks', 'FOCUS');
            """;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
