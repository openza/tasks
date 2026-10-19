using System.Text.Json;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed class WindowsConvertedViewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-converted-view", Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> SortCases =>
        from mode in Enum.GetValues<TaskSortMode>()
        from direction in Enum.GetValues<TaskSortDirection>()
        select new object[] { mode, direction };

    public static IEnumerable<object[]> GroupCases =>
        Enum.GetValues<TaskGroupMode>().Select(mode => new object[] { mode });

    [Theory]
    [MemberData(nameof(SortCases))]
    public async Task Converted_sort_reaches_actual_Avalonia_ordering(TaskSortMode mode, TaskSortDirection direction)
    {
        var (store, vm) = await CreateAsync(mode, direction, TaskGroupMode.None);
        var expected = await store.GetTasksAsync(new()
        { SpaceId = SpaceIds.Default, Kind = TaskListKind.Open, SortMode = mode, SortDirection = direction });
        await vm.ApplyListOptionsAsync();
        Assert.Equal(expected.Select(task => task.Id), vm.Tasks.Select(item => item.Task.Id));
        Assert.DoesNotContain(vm.TaskEntries, entry => entry.IsHeader);
    }

    [Theory]
    [MemberData(nameof(GroupCases))]
    public async Task Converted_group_reaches_actual_Avalonia_group_headers(TaskGroupMode mode)
    {
        var (store, vm) = await CreateAsync(TaskSortMode.PriorityThenDate, TaskSortDirection.Ascending, mode);
        var projects = (await store.GetProjectsAsync()).ToDictionary(project => project.Id);
        var rows = await store.GetTasksAsync(new() { SpaceId = SpaceIds.Default, Kind = TaskListKind.Open });
        var expected = rows.SelectMany(task => TaskGroupBuilder.GetAssignments(task,
                task.ProjectId is null ? null : projects[task.ProjectId], mode))
            .Select(group => group.Key).Distinct().Order(StringComparer.Ordinal);
        await vm.ApplyListOptionsAsync();
        Assert.Equal(expected, vm.TaskEntries.Where(entry => entry.IsHeader).Select(entry => entry.GroupKey).Order(StringComparer.Ordinal));
        Assert.Equal(rows.Select(task => task.Id).Order(), vm.TaskEntries.Where(entry => entry.IsTask).Select(entry => entry.Task!.Task.Id).Distinct().Order());
    }

    [Theory]
    [InlineData("All", true)]
    [InlineData("Planned", false)]
    [InlineData("Deadline", false)]
    [InlineData("Scheduled", false)]
    [InlineData("all", false)]
    public async Task Synthetic_persisted_DateScope_is_preserved_or_rejected_without_rewriting_input(string scope, bool supported)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "synthetic-winui-settings.json");
        var original = JsonSerializer.Serialize(new
        {
            TaskViewSettings = new Dictionary<string, object>
            { ["|today|all"] = new { DateScope = scope } },
        });
        await File.WriteAllTextAsync(path, original);
        var input = await File.ReadAllTextAsync(path);
        if (supported) Assert.Equal(original, WinUiSettingsConverter.Convert(input).OriginalSettingsJson);
        else Assert.Throws<InvalidDataException>(() => WinUiSettingsConverter.Convert(input));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    private async Task<(SqliteTaskStore Store, MainWindowViewModel ViewModel)> CreateAsync(
        TaskSortMode sort, TaskSortDirection direction, TaskGroupMode group)
    {
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        await store.UpsertProjectAsync(new() { Id = "project_a", Name = "Alpha project" });
        await store.UpsertProjectAsync(new() { Id = "project_b", Name = "Zulu project" });
        var label = new LabelItem { Id = "label_example", Name = "Example" };
        await store.UpsertLabelAsync(label);
        var day = new DateOnly(2026, 6, 17);
        var created = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        TaskItem[] rows =
        [
            new() { Id = "a", Title = "Alpha", SpaceId = SpaceIds.Default, Priority = 3,
                ProjectId = "project_a", WorkflowStatus = TaskWorkflowStatus.Next, PlannedOn = day.AddDays(1),
                CreatedAt = created.AddDays(2), Labels = [label], RecurrenceRule = "synthetic repeat" },
            new() { Id = "z", Title = "Zulu", SpaceId = SpaceIds.Default, Priority = 1,
                ProjectId = "project_b", WorkflowStatus = TaskWorkflowStatus.Inbox, PlannedOn = day.AddDays(3),
                CreatedAt = created, SourceIntegrationId = IntegrationIds.Todoist, SourceExternalId = "synthetic-z" },
            new() { Id = "e", Title = "Echo", SpaceId = SpaceIds.Default, Priority = 4,
                WorkflowStatus = TaskWorkflowStatus.Waiting, PlannedOn = day, CreatedAt = created.AddDays(4),
                SourceIntegrationId = IntegrationIds.MicrosoftToDo, SourceExternalId = "synthetic-e" },
            new() { Id = "b", Title = "Bravo", SpaceId = SpaceIds.Default, Priority = 2,
                ProjectId = "project_a", WorkflowStatus = TaskWorkflowStatus.None, CreatedAt = created.AddDays(3) },
        ];
        foreach (var row in rows) await store.UpsertTaskAsync(row);
        var input = JsonSerializer.Serialize(new
        {
            LastView = "tasks", LastSpaceId = SpaceIds.Default,
            AutoSyncEnabled = false, AutoBackupEnabled = false, ShowGetStarted = false,
            TaskViewSettings = new Dictionary<string, object>
            { [$"{SpaceIds.Default}|tasks|all"] = new { SortMode = sort.ToString(), SortDirection = direction.ToString(), GroupMode = group.ToString() } },
        });
        var converted = WinUiSettingsConverter.Convert(input);
        var preferences = new DesktopPreferencesStore(Path.Combine(_directory, "converted-settings.json"));
        await preferences.SaveAsync(converted.Preferences);
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
        await vm.InitializeAsync();
        Assert.Equal(TaskListKind.Open, vm.SelectedNavigation!.Kind);
        return (store, vm);
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
