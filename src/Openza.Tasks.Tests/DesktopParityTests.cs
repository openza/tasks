using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed class DesktopParityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-parity-tests", Guid.NewGuid().ToString("N"));
    private async Task<SqliteTaskStore> StoreAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        return store;
    }
    private static TaskItem TaskIn(string id, string? project = null) => new()
    {
        Id = id, Title = id, ProjectId = project, SpaceId = SpaceIds.Default,
        IntegrationId = IntegrationIds.Local, Status = TaskItemStatus.Inbox, CreatedAt = DateTimeOffset.UtcNow,
    };

    [Theory]
    [InlineData(ProjectLifecycleStates.Completed, false)]
    [InlineData(ProjectLifecycleStates.Archived, true)]
    public async Task Editing_title_or_notes_preserves_nonactive_project(string status, bool archived)
    {
        var store = await StoreAsync();
        await store.UpsertProjectAsync(new ProjectItem { Id = "project", Name = "Project", SpaceId = SpaceIds.Default, Status = status, IsArchived = archived });
        await store.UpsertTaskAsync(TaskIn("task", "project"));
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await vm.SelectNavigationAsync(vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await vm.SelectTaskAsync(Assert.Single(vm.Tasks));
        Assert.Equal("project", vm.DetailProject?.ProjectId);
        vm.DetailTitle = "Edited title";
        Assert.True(await vm.SaveSelectedAsync());
        vm.DetailNotes = "Edited notes";
        Assert.True(await vm.SaveSelectedAsync());
        Assert.Equal("project", (await store.GetTaskAsync("task"))!.ProjectId);
        vm.DetailProject = vm.ProjectOptions.First(item => item.ProjectId is null);
        Assert.True(await vm.SaveSelectedAsync());
        Assert.Null((await store.GetTaskAsync("task"))!.ProjectId);
    }

    [Fact]
    public async Task Return_to_list_remembers_its_selection()
    {
        var store = await StoreAsync();
        await store.UpsertTaskAsync(TaskIn("inbox"));
        await store.UpsertTaskAsync(TaskIn("next") with { Status = TaskItemStatus.Next });
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore());
        var inbox = vm.NavigationItems.Single(item => item.Kind == TaskListKind.Inbox);
        var next = vm.NavigationItems.Single(item => item.Kind == TaskListKind.NextActions);
        await vm.SelectNavigationAsync(inbox);
        await vm.SelectTaskAsync(Assert.Single(vm.Tasks));
        await vm.SelectNavigationAsync(next);
        await vm.SelectTaskAsync(Assert.Single(vm.Tasks));
        await vm.SelectNavigationAsync(inbox);
        Assert.Equal("inbox", vm.SelectedTask?.Task.Id);
        await vm.SelectNavigationAsync(next);
        Assert.Equal("next", vm.SelectedTask?.Task.Id);
    }

    [Fact]
    public async Task Project_edit_saves_color_without_changing_identity()
    {
        var store = await StoreAsync();
        await store.UpsertProjectAsync(new ProjectItem { Id = "project", Name = "Project", SpaceId = SpaceIds.Default });
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await vm.UpdateProjectAsync("project", "Renamed", ProjectLifecycleStates.Completed, true, "#123456");
        var project = Assert.Single(await store.GetProjectsAsync(SpaceIds.Default));
        Assert.Equal("project", project.Id);
        Assert.Equal("#123456", project.Color);
        Assert.Equal(ProjectLifecycleStates.Completed, project.EffectiveStatus);
        Assert.True(project.IsFavorite);
    }

    [Fact]
    public async Task Last_view_and_backup_preference_survive_new_view_model()
    {
        var store = await StoreAsync();
        var preferences = new DesktopPreferencesStore(Path.Combine(_directory, "settings.json"));
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
        await vm.SetAutomaticRestorePointsAsync(false);
        await vm.DismissGetStartedAsync();
        await vm.SelectNavigationAsync(vm.NavigationItems.Single(item => item.Kind == TaskListKind.NextActions));
        var restarted = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
        await restarted.InitializeAsync();
        Assert.Equal(TaskListKind.NextActions, restarted.SelectedNavigation?.Kind);
        Assert.False(restarted.AutomaticRestorePointsEnabled);
    }

    [Theory]
    [InlineData("Settings", true, "Inbox")]
    [InlineData("Sync", true, "Inbox")]
    [InlineData("Settings", false, "Settings")]
    public async Task Empty_first_run_opens_Inbox_until_onboarding_is_dismissed(string lastView, bool showGetStarted, string expected)
    {
        var store = await StoreAsync();
        var preferences = new DesktopPreferencesStore(Path.Combine(_directory, "settings.json"));
        await preferences.SaveAsync(new() { LastView = lastView, ShowGetStarted = showGetStarted, AutomaticRestorePointsEnabled = false });
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
        await vm.InitializeAsync();
        Assert.Equal(expected, vm.StartupView);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Archive_space_preserves_other_context_and_saves_pending_edits(bool archiveCurrent)
    {
        var store = await StoreAsync();
        await store.UpsertSpaceAsync(new SpaceItem { Id = "other", Name = "Other" });
        await store.UpsertTaskAsync(TaskIn("task"));
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore());
        await vm.SetAutomaticRestorePointsAsync(false);
        await vm.InitializeAsync();
        await vm.SelectSpaceAsync(vm.SpaceItems.Single(item => item.SpaceId == SpaceIds.Default));
        await vm.SelectNavigationAsync(vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await vm.SelectTaskAsync(Assert.Single(vm.Tasks));
        vm.DetailNotes = "Pending notes";
        await vm.ArchiveSpaceAsync(vm.SpaceItems.Single(item => item.SpaceId == (archiveCurrent ? SpaceIds.Default : "other")));
        Assert.Equal("Pending notes", (await store.GetTaskAsync("task"))!.Notes);
        Assert.Equal(archiveCurrent ? "other" : SpaceIds.Default, vm.SelectedSpace?.SpaceId);
        if (!archiveCurrent) Assert.Equal("task", vm.SelectedTask?.Task.Id);
    }

    [Fact]
    public async Task Sync_attempts_Microsoft_when_Todoist_fails_and_retains_result_after_edit()
    {
        var store = await StoreAsync();
        await store.UpsertTaskAsync(TaskIn("task"));
        var credentials = new InMemoryCredentialStore();
        await credentials.SaveAsync("todoist-token", "test-token");
        var todoist = new StubProvider(IntegrationIds.Todoist, fail: true);
        var microsoft = new StubProvider(IntegrationIds.MicrosoftToDo, fail: false);
        var vm = new MainWindowViewModel(store, credentials, todoistSyncProviderFactory: _ => todoist,
            microsoftAuth: new StubMicrosoftAuth(), microsoftSyncProviderFactory: _ => microsoft);
        await vm.ConnectMicrosoftAsync("todo", _ => Task.CompletedTask, CancellationToken.None);
        await vm.RunTodoistSyncAsync();
        Assert.Equal(1, todoist.FetchCount); Assert.Equal(1, microsoft.FetchCount);
        Assert.Contains("sync failed", vm.LastSyncResult); Assert.Contains("Microsoft To Do synced", vm.LastSyncResult);
        var result = vm.LastSyncResult;
        await vm.SelectNavigationAsync(vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await vm.SelectTaskAsync(Assert.Single(vm.Tasks));
        vm.DetailTitle = "Changed";
        Assert.True(await vm.SaveSelectedAsync());
        Assert.Equal(result, vm.LastSyncResult);
    }

    [Fact]
    public async Task Cloud_encryption_keeps_passphrase_in_credential_store_only()
    {
        var store = await StoreAsync();
        var credentials = new InMemoryCredentialStore();
        var vm = new MainWindowViewModel(store, credentials);
        await vm.SetCloudEncryptionAsync(true, "test-only-passphrase");
        Assert.True(vm.OneDriveEncrypted);
        Assert.Equal("test-only-passphrase", await credentials.GetAsync("onedrive-backup-passphrase"));
        Assert.DoesNotContain("test-only-passphrase", await File.ReadAllTextAsync(Path.Combine(_directory, "settings.json")));
        await vm.SetCloudEncryptionAsync(false, null);
        Assert.Null(await credentials.GetAsync("onedrive-backup-passphrase"));
    }

    [Fact]
    public async Task Startup_sync_status_recognizes_stored_Todoist_credentials_without_opening_Settings()
    {
        var store = await StoreAsync();
        var credentials = new InMemoryCredentialStore();
        await credentials.SaveAsync("todoist-token", "test-token");
        var vm = new MainWindowViewModel(store, credentials);
        await vm.SetAutomaticRestorePointsAsync(false);
        await vm.InitializeAsync();
        Assert.DoesNotContain("Not connected", vm.TodoistConnectionText);
    }

    private sealed class StubMicrosoftAuth : IDesktopMicrosoftAuthService
    {
        public Task<MicrosoftAccess> ConnectAsync(string feature, Func<SignInCode, Task> showCode, CancellationToken cancellationToken) =>
            Task.FromResult(new MicrosoftAccess("test-token", new MicrosoftAccount("test-account", "test-user")));
        public Task<string?> GetTokenAsync(string feature, MicrosoftAccount? account, CancellationToken cancellationToken = default) => Task.FromResult<string?>(account is null ? null : "test-token");
        public Task DisconnectAsync(string feature) => Task.CompletedTask;
    }
    private sealed class StubProvider(string integration, bool fail) : ISyncProvider
    {
        public string IntegrationId => integration;
        public int FetchCount { get; private set; }
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
        {
            FetchCount++;
            if (fail) throw new InvalidOperationException("offline fixture");
            return Task.FromResult(new ProviderSnapshot([], [], []));
        }
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
