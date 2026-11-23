using System.Text.Json;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task Reminder_can_be_set_shown_and_cleared_but_requires_an_absolute_instant()
    {
        var store = await CreateStoreAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "task_reminder", Title = "Synthetic reminder" });
        var set = await RunAsync("task", "update", "task_reminder", "--reminder", "2027-07-01T09:30:00-07:00", "--format", "json");
        AssertSuccess(set);
        using var json = JsonDocument.Parse(set.Stdout);
        Assert.Equal(DateTimeOffset.Parse("2027-07-01T16:30:00Z"), json.RootElement.GetProperty("data").GetProperty("reminderAt").GetDateTimeOffset());
        var shown = await RunAsync("task", "show", "task_reminder");
        AssertSuccess(shown);
        Assert.Contains("reminder_at  2027-07-01T16:30:00", shown.Stdout);
        var added = await RunAsync("task", "add", "Reminder on creation", "--reminder", "2027-07-01T09:30:00Z", "--format", "json");
        AssertSuccess(added);
        using var created = JsonDocument.Parse(added.Stdout);
        Assert.Equal(DateTimeOffset.Parse("2027-07-01T09:30:00Z"), created.RootElement.GetProperty("data").GetProperty("reminderAt").GetDateTimeOffset());
        Assert.NotEqual(0, (await RunAsync("task", "update", "task_reminder", "--reminder", "2027-07-01")).ExitCode);
        Assert.NotEqual(0, (await RunAsync("task", "update", "task_reminder", "--reminder", "bad-date")).ExitCode);
        Assert.NotEqual(0, (await RunAsync("task", "update", "task_reminder", "--reminder", "2027-07-01T10:00:00Z", "--clear-reminder")).ExitCode);
        AssertSuccess(await RunAsync("task", "update", "task_reminder", "--clear-reminder"));
        Assert.Null((await store.GetTaskAsync("task_reminder"))!.ReminderAt);
    }
}
