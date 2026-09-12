using System.Net;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed class DesktopParityIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-parity-integrations", Guid.NewGuid().ToString("N"));
    private async Task<SqliteTaskStore> StoreAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "task", Title = "Original", SpaceId = SpaceIds.Default, IntegrationId = IntegrationIds.Local });
        return store;
    }

    [Fact]
    public async Task Cloud_restore_validates_passphrase_and_keeps_safety_backup()
    {
        var store = await StoreAsync();
        var backup = new BackupService(store.DatabasePath, Path.Combine(_directory, "restore"), context: new("test", "dev", "0.0.1"));
        var provider = new MemoryCloudProvider();
        var preferences = new DesktopPreferencesStore(Path.Combine(_directory, "settings.json"));
        await preferences.SaveAsync(new() { OneDriveAccount = new("test-account", "test-user"), AutomaticRestorePointsEnabled = false });
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences,
            cloudService: new CloudBackupService(provider), backupService: backup);
        await vm.SetCloudEncryptionAsync(true, "test-passphrase");
        await vm.CreateRestorePointAsync();
        await vm.SetCloudEnabledAsync(true);
        await vm.RefreshCloudBackupsAsync();
        var uploaded = Assert.Single(vm.CloudBackups);
        Assert.Equal(CloudBackupEncryptionModes.Passphrase, uploaded.EncryptionMode);
        await store.UpsertTaskAsync((await store.GetTaskAsync("task"))! with { Title = "After backup" });

        await vm.RestoreCloudBackupAsync(uploaded, "wrong-passphrase");
        Assert.Equal("After backup", (await store.GetTaskAsync("task"))!.Title);
        Assert.Contains("failed", vm.CloudStatus);
        await vm.RestoreCloudBackupAsync(uploaded, "test-passphrase");
        Assert.Equal("Original", (await store.GetTaskAsync("task"))!.Title);
        var safety = Assert.Single(backup.ListBackupInfo(), item => item.Reason == BackupReasons.PreRestore);
        var safetyStore = new SqliteTaskStore(safety.Path);
        Assert.Equal("After backup", (await safetyStore.GetTaskAsync("task"))!.Title);
    }

    [Fact]
    public async Task Selected_backup_export_uses_historical_snapshot_not_current_database()
    {
        var store = await StoreAsync();
        var backup = new BackupService(store.DatabasePath, Path.Combine(_directory, "restore"));
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), backupService: backup);
        await vm.CreateRestorePointAsync();
        await store.UpsertTaskAsync((await store.GetTaskAsync("task"))! with { Title = "Changed after snapshot" });
        var exportedPath = Path.Combine(_directory, "export.db");
        await vm.ExportSelectedRestorePointAsync(exportedPath);
        Assert.Equal("Original", (await new SqliteTaskStore(exportedPath).GetTaskAsync("task"))!.Title);
        Assert.Equal("Changed after snapshot", (await store.GetTaskAsync("task"))!.Title);
    }

    [Fact]
    public async Task GitHub_replacement_updates_one_link_on_original_task_and_removal_does_not_delete_issue()
    {
        var store = await StoreAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "other", Title = "Other", SpaceId = SpaceIds.Default });
        await store.UpsertTaskExternalLinkAsync(new TaskExternalLinkInfo
        {
            Id = "existing", TaskId = "task", IntegrationId = IntegrationIds.GitHub, ConnectionId = GitHubIssueService.DefaultConnectionId,
            Kind = TaskExternalLinkKinds.Issue, ExternalId = "example/repo#1", Url = "https://github.com/example/repo/issues/1",
        });
        var credentials = new InMemoryCredentialStore(); await credentials.SaveAsync(GitHubIssueService.TokenKey, "test-token");
        var handler = new GitHubHandler();
        var vm = new MainWindowViewModel(store, credentials, gitHubIssueService: new GitHubIssueService(new HttpClient(handler)));
        await vm.SelectNavigationAsync(vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await vm.SelectTaskAsync(vm.Tasks.Single(item => item.Task.Id == "other"));
        var draft = new GitHubIssueDraft("task", "Original", "Body", "example/repo", [], "existing");
        await vm.CreateGitHubIssueAsync(draft, new("example", "repo", "Reviewed title", "Reviewed body", ["bug"], []));
        var link = Assert.Single(await store.GetTaskExternalLinksAsync("task"));
        Assert.Equal("existing", link.Id); Assert.Equal("example/repo#2", link.ExternalId);
        Assert.Empty(await store.GetTaskExternalLinksAsync("other"));
        Assert.Contains("Reviewed title", handler.PostBody); Assert.Contains("bug", handler.PostBody);
        await vm.RemoveGitHubLinkAsync(link);
        Assert.Empty(await store.GetTaskExternalLinksAsync("task"));
        Assert.Equal(new[] { HttpMethod.Post }, handler.Methods);
    }

    [Fact]
    public async Task Markdown_export_contract_includes_children_and_completed_tasks_in_selected_space()
    {
        var store = await StoreAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "child", Title = "Child", ParentId = "task", SpaceId = SpaceIds.Default });
        await store.UpsertTaskAsync(new TaskItem { Id = "completed", Title = "Completed", Status = TaskItemStatus.Completed, SpaceId = SpaceIds.Default });
        await store.UpsertSpaceAsync(new SpaceItem { Id = "other", Name = "Other" });
        await store.UpsertTaskAsync(new TaskItem { Id = "excluded", Title = "Outside scope", SpaceId = "other" });
        var shared = await store.GetTasksAsync(TaskQuery.ForMarkdownExport(SpaceIds.Default));
        Assert.Equal(new[] { "child", "completed", "task" }, shared.Select(task => task.Id).Order());
        var preferences = new DesktopPreferencesStore(Path.Combine(_directory, "settings.json"));
        await preferences.SaveAsync(new() { AutomaticRestorePointsEnabled = false, AutomaticSyncEnabled = false });
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
        await vm.InitializeAsync();
        await vm.SelectSpaceAsync(vm.SpaceItems.Single(item => item.SpaceId == SpaceIds.Default));
        var output = Path.Combine(_directory, "tasks.md");
        await vm.ExportMarkdownAsync(output);
        var markdown = await File.ReadAllTextAsync(output);
        Assert.Contains("Child", markdown); Assert.Contains("Completed", markdown); Assert.DoesNotContain("Outside scope", markdown);
    }

    [Fact]
    public async Task Failed_restore_returns_failure_and_preserves_database()
    {
        var store = await StoreAsync();
        var invalid = Path.Combine(_directory, "invalid.db");
        await File.WriteAllTextAsync(invalid, "invalid database");
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore());
        Assert.False(await vm.RestoreDatabaseAsync(invalid));
        Assert.True(vm.IsStatusMessagePersistent);
        Assert.Equal("Original", (await store.GetTaskAsync("task"))!.Title);
    }

    [Fact]
    public async Task GitHub_creation_preserves_warning_when_saving_default_repository_fails()
    {
        var store = await StoreAsync();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_settings BEFORE INSERT ON provider_connections BEGIN SELECT RAISE(FAIL, 'Preference storage failure'); END;";
        await command.ExecuteNonQueryAsync();
        var credentials = new InMemoryCredentialStore(); await credentials.SaveAsync(GitHubIssueService.TokenKey, "test-token");
        var handler = new GitHubHandler();
        var vm = new MainWindowViewModel(store, credentials, gitHubIssueService: new GitHubIssueService(new HttpClient(handler)));
        var draft = new GitHubIssueDraft("task", "Original", "Body", "", [], null);
        var url = await vm.CreateGitHubIssueAsync(draft, new("example", "repo", "Reviewed title", "Body", [], []));
        Assert.Equal("https://github.com/example/repo/issues/2", url);
        Assert.Single(await store.GetTaskExternalLinksAsync("task"));
        Assert.True(vm.IsStatusMessagePersistent);
        Assert.Contains("default repository could not be saved", vm.StatusMessage);
        Assert.Single(handler.Methods);
    }

    private sealed class GitHubHandler : HttpMessageHandler
    {
        public string PostBody { get; private set; } = "";
        public List<HttpMethod> Methods { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            Assert.Equal("/repos/example/repo/issues", request.RequestUri!.AbsolutePath);
            PostBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"id":2,"number":2,"title":"Reviewed title","html_url":"https://github.com/example/repo/issues/2","state":"open"}"""),
            };
        }
    }

    private sealed class MemoryCloudProvider : ICloudBackupProvider
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        public Task UploadFileAsync(string remotePath, string localPath, string contentType, CancellationToken cancellationToken = default)
        { _files[remotePath] = File.ReadAllBytes(localPath); return Task.CompletedTask; }
        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default)
        { Directory.CreateDirectory(Path.GetDirectoryName(localPath)!); File.WriteAllBytes(localPath, _files[remotePath]); return Task.CompletedTask; }
        public Task<IReadOnlyList<CloudBackupInfo>> ListBackupsAsync(string appFlavor, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudBackupInfo>>(_files.Where(pair => pair.Key.StartsWith(CloudBackupPaths.BackupDirectory(appFlavor), StringComparison.Ordinal) && pair.Key.EndsWith(".json", StringComparison.Ordinal))
                .Select(pair => JsonSerializer.Deserialize<CloudBackupInfo>(pair.Value)!).ToList());
        public Task DeleteBackupAsync(CloudBackupInfo backup, CancellationToken cancellationToken = default)
        { _files.Remove(backup.CloudPath); _files.Remove(backup.MetadataPath); return Task.CompletedTask; }
        public Task UploadManifestAsync(string appFlavor, CloudBackupManifest manifest, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
