using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed class MicrosoftReminderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "openza-reminders", Guid.NewGuid().ToString("N"));
    private const string Instance = "instance_reminder_test";
    private const string Device = "device_reminder_test";
    private static readonly DateTimeOffset Instant = DateTimeOffset.Parse("2027-07-01T16:30:00Z");
    private static string Reminder(bool enabled = true, string zone = "UTC", string date = "2027-07-01T16:30:00") =>
        JsonSerializer.Serialize(new { msToDo = new { reminder = new { isReminderOn = enabled, reminderDateTime = new { dateTime = date, timeZone = zone } } } });
    private static ProviderSourceItem Source(string snapshot) => new()
    {
        Id = "source_reminder", ExternalId = "external_reminder", ProviderTaskId = "list|external_reminder",
        ProviderConnectionId = "mstodo_default", IntegrationId = IntegrationIds.MicrosoftToDo,
        Title = "Synthetic imported reminder", SnapshotJson = snapshot,
    };
    private async Task<SqliteTaskStore> Store()
    {
        var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
        await store.InitializeAsync();
        return store;
    }
    private static async Task<TaskItem> Adopt(SqliteTaskStore store, string snapshot)
    {
        await store.UpsertProviderSourceItemAsync(Source(snapshot));
        return (await store.AdoptProviderSourceItemAsync(Source(snapshot).Id))!;
    }
    private static void Applied(TaskItem task, bool expected) =>
        Assert.Equal(expected, task.SourceMetadataJson?.Contains("\"msToDoReminderApplied\":true", StringComparison.Ordinal) == true);

    [Theory]
    [InlineData("UTC", "2027-07-01T16:30:00", "2027-07-01T16:30:00Z")]
    [InlineData("America/Los_Angeles", "2027-07-01T09:30:00", "2027-07-01T16:30:00Z")]
    [InlineData("Pacific Standard Time", "2027-07-01T09:30:00", "2027-07-01T16:30:00Z")]
    [InlineData("America/Los_Angeles", "2027-01-01T09:30:00", "2027-01-01T17:30:00Z")]
    [InlineData("America/Los_Angeles", "2027-07-01T16:30:00Z", "2027-07-01T16:30:00Z")]
    [InlineData("Pacific Standard Time", "2027-07-01T09:30:00-07:00", "2027-07-01T16:30:00Z")]
    [InlineData("UTC", "2027-07-01T16:30:00.1234567", "2027-07-01T16:30:00.123Z")]
    public async Task On_import_converts_windows_and_iana_zones_to_an_instant(string zone, string date, string expected)
    {
        var task = await Adopt(await Store(), Reminder(zone: zone, date: date));
        Assert.Equal(DateTimeOffset.Parse(expected), task.ReminderAt);
        Applied(task, true);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"msToDo\":{}}")]
    [InlineData("{\"msToDo\":{\"reminder\":null}}")]
    [InlineData("{\"msToDo\":{\"reminder\":{\"isReminderOn\":\"true\"}}}")]
    [InlineData("{\"msToDo\":{\"reminder\":{\"isReminderOn\":true}}}")]
    public async Task Absent_or_malformed_values_leave_import_pending(string snapshot)
    {
        var task = await Adopt(await Store(), snapshot);
        Assert.Null(task.ReminderAt); Applied(task, false);
    }

    [Theory]
    [InlineData("Unknown/Synthetic", "2027-07-01T16:30:00")]
    [InlineData("UTC", "bad-date")]
    [InlineData("UTC", "2027-07-01")]
    [InlineData("America/Los_Angeles", "2027-03-14T02:30:00")]
    public async Task Invalid_date_zone_or_dst_gap_can_retry_later(string zone, string date)
    {
        var store = await Store();
        var task = await Adopt(store, Reminder(zone: zone, date: date));
        Assert.Null(task.ReminderAt); Applied(task, false);
        await store.UpsertProviderSourceItemAsync(Source(Reminder()));
        task = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Instant, task.ReminderAt); Applied(task, true);
    }

    [Fact]
    public async Task Explicit_off_marks_without_setting_a_reminder()
    {
        var store = await Store();
        var task = await Adopt(store, "{\"msToDo\":{\"reminder\":{\"isReminderOn\":false}}}");
        Assert.Null(task.ReminderAt); Applied(task, true);
        await store.UpsertProviderSourceItemAsync(Source(Reminder()));
        Assert.Null((await store.GetTaskAsync(task.Id))!.ReminderAt);
    }

    [Fact]
    public async Task Unknown_zone_is_logged_and_leaves_only_the_reminder_marker_unset()
    {
        using var writer = new StringWriter();
        using var listener = new TextWriterTraceListener(writer);
        Trace.Listeners.Add(listener);
        try
        {
            var store = await Store();
            var task = await Adopt(store, "{\"msToDo\":{\"categories\":[],\"checklistItems\":[],\"reminder\":{\"isReminderOn\":true,\"reminderDateTime\":{\"dateTime\":\"2027-07-01T09:30:00\",\"timeZone\":\"Unknown/Synthetic\"}}}}");
            Applied(task, false);
            Assert.Contains("\"msToDoCategoriesApplied\":true", task.SourceMetadataJson);
            Assert.Contains("\"msToDoStepsApplied\":true", task.SourceMetadataJson);
            listener.Flush();
            Assert.Contains("unknown time zone", writer.ToString());
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backfill_preserves_existing_local_value_and_user_clear_survives_refresh_and_relink(bool localValue)
    {
        var store = await Store();
        var task = await Adopt(store, "{}");
        if (localValue) await store.TrySetTaskReminderAsync(task.Id, Instant.AddHours(1));
        await store.UpsertProviderSourceItemAsync(Source(Reminder()));
        task = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(localValue ? Instant.AddHours(1) : Instant, task.ReminderAt); Applied(task, true);
        await store.TrySetTaskReminderAsync(task.Id, null);
        await store.DetachProviderSourceItemAsync(Source("").Id);
        Applied((await store.GetTaskAsync(task.Id))!, true);
        await store.UpsertProviderSourceItemAsync(Source(Reminder()));
        task = (await store.GetTaskAsync(task.Id))!;
        Assert.Null(task.ReminderAt); Applied(task, true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Editing_and_clearing_reminder_never_enqueue_cloud_or_block_pairing(bool workspace, bool connected)
    {
        var store = await Store();
        if (workspace) await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        var task = await Adopt(store, Reminder());
        if (workspace)
            await store.ApplyPersonalCloudChangePageAsync(Instance, Device,
                PersonalCloudWorkspaceTests.Page(1, [PersonalCloudWorkspaceTests.Space()],
                    tasks: [PersonalCloudWorkspaceTests.TaskRecord(task.Id, projectId: null)]));
        else
            await store.ReplacePersonalCloudReplicaFromBootstrapAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new PersonalCloudBootstrap
            {
                ProtocolVersion = 1, Product = "tasks", Checkpoint = 1,
                Snapshot = new() { Spaces = [new(SpaceIds.Default, "Default", null)], Projects = [], Labels = [], TaskLabels = [], Tombstones = [],
                    Tasks = [new(task.Id, SpaceIds.Default, null, null, "Remote title", "open", "inbox", 2, null, null, null, null, null,
                        "2027-01-01T00:00:00.000Z", 1, null)] }
            }));
        task = (await store.GetTaskAsync(task.Id))!;
        var updatedAt = task.UpdatedAt;
        var service = new TaskApplicationService(store, () => connected ? new() { ServiceOrigin = new Uri("https://synthetic.invalid"),
            InstanceId = Instance, DeviceId = Device, ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow } : null);
        foreach (var value in new DateTimeOffset?[] { Instant.AddHours(2), null })
        {
            task = await service.UpdateTaskAsync(new() { TaskId = task.Id, ReminderAt = OptionalValue<DateTimeOffset?>.Set(value) });
            Assert.Equal(value, task.ReminderAt);
            Assert.Equal(updatedAt, task.UpdatedAt);
            Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
            await store.EnsureNoPendingPersonalCloudIntentsAsync();
        }
    }

    [Fact]
    public async Task Reminder_edit_checks_revision_and_missing_tasks_without_changing_the_value()
    {
        var store = await Store();
        var task = await Adopt(store, Reminder());
        Assert.False(await store.TrySetTaskReminderAsync("missing", null));
        Assert.False(await store.TrySetTaskReminderAsync(task.Id, null, task.Revision + 1));
        var service = new TaskApplicationService(store);
        await Assert.ThrowsAsync<TaskConflictException>(() => service.UpdateTaskAsync(new()
        {
            TaskId = task.Id, ExpectedRevision = task.Revision + 1, ReminderAt = OptionalValue<DateTimeOffset?>.Set(null),
        }));
        Assert.Equal(Instant, (await store.GetTaskAsync(task.Id))!.ReminderAt);
    }

    [Fact]
    public async Task Workspace_hydration_and_replacement_keep_device_value_and_marker()
    {
        var store = await Store();
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        var task = await Adopt(store, Reminder());
        var remote = PersonalCloudWorkspaceTests.TaskRecord(task.Id, projectId: null);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device,
            PersonalCloudWorkspaceTests.Page(1, [PersonalCloudWorkspaceTests.Space()], tasks: [remote]));
        task = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Instant, task.ReminderAt); Applied(task, true);
        var bootstrap = JsonSerializer.Serialize(new { protocolVersion = 1, product = "tasks", checkpoint = 4, snapshot = new {
            spaces = new[] { PersonalCloudWorkspaceTests.Space() }, projects = Array.Empty<object>(), tasks = new[] { remote },
            labels = Array.Empty<object>(), taskLabels = Array.Empty<object>(), tombstones = Array.Empty<object>(), sourceDescriptors = Array.Empty<object>(),
            externalCommands = Array.Empty<object>(), scopes = Array.Empty<object>(), catalog = Array.Empty<object>(), intents = Array.Empty<object>(), routes = Array.Empty<object>() } });
        await store.ReplacePersonalCloudWorkspaceFromBootstrapAsync(Instance, Device, bootstrap);
        task = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Instant, task.ReminderAt); Applied(task, true);
        await store.TrySetTaskReminderAsync(task.Id, null);
        await store.ReplacePersonalCloudWorkspaceFromBootstrapAsync(Instance, Device, bootstrap);
        task = (await store.GetTaskAsync(task.Id))!;
        Assert.Null(task.ReminderAt); Applied(task, true);
    }

    [Fact]
    public async Task Legacy_bootstrap_replacement_keeps_device_value_and_marker()
    {
        var store = await Store();
        var task = await Adopt(store, Reminder());
        var bootstrap = PersonalCloudK0Codec.Serialize(new PersonalCloudBootstrap {
            ProtocolVersion = 1, Product = "tasks", Checkpoint = 1,
            Snapshot = new() { Spaces = [new(SpaceIds.Default, "Default", null)], Projects = [], Labels = [], TaskLabels = [], Tombstones = [],
                Tasks = [new(task.Id, SpaceIds.Default, null, null, "Remote title", "open", "inbox", 2, null, null, null, null, null,
                    "2027-01-01T00:00:00.000Z", 1, null)] } });
        await store.ReplacePersonalCloudReplicaFromBootstrapAsync(Instance, Device, bootstrap);
        task = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Instant, task.ReminderAt); Applied(task, true);
        await store.TrySetTaskReminderAsync(task.Id, null);
        await store.ReplacePersonalCloudReplicaFromBootstrapAsync(Instance, Device, bootstrap);
        task = (await store.GetTaskAsync(task.Id))!;
        Assert.Null(task.ReminderAt); Applied(task, true);
    }

    [Fact]
    public async Task Fresh_schema_and_existing_schema_upgrade_round_trip_json_export_and_backup()
    {
        var store = await Store();
        await store.UpsertTaskAsync(new() { Id = "task_reminder", Title = "Synthetic export", ReminderAt = Instant });
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE tasks DROP COLUMN reminder_at; PRAGMA user_version=13;";
            await command.ExecuteNonQueryAsync();
        }
        await store.InitializeAsync();
        var task = (await store.GetTaskAsync("task_reminder"))!;
        Assert.Null(task.ReminderAt);
        await store.TrySetTaskReminderAsync(task.Id, Instant);
        task = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Instant, JsonSerializer.Deserialize<TaskItem>(JsonSerializer.Serialize(task))!.ReminderAt);
        var markdown = MarkdownExporter.Export([task], [], []);
        var parsed = Assert.Single(MarkdownTaskParser.Parse(markdown));
        Assert.Equal(Instant, parsed.ReminderAt);
        await MarkdownTaskImporter.ImportAsync(store, [parsed], SpaceIds.Default);
        Assert.All(await store.GetTasksAsync(new() { Kind = TaskListKind.All }), item => Assert.Equal(Instant, item.ReminderAt));
        var backups = new BackupService(store.DatabasePath, Path.Combine(directory, "backups"));
        var backup = await backups.CreateBackupAsync();
        await store.TrySetTaskReminderAsync(task.Id, null);
        await backups.RestoreBackupAsync(backup);
        Assert.Equal(Instant, (await store.GetTaskAsync(task.Id))!.ReminderAt);
    }

    [Fact]
    public async Task Desktop_view_model_edits_and_clears_imported_reminder()
    {
        var store = await Store();
        var task = await Adopt(store, Reminder());
        var preferences = new DesktopPreferencesStore(Path.Combine(directory, "preferences.json"));
        await preferences.SaveAsync(new() { ShowGetStarted = false });
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
        await vm.InitializeAsync();
        await vm.SelectTaskAsync(vm.Tasks.Single(item => item.Task.Id == task.Id));
        Assert.Equal(Instant, vm.DetailReminder);
        vm.DetailReminderDate = Instant.LocalDateTime.Date.AddDays(1);
        vm.DetailReminderTime = TimeSpan.FromHours(10);
        var expected = vm.DetailReminder;
        Assert.True(await vm.SaveSelectedAsync());
        Assert.Equal(expected, (await store.GetTaskAsync(task.Id))!.ReminderAt);
        vm.DetailReminder = null;
        Assert.True(await vm.SaveSelectedAsync());
        Assert.Null((await store.GetTaskAsync(task.Id))!.ReminderAt);
    }

    [Theory]
    [InlineData("\"isReminderOn\":true,\"reminderDateTime\":{\"dateTime\":\"2027-07-01T09:30:00\",\"timeZone\":\"Pacific Standard Time\"}", true)]
    [InlineData("\"isReminderOn\":false", true)]
    [InlineData("\"isReminderOn\":true,\"reminderDateTime\":null", true)]
    [InlineData("\"isReminderOn\":\"bad\"", true)]
    [InlineData("\"status\":\"notStarted\"", false)]
    public async Task Graph_snapshot_preserves_reminder_fields_and_absence(string properties, bool present)
    {
        using var client = new HttpClient(new GraphHandler(properties));
        var snapshot = await new MicrosoftToDoProvider(client, "synthetic").FetchSnapshotAsync();
        using var metadata = JsonDocument.Parse(Assert.Single(snapshot.Tasks).ProviderMetadataJson!);
        Assert.Equal(present, metadata.RootElement.GetProperty("msToDo").TryGetProperty("reminder", out var reminder));
        if (present)
        {
            using var original = JsonDocument.Parse("{" + properties + "}");
            Assert.Equal(original.RootElement.GetProperty("isReminderOn").GetRawText(), reminder.GetProperty("isReminderOn").GetRawText());
        }
    }

    private sealed class GraphHandler(string properties) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path.EndsWith("/lists", StringComparison.Ordinal) ? "{\"value\":[{\"id\":\"list\"}]}" :
                path.Contains("masterCategories", StringComparison.Ordinal) ? "{\"value\":[]}" : "{\"value\":[{\"id\":\"task\"," + properties + "}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
