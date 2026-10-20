using Avalonia.Controls;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.Shell;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed class DesktopExactDateEditingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Displaying_and_saving_exact_only_dates_does_not_emit_date_edits(bool cloud)
    {
        using var fixture = await ExactDateFixture.CreateAsync(cloud);
        var vm = fixture.ViewModel;
        await fixture.SelectAsync();
        Assert.Equal(TaskDateValues.FromDateTimeOffset(ExactDateFixture.Planned), DateOnly.FromDateTime(vm.DetailDate!.Value.LocalDateTime));
        Assert.Equal(TaskDateValues.FromDateTimeOffset(ExactDateFixture.Deadline), DateOnly.FromDateTime(vm.DetailDeadline!.Value.LocalDateTime));
        var before = (await fixture.Store.GetTaskAsync(ExactDateFixture.TaskId))!;
        var pending = (await fixture.OutboxAsync()).Count;
        Assert.True(await vm.SaveSelectedAsync());
        var unchanged = (await fixture.Store.GetTaskAsync(before.Id))!;
        Assert.Equal(before.Revision, unchanged.Revision);
        Assert.Null(unchanged.PlannedOn); Assert.Null(unchanged.DeadlineOn);
        Assert.Equal(pending, (await fixture.OutboxAsync()).Count);
        vm.DetailTitle = "Unrelated title edit";
        Assert.True(await vm.SaveSelectedAsync());
        var edited = (await fixture.Store.GetTaskAsync(before.Id))!;
        Assert.Equal(ExactDateFixture.Planned, edited.PlannedAt);
        Assert.Equal(ExactDateFixture.Deadline, edited.DeadlineAt);
        Assert.Empty(await fixture.Store.GetPendingTaskDateUpdatesAsync(IntegrationIds.Todoist));
        if (cloud)
        {
            var emitted = (await fixture.OutboxAsync()).Skip(pending);
            var mutation = Assert.Single(emitted);
            Assert.DoesNotContain("plannedOn", mutation.CanonicalJson, StringComparison.Ordinal);
            Assert.DoesNotContain("deadlineOn", mutation.CanonicalJson, StringComparison.Ordinal);
            Assert.Null(edited.PlannedOn); Assert.Null(edited.DeadlineOn);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Explicit_clear_or_replacement_survives_second_unrelated_save(bool cloud, bool replace)
    {
        using var fixture = await ExactDateFixture.CreateAsync(cloud);
        await fixture.SelectAsync();
        var vm = fixture.ViewModel;
        DateOnly? chosen = replace ? new(2026, 7, 1) : null;
        vm.DetailDate = TaskDateValues.ToLocalDateTime(chosen);
        vm.DetailDeadline = TaskDateValues.ToLocalDateTime(chosen);
        var gate = (SemaphoreSlim)typeof(MainWindowViewModel)
            .GetField("_operationGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
        await gate.WaitAsync();
        Task<bool> firstSave;
        try
        {
            firstSave = vm.SaveSelectedAsync();
            vm.DetailNotes = "Typed while the date save was queued";
        }
        finally { gate.Release(); }
        Assert.True(await firstSave);
        var first = (await fixture.Store.GetTaskAsync(ExactDateFixture.TaskId))!;
        Assert.Equal(chosen, first.PlannedOn); Assert.Equal(chosen, first.DeadlineOn);
        Assert.Null(first.PlannedAt); Assert.Null(first.DeadlineAt);
        Assert.Equal("Typed while the date save was queued", first.Notes);
        var pending = (await fixture.OutboxAsync()).Count;
        vm.DetailNotes = "A later unrelated edit";
        Assert.True(await vm.SaveSelectedAsync());
        var second = (await fixture.Store.GetTaskAsync(first.Id))!;
        Assert.Equal(chosen, second.PlannedOn); Assert.Equal(chosen, second.DeadlineOn);
        Assert.Null(second.PlannedAt); Assert.Null(second.DeadlineAt);
        Assert.Equal(TaskDateValues.ToLocalDateTime(chosen), vm.DetailDate);
        Assert.Equal(TaskDateValues.ToLocalDateTime(chosen), vm.DetailDeadline);
        if (cloud)
        {
            var mutation = Assert.Single((await fixture.OutboxAsync()).Skip(pending));
            Assert.DoesNotContain("plannedOn", mutation.CanonicalJson, StringComparison.Ordinal);
            Assert.DoesNotContain("deadlineOn", mutation.CanonicalJson, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_keeps_new_untouched_exact_dates_while_preserving_unsaved_notes(bool cloud)
    {
        using var fixture = await ExactDateFixture.CreateAsync(cloud);
        await fixture.Store.UpsertProjectAsync(new() { Id = "refresh-project", Name = "Synthetic refresh project" });
        await fixture.SelectAsync();
        var vm = fixture.ViewModel;
        vm.DetailNotes = "Unsaved note during refresh";
        var planned = ExactDateFixture.Planned.AddDays(2);
        var deadline = ExactDateFixture.Deadline.AddDays(2);
        await fixture.ForceExactOnlyAsync(planned, deadline);
        // This public operation refreshes with preserveEditor without first saving
        // the task, so it exercises the same restoration used after background sync.
        await vm.UpdateProjectAsync("refresh-project", "Refreshed project", "active", false);
        Assert.Equal("Unsaved note during refresh", vm.DetailNotes);
        Assert.Null((await fixture.Store.GetTaskAsync(ExactDateFixture.TaskId))!.Notes);
        Assert.Equal(TaskDateValues.FromDateTimeOffset(planned), DateOnly.FromDateTime(vm.DetailDate!.Value.LocalDateTime));
        Assert.Equal(TaskDateValues.FromDateTimeOffset(deadline), DateOnly.FromDateTime(vm.DetailDeadline!.Value.LocalDateTime));
        var pending = (await fixture.OutboxAsync()).Count;
        Assert.True(await vm.SaveSelectedAsync());
        var saved = (await fixture.Store.GetTaskAsync(ExactDateFixture.TaskId))!;
        Assert.Equal(planned, saved.PlannedAt); Assert.Equal(deadline, saved.DeadlineAt);
        Assert.Empty(await fixture.Store.GetPendingTaskDateUpdatesAsync(IntegrationIds.Todoist));
        if (cloud)
        {
            var mutation = Assert.Single((await fixture.OutboxAsync()).Skip(pending));
            Assert.DoesNotContain("plannedOn", mutation.CanonicalJson, StringComparison.Ordinal);
            Assert.DoesNotContain("deadlineOn", mutation.CanonicalJson, StringComparison.Ordinal);
        }
    }
}

[Collection(nameof(EditorTestCollection))]
public sealed class DesktopExactDatePickerTests(EditorTestSession session)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rendered_date_pickers_show_exact_only_days_without_auto_saving(bool cloud)
    {
        await session.Session.Dispatch(async () =>
        {
            using var fixture = await ExactDateFixture.CreateAsync(cloud);
            var vm = fixture.ViewModel;
            var window = new MainWindow(vm);
            try
            {
                window.Show();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (vm.SelectedNavigation is null || vm.IsBusy) await Task.Delay(10, timeout.Token);
                await fixture.ForceExactOnlyAsync();
                var original = (await fixture.Store.GetTaskAsync(ExactDateFixture.TaskId))!;
                await fixture.SelectAsync();
                await Task.Delay(100, timeout.Token);
                Assert.Equal(TaskDateValues.FromDateTimeOffset(ExactDateFixture.Planned)!.Value.ToDateTime(TimeOnly.MinValue), window.FindControl<CalendarDatePicker>("DetailDatePicker")!.SelectedDate);
                Assert.Equal(TaskDateValues.FromDateTimeOffset(ExactDateFixture.Deadline)!.Value.ToDateTime(TimeOnly.MinValue), window.FindControl<CalendarDatePicker>("DetailDeadlinePicker")!.SelectedDate);
                Assert.True(await vm.SaveSelectedAsync());
                var after = (await fixture.Store.GetTaskAsync(original.Id))!;
                Assert.Equal(original.Revision, after.Revision);
                Assert.Null(after.PlannedOn); Assert.Null(after.DeadlineOn);
                Assert.Equal(ExactDateFixture.Planned, after.PlannedAt);
                Assert.Equal(ExactDateFixture.Deadline, after.DeadlineAt);
            }
            finally { window.Close(); await Task.Delay(100); }
            return true;
        }, CancellationToken.None);
    }
}

internal sealed class ExactDateFixture(string directory, SqliteTaskStore store, MainWindowViewModel viewModel) : IDisposable
{
    public const string TaskId = "task_synthetic_exact_editor";
    private const string Instance = "instance_00000000000000000000000000000001";
    private const string Device = "device_00000000000000000000000000000001";
    public static readonly DateTimeOffset Planned = DateTimeOffset.Parse("2026-06-17T00:15:00+14:00");
    public static readonly DateTimeOffset Deadline = DateTimeOffset.Parse("2026-06-20T23:45:00-12:00");
    public SqliteTaskStore Store { get; } = store;
    public MainWindowViewModel ViewModel { get; } = viewModel;

    public static async Task<ExactDateFixture> CreateAsync(bool cloud)
    {
        var directory = Path.Combine(Path.GetTempPath(), "openza-exact-date-editor", Guid.NewGuid().ToString("N"));
        var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
        await store.InitializeAsync();
        var task = new TaskItem { Id = TaskId, Title = "Synthetic exact-time task", SpaceId = SpaceIds.Default, PlannedAt = Planned, DeadlineAt = Deadline };
        if (cloud)
        {
            await store.CreateTaskWithPersonalCloudIntentAsync(task, Instance, Device);
            await store.UpdateTaskPlannedAtWithPersonalCloudIntentAsync(TaskId, Planned, 1, Instance, Device);
        }
        else await store.UpsertTaskAsync(task with
        { SourceIntegrationId = IntegrationIds.Todoist, SourceExternalId = "synthetic-source", SourceProviderTaskId = "synthetic-source" });
        var preferences = new DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
        await preferences.SaveAsync(new() { ShowGetStarted = false, AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false });
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences,
            personalCloudSetup: cloud ? new OfflineSetup() : null);
        var fixture = new ExactDateFixture(directory, store, vm);
        await fixture.ForceExactOnlyAsync();
        return fixture;
    }

    public async Task SelectAsync()
    {
        await ViewModel.SelectNavigationAsync(ViewModel.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await ViewModel.SelectTaskAsync(Assert.Single(ViewModel.Tasks));
    }

    public async Task ForceExactOnlyAsync(DateTimeOffset? planned = null, DateTimeOffset? deadline = null)
    {
        await using var connection = new SqliteConnection($"Data Source={Store.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tasks SET planned_on = NULL, deadline_on = NULL, planned_at = @planned, deadline_at = @deadline WHERE id = @id";
        command.Parameters.AddWithValue("@planned", (planned ?? Planned).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@deadline", (deadline ?? Deadline).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@id", TaskId);
        await command.ExecuteNonQueryAsync();
    }

    public Task<IReadOnlyList<PersonalCloudK1OutboxEntry>> OutboxAsync() => Store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
    public void Dispose() => TestDirectory.Delete(directory);

    private sealed class OfflineSetup : IPersonalCloudSetupService
    {
        public bool IsConfigured => true;
        public PersonalCloudConnectionProfile CurrentConnection { get; } = new()
        { ServiceOrigin = new("https://example.invalid/"), InstanceId = Instance, DeviceId = Device, ArtifactVersion = "1.2.3", ConnectedAt = DateTimeOffset.UtcNow };
        public Task OpenAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PersonalCloudProvisioningResult> ConnectAsync(CancellationToken cancellationToken = default, IProgress<PersonalCloudSetupProgress>? progress = null) => throw new NotSupportedException();
        public Task<PersonalCloudConnectionProfile> PairExistingAsync(Uri serviceOrigin, string grant, string deviceName, bool providerBridge, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudflareD1Export> ExportAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PersonalCloudUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
