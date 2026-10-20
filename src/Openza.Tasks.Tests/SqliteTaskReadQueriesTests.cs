using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed class SqliteTaskReadQueriesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-read-affinity", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Hydration_and_counts_observe_the_callers_uncommitted_connection_without_committing_it()
    {
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new() { Id = "task", Title = "Original", Labels = [new() { Id = "label", Name = "Original label" }] });
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE; UPDATE tasks SET title='Uncommitted',workflow_status='next' WHERE id='task'; UPDATE labels SET name='Uncommitted label' WHERE id='label';";
        await command.ExecuteNonQueryAsync();
        try
        {
            var task = Assert.Single(await SqliteTaskReadQueries.ReadTasksAsync(connection, CancellationToken.None));
            Assert.Equal("Uncommitted", task.Title);
            Assert.Equal("Uncommitted label", Assert.Single(task.Labels).Name);
            var counts = await SqliteTaskReadQueries.ReadCountsAsync(connection, null, new(2026, 10, 18), CancellationToken.None);
            Assert.Equal(1, counts.NextActions);
            Assert.Equal(0, counts.Inbox);
        }
        finally { command.CommandText = "ROLLBACK"; await command.ExecuteNonQueryAsync(); }
        var original = (await store.GetTaskAsync("task"))!;
        Assert.Equal("Original", original.Title);
        Assert.Equal("Original label", Assert.Single(original.Labels).Name);
        Assert.Equal(TaskWorkflowStatus.Inbox, original.WorkflowStatus);
    }

    [Fact]
    public async Task Read_only_legacy_revision_fallback_does_not_require_or_create_a_revision_column()
    {
        var store = new SqliteTaskStore(Path.Combine(_directory, "legacy.db"));
        await store.InitializeAsync();
        var exact = DateTimeOffset.Parse("2026-10-18T10:11:12.345Z");
        await store.UpsertTaskAsync(new() { Id = "task", Title = "Legacy", PlannedAt = exact, Notes = null });
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE tasks DROP COLUMN revision; PRAGMA user_version=5;";
            await command.ExecuteNonQueryAsync();
        }
        var readOnly = new SqliteTaskStore(store.DatabasePath, readOnly: true);
        await readOnly.InitializeAsync();
        var task = (await readOnly.GetTaskAsync("task"))!;
        Assert.Equal(0, task.Revision);
        Assert.Equal(exact, task.PlannedAt);
        Assert.Null(task.Notes);
        Assert.Empty(task.Labels);
        Assert.Null(await readOnly.GetTaskAsync("missing"));
    }

    [Theory]
    [InlineData("1760000000", 1760000000000L)]
    [InlineData("1760000000123", 1760000000123L)]
    [InlineData("1760000000.123", 1760000000123L)]
    [InlineData("NULL", null)]
    public async Task Read_instants_preserve_seconds_integer_milliseconds_fractional_seconds_and_null(string sql, long? milliseconds)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT {sql}";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(milliseconds, SqliteReadValues.ReadDate(reader, 0)?.ToUnixTimeMilliseconds());
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
