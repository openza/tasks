using Microsoft.Data.Sqlite;
using System.Text.Json;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class TaskPlanningPreservationTests : IDisposable
{
    private const string InstanceId = "instance_00000000000000000000000000000001";
    private const string DeviceId = "device_00000000000000000000000000000001";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-planning-preservation", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Planned = DateTimeOffset.Parse("2026-06-17T04:00:00Z");
    private static readonly DateTimeOffset Deadline = Planned.AddDays(3);

    [Theory]
    [InlineData("title")]
    [InlineData("notes")]
    [InlineData("status")]
    public async Task Unrelated_local_edit_preserves_raw_exact_only_dates_and_rich_planning(string field)
    {
        var (store, original) = await CreateAsync();
        var request = new UpdateTaskRequest { TaskId = original.Id, ExpectedRevision = original.Revision };
        request = field switch
        {
            "title" => request with { Title = OptionalValue<string?>.Set("Edited") },
            "notes" => request with { Notes = OptionalValue<string?>.Set("Edited notes") },
            _ => request with { Status = OptionalValue<TaskWorkflowStatus>.Set(TaskWorkflowStatus.Next) },
        };
        var updated = await new TaskApplicationService(store).UpdateTaskAsync(request);
        AssertPlanning(original, updated);
        Assert.Equal(Planned, updated.PlannedAt);
        Assert.Equal(Deadline, updated.DeadlineAt);
        // Ordinary local writes normalize the day/instant pair, without losing the instant.
        Assert.Equal(TaskDateValues.FromDateTimeOffset(Planned), updated.PlannedOn);
        Assert.Equal(TaskDateValues.FromDateTimeOffset(Deadline), updated.DeadlineOn);
        Assert.Equal(original.Revision + 1, updated.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_date_clear_or_replacement_still_removes_exact_time(bool replace)
    {
        var (store, original) = await CreateAsync();
        DateOnly? day = replace ? new DateOnly(2026, 7, 1) : null;
        var updated = await new TaskApplicationService(store).UpdateTaskAsync(new()
        {
            TaskId = original.Id, ExpectedRevision = original.Revision,
            PlannedOn = OptionalValue<DateOnly?>.Set(day), DeadlineOn = OptionalValue<DateOnly?>.Set(day),
        });
        Assert.Null(updated.PlannedAt); Assert.Null(updated.DeadlineAt);
        Assert.Equal(day, updated.PlannedOn); Assert.Equal(day, updated.DeadlineOn);
        Assert.Equal(original.RecurrenceRule, updated.RecurrenceRule);
    }

    [Fact]
    public async Task Explicit_same_day_edit_of_normalized_pair_preserves_exact_time()
    {
        var (store, original) = await CreateAsync(rawExactOnly: false);
        var updated = await new TaskApplicationService(store).UpdateTaskAsync(new()
        {
            TaskId = original.Id, ExpectedRevision = original.Revision,
            PlannedOn = OptionalValue<DateOnly?>.Set(original.PlannedOn),
            DeadlineOn = OptionalValue<DateOnly?>.Set(original.DeadlineOn),
        });
        AssertPlanning(original, updated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Clearing_one_date_preserves_the_other_unspecified_instant(bool clearPlanned)
    {
        var (store, original) = await CreateAsync();
        var updated = await new TaskApplicationService(store).UpdateTaskAsync(new()
        {
            TaskId = original.Id, ExpectedRevision = original.Revision,
            PlannedOn = clearPlanned ? OptionalValue<DateOnly?>.Set(null) : default,
            DeadlineOn = clearPlanned ? default : OptionalValue<DateOnly?>.Set(null),
        });
        Assert.Equal(clearPlanned ? null : Planned, updated.PlannedAt);
        Assert.Equal(clearPlanned ? Deadline : null, updated.DeadlineAt);
        Assert.Equal(original.ScheduledStart, updated.ScheduledStart);
        Assert.Equal(original.RecurrenceRule, updated.RecurrenceRule);
    }

    [Fact]
    public async Task Unrelated_edit_of_exact_only_provider_wrapper_does_not_queue_a_date_clear()
    {
        var (store, original) = await CreateAsync();
        await store.UpsertTaskAsync(original with
        {
            SourceIntegrationId = IntegrationIds.Todoist, SourceExternalId = "synthetic-source",
            SourceProviderTaskId = "synthetic-source", RecurrenceRule = null,
        });
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tasks SET planned_on = NULL, deadline_on = NULL WHERE id = @id";
        command.Parameters.AddWithValue("@id", original.Id);
        await command.ExecuteNonQueryAsync();
        original = (await store.GetTaskAsync(original.Id))!;
        var updated = await new TaskApplicationService(store).UpdateTaskAsync(new()
        { TaskId = original.Id, ExpectedRevision = original.Revision, Title = OptionalValue<string?>.Set("Edited wrapper") });
        AssertPlanning(original, updated);
        Assert.Empty(await store.GetPendingTaskDateUpdatesAsync(IntegrationIds.Todoist));
    }

    [Fact]
    public async Task Completion_and_reopen_retain_rule_and_session_without_creating_an_occurrence()
    {
        var (store, original) = await CreateAsync();
        var service = new TaskApplicationService(store);
        var completed = await service.UpdateTaskAsync(new()
        { TaskId = original.Id, ExpectedRevision = original.Revision, Completed = OptionalValue<bool>.Set(true) });
        Assert.True(completed.IsCompleted); Assert.NotNull(completed.CompletedAt);
        AssertPlanning(original, completed);
        var reopened = await service.UpdateTaskAsync(new()
        { TaskId = original.Id, ExpectedRevision = completed.Revision, Completed = OptionalValue<bool>.Set(false) });
        Assert.False(reopened.IsCompleted); Assert.Null(reopened.CompletedAt);
        AssertPlanning(original, reopened);
        Assert.Equal(original.Id, Assert.Single(await store.GetTasksAsync(new() { Kind = TaskListKind.All })).Id);
    }

    [Fact]
    public async Task Cloud_title_edit_keeps_exact_only_pair_and_emits_no_planning_mutation()
    {
        var (store, original) = await CreateAsync(cloud: true);
        var connection = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri("https://example.invalid/"), InstanceId = InstanceId, DeviceId = DeviceId,
            ArtifactVersion = "1.2.3", ConnectedAt = DateTimeOffset.UtcNow,
        };
        var service = new TaskApplicationService(store, () => connection);
        var before = (await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId)).Count;
        var updated = await service.UpdateTaskAsync(new()
        { TaskId = original.Id, ExpectedRevision = original.Revision, Title = OptionalValue<string?>.Set("Cloud title edited") });
        AssertPlanning(original, updated);
        Assert.Null(updated.PlannedOn); Assert.Null(updated.DeadlineOn);
        var emitted = (await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId)).Skip(before).ToArray();
        var mutation = Assert.Single(emitted);
        Assert.Equal("task.updateOpenzaFields", mutation.Operation);
        using var json = JsonDocument.Parse(mutation.CanonicalJson);
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal(new[] { "title" }, payload.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Cloud title edited", payload.GetProperty("title").GetString());
    }

    private async Task<(SqliteTaskStore Store, TaskItem Task)> CreateAsync(bool rawExactOnly = true, bool cloud = false)
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        var task = new TaskItem
        {
            Id = "task_synthetic_planning", Title = "Synthetic recurring task", SpaceId = SpaceIds.Default,
            PlannedAt = Planned, DeadlineAt = Deadline, ScheduledStart = Planned.AddHours(1),
            ScheduledEnd = Planned.AddHours(2), DurationMinutes = 60, RecurrenceRule = "opaque local rule",
        };
        if (cloud)
        {
            await store.CreateTaskWithPersonalCloudIntentAsync(task, InstanceId, DeviceId);
            await store.UpdateTaskPlannedAtWithPersonalCloudIntentAsync(task.Id, Planned, 1, InstanceId, DeviceId);
        }
        else await store.UpsertTaskAsync(task);
        if (rawExactOnly)
        {
            // Synthetic persisted fixture: pull/import may store instants without date-only columns.
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tasks SET planned_on = NULL, deadline_on = NULL, deadline_at = @deadline WHERE id = @id";
            command.Parameters.AddWithValue("@deadline", Deadline.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@id", task.Id);
            await command.ExecuteNonQueryAsync();
        }
        return (store, (await store.GetTaskAsync(task.Id))!);
    }

    private static void AssertPlanning(TaskItem original, TaskItem actual)
    {
        Assert.Equal(original.PlannedAt, actual.PlannedAt);
        Assert.Equal(original.DeadlineAt, actual.DeadlineAt);
        Assert.Equal(original.ScheduledStart, actual.ScheduledStart);
        Assert.Equal(original.ScheduledEnd, actual.ScheduledEnd);
        Assert.Equal(original.DurationMinutes, actual.DurationMinutes);
        Assert.Equal(original.RecurrenceRule, actual.RecurrenceRule);
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
