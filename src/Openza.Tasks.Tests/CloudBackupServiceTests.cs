using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Tests;

public sealed class CloudBackupServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-cloud-backup-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UploadBackup_returns_null_when_cloud_backup_is_disabled()
    {
        var backup = await CreateBackupAsync("disabled.db", "task_disabled");
        var provider = new FakeCloudBackupProvider();
        var service = new CloudBackupService(provider);

        var result = await service.UploadBackupAsync(
            backup,
            new CloudBackupOptions(false, false, null, BackupPaths.ProductionFlavor));

        Assert.Null(result);
        Assert.Empty(provider.UploadedPaths);
    }

    [Fact]
    public async Task UploadBackup_encrypts_and_restores_with_passphrase()
    {
        var backup = await CreateBackupAsync("encrypted.db", "task_encrypted");
        var provider = new FakeCloudBackupProvider();
        var service = new CloudBackupService(provider);

        var uploaded = await service.UploadBackupAsync(
            backup,
            new CloudBackupOptions(true, true, "correct horse battery staple", BackupPaths.ProductionFlavor));

        Assert.NotNull(uploaded);
        Assert.Equal(CloudBackupEncryptionModes.Passphrase, uploaded.EncryptionMode);
        Assert.EndsWith(".db.gz.enc", uploaded.CloudPath, StringComparison.Ordinal);
        Assert.Contains(uploaded.MetadataPath, provider.UploadedPaths);
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            service.DownloadBackupAsync(uploaded, Path.Combine(_directory, "wrong.db"), "wrong passphrase"));

        var restoredPath = Path.Combine(_directory, "restored.db");
        await service.DownloadBackupAsync(uploaded, restoredPath, "correct horse battery staple");
        var restoredStore = new SqliteTaskStore(restoredPath);
        await restoredStore.InitializeAsync();

        Assert.NotNull(await restoredStore.GetTaskAsync("task_encrypted"));
    }

    [Fact]
    public async Task UploadBackup_without_passphrase_uses_onedrive_protected_bundle()
    {
        var backup = await CreateBackupAsync("onedrive-protected.db", "task_plain");
        var provider = new FakeCloudBackupProvider();
        var service = new CloudBackupService(provider);

        var uploaded = await service.UploadBackupAsync(
            backup,
            new CloudBackupOptions(true, false, null, BackupPaths.ProductionFlavor));

        Assert.NotNull(uploaded);
        Assert.Equal(CloudBackupEncryptionModes.None, uploaded.EncryptionMode);
        Assert.EndsWith(".db.gz", uploaded.CloudPath, StringComparison.Ordinal);
        Assert.Contains(uploaded.CloudPath, provider.UploadedPaths);
    }

    [Fact]
    public async Task DownloadBackup_rejects_payload_when_content_hash_does_not_match_metadata()
    {
        var firstBackup = await CreateBackupAsync("hash-first.db", "task_first");
        var secondBackup = await CreateBackupAsync("hash-second.db", "task_second");
        var provider = new FakeCloudBackupProvider();
        var service = new CloudBackupService(provider);
        var firstUploaded = await service.UploadBackupAsync(
            firstBackup,
            new CloudBackupOptions(true, false, null, BackupPaths.ProductionFlavor));
        var secondUploaded = await service.UploadBackupAsync(
            secondBackup,
            new CloudBackupOptions(true, false, null, BackupPaths.ProductionFlavor));
        Assert.NotNull(firstUploaded);
        Assert.NotNull(secondUploaded);
        provider.Files[firstUploaded.CloudPath] = provider.Files[secondUploaded.CloudPath];
        var destinationPath = Path.Combine(_directory, "hash-mismatch.db");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadBackupAsync(firstUploaded, destinationPath, null));

        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task Retention_prunes_only_owned_cloud_backups()
    {
        var provider = new FakeCloudBackupProvider();
        var service = new CloudBackupService(
            provider,
            new BackupRetentionPolicy(DailyBackups: 0, WeeklyBackups: 0, MonthlyBackups: 0, EventBackups: 1));
        var first = await CreateBackupAsync("first.db", "task_first");
        await service.UploadBackupAsync(first, new CloudBackupOptions(true, false, null, BackupPaths.ProductionFlavor));
        await Task.Delay(10);
        var second = await CreateBackupAsync("second.db", "task_second");
        await service.UploadBackupAsync(second, new CloudBackupOptions(true, false, null, BackupPaths.ProductionFlavor));
        provider.Files["manual/unknown.db.gz"] = Encoding.UTF8.GetBytes("not owned by Openza metadata");

        var backups = await provider.ListBackupsAsync(BackupPaths.ProductionFlavor);

        Assert.Single(backups);
        Assert.Contains("task_second", await ReadRestoredTaskIdsAsync(service, backups.Single()));
        Assert.NotEmpty(provider.DeletedBackupIds);
        Assert.True(provider.Files.ContainsKey("manual/unknown.db.gz"));
    }

    [Fact]
    public async Task UploadPendingBackups_uses_isolated_dev_flavor()
    {
        var backup = await CreateBackupAsync("dev.db", "task_dev");
        var provider = new FakeCloudBackupProvider();
        var service = new CloudBackupService(provider);

        var uploaded = await service.UploadPendingBackupsAsync(
            [backup],
            new CloudBackupOptions(true, false, null, BackupPaths.DevFlavor));

        var cloudBackup = Assert.Single(uploaded);
        Assert.True(CloudBackupService.IsAvailableForAppFlavor(BackupPaths.DevFlavor));
        Assert.StartsWith("v1/dev/backups/", cloudBackup.CloudPath, StringComparison.Ordinal);
        Assert.DoesNotContain("/production/", cloudBackup.CloudPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OneDriveProvider_uses_app_folder_scope_and_isolated_paths()
    {
        Assert.Contains(OneDriveBackupProvider.AppFolderScope, OneDriveBackupProvider.Scopes);
        Assert.DoesNotContain("https://graph.microsoft.com/Files.ReadWrite.All", OneDriveBackupProvider.Scopes);
        Assert.Equal("v1/production/manifest.json", CloudBackupPaths.ManifestPath(BackupPaths.ProductionFlavor));
        Assert.Equal("v1/production/backups", CloudBackupPaths.BackupDirectory(BackupPaths.ProductionFlavor));
        Assert.Equal("v1/dev/backups", CloudBackupPaths.BackupDirectory(BackupPaths.DevFlavor));
    }

    private async Task<BackupInfo> CreateBackupAsync(string databaseFileName, string taskId)
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, databaseFileName);
        var store = new SqliteTaskStore(databasePath);
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = taskId,
            Title = taskId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var service = new BackupService(
            databasePath,
            Path.Combine(_directory, "local-backups"),
            BackupRetentionPolicy.Default,
            new BackupContext("test.identity", BackupPaths.ProductionFlavor, "1.0.0.0"));
        var backupPath = await service.CreateBackupAsync(BackupReasons.Manual);
        return Assert.Single(service.ListBackupInfo(), info => info.Path == backupPath);
    }

    private async Task<IReadOnlyList<string>> ReadRestoredTaskIdsAsync(CloudBackupService service, CloudBackupInfo backup)
    {
        var restoredPath = Path.Combine(_directory, $"{backup.BackupId}.db");
        await service.DownloadBackupAsync(backup, restoredPath, null);
        var store = new SqliteTaskStore(restoredPath);
        await store.InitializeAsync();
        return (await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }))
            .Select(task => task.Id)
            .ToList();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                SqliteConnection.ClearAllPools();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100);
            }
        }
    }

    private sealed class FakeCloudBackupProvider : ICloudBackupProvider
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> UploadedPaths { get; } = [];
        public List<string> DeletedBackupIds { get; } = [];

        public Task UploadFileAsync(string remotePath, string localPath, string contentType, CancellationToken cancellationToken = default)
        {
            Files[remotePath] = File.ReadAllBytes(localPath);
            UploadedPaths.Add(remotePath);
            return Task.CompletedTask;
        }

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? ".");
            File.WriteAllBytes(localPath, Files[remotePath]);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CloudBackupInfo>> ListBackupsAsync(string appFlavor, CancellationToken cancellationToken = default)
        {
            var backups = Files
                .Where(pair => pair.Key.StartsWith(CloudBackupPaths.BackupDirectory(appFlavor), StringComparison.OrdinalIgnoreCase))
                .Where(pair => pair.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(pair => JsonSerializer.Deserialize<CloudBackupInfo>(Encoding.UTF8.GetString(pair.Value)))
                .Where(info => info is not null)
                .Cast<CloudBackupInfo>()
                .OrderByDescending(info => info.CreatedAt)
                .ToList();
            return Task.FromResult<IReadOnlyList<CloudBackupInfo>>(backups);
        }

        public Task DeleteBackupAsync(CloudBackupInfo backup, CancellationToken cancellationToken = default)
        {
            DeletedBackupIds.Add(backup.BackupId);
            Files.Remove(backup.CloudPath);
            Files.Remove(backup.MetadataPath);
            return Task.CompletedTask;
        }

        public Task UploadManifestAsync(string appFlavor, CloudBackupManifest manifest, CancellationToken cancellationToken = default)
        {
            Files[CloudBackupPaths.ManifestPath(appFlavor)] = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            return Task.CompletedTask;
        }
    }
}
