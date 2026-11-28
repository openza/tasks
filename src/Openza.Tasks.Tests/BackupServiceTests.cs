using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-backup-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    [Trait("Category", "WindowsSmoke")]
    public void Restore_point_path_stays_under_package_local_state()
    {
        var localState = Path.Combine("package-root", "LocalState");

        Assert.Equal(Path.Combine(localState, "restore-points"), BackupPaths.GetRestorePointDirectory(localState));
        Assert.Equal(Path.Combine(localState, "backups"), BackupPaths.GetLegacyPackageBackupDirectory(localState));
    }

    [Fact]
    [Trait("Category", "WindowsSmoke")]
    public void Redirected_stable_backup_path_resolves_inside_package_cache()
    {
        var localState = Path.Combine(_directory, "Packages", "Openza.OpenzaTasks.Preview_rt595jwp8ay6e", "LocalState");
        var candidates = BackupPaths.GetRedirectedStableBackupDirectories(localState, BackupPaths.PreviewPackageIdentity);

        var candidate = Assert.Single(candidates);
        Assert.Equal(
            Path.Combine(
                _directory,
                "Packages",
                "Openza.OpenzaTasks.Preview_rt595jwp8ay6e",
                "LocalCache",
                "Local",
                "Openza",
                "Tasks Preview",
                "Backups"),
            candidate);
    }

    [Fact]
    [Trait("Category", "WindowsSmoke")]
    public async Task CreateBackup_writes_metadata_and_lists_counts()
    {
        var databasePath = await CreateDatabaseAsync("source.db", taskId: "task_1", projectId: "project_1");
        var service = CreateService(databasePath);

        var backupPath = await service.CreateBackupAsync(BackupReasons.Manual);
        var backups = service.ListBackupInfo();

        var backup = Assert.Single(backups);
        Assert.Equal(backupPath, backup.Path);
        Assert.Equal(BackupReasons.Manual, backup.Reason);
        Assert.Equal(1, backup.TaskCount);
        Assert.Equal(1, backup.ProjectCount);
        Assert.Equal(1, backup.SpaceCount);
        Assert.Equal(BackupIntegrityStatuses.Valid, backup.IntegrityStatus);
        Assert.True(File.Exists($"{backupPath}.json"));
        Assert.Contains("Manual", backup.DisplayName, StringComparison.Ordinal);

        var metadata = JsonSerializer.Deserialize<BackupMetadata>(await File.ReadAllTextAsync($"{backupPath}.json"));
        Assert.NotNull(metadata);
        Assert.Equal("test", metadata.AppFlavor);
        Assert.Equal("1.2.3.4", metadata.AppVersion);
    }

    [Fact]
    public async Task Backup_storage_makes_existing_and_new_files_private_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var databasePath = await CreateDatabaseAsync("permissions-source.db", taskId: "task_permissions");
        var backupDirectory = Path.Combine(_directory, "permission-backups");
        Directory.CreateDirectory(backupDirectory);
        var existingDirectoryMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(backupDirectory, existingDirectoryMode);
        var existingDatabase = Path.Combine(backupDirectory, "existing.db");
        var existingMetadata = $"{existingDatabase}.json";
        await File.WriteAllTextAsync(existingDatabase, "existing");
        await File.WriteAllTextAsync(existingMetadata, "existing");
        var permissiveFileMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
        File.SetUnixFileMode(existingDatabase, permissiveFileMode);
        File.SetUnixFileMode(existingMetadata, permissiveFileMode);

        var service = CreateService(databasePath, backupDirectory);
        _ = service.ListBackups();
        var createdDatabase = await service.CreateBackupAsync(BackupReasons.Manual);

        var privateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(existingDirectoryMode, File.GetUnixFileMode(backupDirectory));
        Assert.Equal(privateFileMode, File.GetUnixFileMode(existingDatabase));
        Assert.Equal(privateFileMode, File.GetUnixFileMode(existingMetadata));
        Assert.Equal(privateFileMode, File.GetUnixFileMode(createdDatabase));
        Assert.Equal(privateFileMode, File.GetUnixFileMode($"{createdDatabase}.json"));
    }

    [Fact]
    public async Task MigrateLegacyBackups_copies_valid_backups_once()
    {
        var databasePath = await CreateDatabaseAsync("legacy-source.db", taskId: "task_legacy");
        var legacyDirectory = Path.Combine(_directory, "legacy");
        var stableDirectory = Path.Combine(_directory, "stable");
        var legacyService = CreateService(databasePath, legacyDirectory);
        var legacyBackup = await legacyService.CreateBackupAsync(BackupReasons.Manual);
        var service = CreateService(databasePath, stableDirectory);

        var firstCopyCount = await service.MigrateLegacyBackupsAsync(legacyDirectory);
        var secondCopyCount = await service.MigrateLegacyBackupsAsync(legacyDirectory);

        var copied = Assert.Single(service.ListBackupInfo());
        Assert.Equal(1, firstCopyCount);
        Assert.Equal(0, secondCopyCount);
        Assert.Equal(BackupReasons.Manual, copied.Reason);
        Assert.NotEqual(legacyBackup, copied.Path);
    }

    [Fact]
    public async Task MigrateLegacyBackups_copies_legacy_and_redirected_restore_points_once()
    {
        var databasePath = await CreateDatabaseAsync("legacy-source.db", taskId: "task_legacy");
        var legacyDirectory = Path.Combine(_directory, "LocalState", "backups");
        var redirectedDirectory = Path.Combine(_directory, "LocalCache", "Local", "Openza", "Tasks Preview", "Backups");
        var restorePointDirectory = Path.Combine(_directory, "LocalState", "restore-points");
        var legacyBackup = await CreateService(databasePath, legacyDirectory).CreateBackupAsync(BackupReasons.Manual);
        Directory.CreateDirectory(redirectedDirectory);
        File.Copy(legacyBackup, Path.Combine(redirectedDirectory, Path.GetFileName(legacyBackup)));
        await File.WriteAllTextAsync(Path.Combine(redirectedDirectory, "not-a-database.db"), "not sqlite");
        var service = CreateService(databasePath, restorePointDirectory);

        var copied = await service.MigrateLegacyBackupsAsync([legacyDirectory, redirectedDirectory]);

        Assert.Equal(1, copied);
        var restorePoint = Assert.Single(service.ListBackupInfo());
        Assert.Equal(BackupReasons.Manual, restorePoint.Reason);
        Assert.Equal(1, restorePoint.TaskCount);
        Assert.DoesNotContain($"{Path.DirectorySeparatorChar}Openza{Path.DirectorySeparatorChar}", restorePoint.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retention_prunes_owned_daily_and_event_backups()
    {
        var databasePath = await CreateDatabaseAsync("retention.db", taskId: "task_retention");
        var service = CreateService(
            databasePath,
            Path.Combine(_directory, "retention-backups"),
            new BackupRetentionPolicy(DailyBackups: 1, WeeklyBackups: 0, MonthlyBackups: 0, EventBackups: 1));

        await service.CreateBackupAsync(BackupReasons.Daily);
        await service.CreateBackupAsync(BackupReasons.Daily);
        await service.CreateBackupAsync(BackupReasons.Daily);
        await service.CreateBackupAsync(BackupReasons.Manual);
        await service.CreateBackupAsync(BackupReasons.Manual);
        await service.CreateBackupAsync(BackupReasons.PreImport);

        var backups = service.ListBackupInfo();

        Assert.Equal(3, backups.Count);
        Assert.Single(backups, backup => backup.Reason == BackupReasons.Daily);
        Assert.Single(backups, backup => backup.Reason == BackupReasons.Manual);
        Assert.Single(backups, backup => backup.Reason == BackupReasons.PreImport);
    }

    [Fact]
    public async Task Manual_retention_does_not_evict_restore_safety_points()
    {
        var databasePath = await CreateDatabaseAsync("safety-retention.db", taskId: "task_safety_retention");
        var service = CreateService(databasePath, Path.Combine(_directory, "safety-retention"),
            new BackupRetentionPolicy(DailyBackups: 0, WeeklyBackups: 0, MonthlyBackups: 0, EventBackups: 1));
        var preRestore = await service.CreateBackupAsync(BackupReasons.PreRestore);
        var preMigration = await service.CreateBackupAsync(BackupReasons.PreMigration);

        for (var index = 0; index < 4; index++)
            await service.CreateBackupAsync(BackupReasons.Manual);

        var backups = service.ListBackupInfo();
        Assert.Contains(backups, backup => backup.Path == preRestore);
        Assert.Contains(backups, backup => backup.Path == preMigration);
        Assert.Single(backups, backup => backup.Reason == BackupReasons.Manual);
    }

    [Fact]
    [Trait("Category", "WindowsSmoke")]
    public async Task Restore_creates_pre_restore_backup_and_replaces_database()
    {
        var currentPath = await CreateDatabaseAsync("current.db", taskId: "task_current");
        var restoreSourcePath = await CreateDatabaseAsync("restore-source.db", taskId: "task_restored");
        var service = CreateService(currentPath);

        await service.RestoreBackupAsync(restoreSourcePath);

        var restoredStore = new SqliteTaskStore(currentPath);
        await restoredStore.InitializeAsync();
        var backups = service.ListBackupInfo();

        Assert.Null(await restoredStore.GetTaskAsync("task_current"));
        Assert.NotNull(await restoredStore.GetTaskAsync("task_restored"));
        Assert.Single(backups, backup => backup.Reason == BackupReasons.PreRestore);
    }

    [Fact]
    public async Task Restore_refuses_pending_provider_writes_in_live_database()
    {
        var currentPath = await CreateDatabaseAsync("provider-queue-live.db", taskId: "task_current");
        var restorePointPath = await CreateDatabaseAsync("provider-queue-snapshot.db", taskId: "task_snapshot");
        var store = new SqliteTaskStore(currentPath);
        await store.QueueCompletionAsync(new PendingCompletion
        {
            Id = "queued-completion", TaskId = "task_current", Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote-current", Completed = true, CreatedAt = DateTimeOffset.UtcNow,
        });
        await store.QueueTaskDateUpdateAsync(new PendingTaskDateUpdate
        {
            Id = "queued-date", TaskId = "task_current", Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote-current", PlannedOn = new DateOnly(2030, 1, 2), CreatedAt = DateTimeOffset.UtcNow,
        });
        var service = CreateService(currentPath);

        var exception = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => service.RestoreBackupAsync(restorePointPath));

        Assert.Equal("pending_provider_writes", exception.Code);
        Assert.NotNull(await store.GetTaskAsync("task_current"));
        Assert.Equal(1, (await store.GetPendingProviderWriteSummaryAsync(IntegrationIds.Todoist)).Completions);
        Assert.Equal(1, (await store.GetPendingProviderWriteSummaryAsync(IntegrationIds.Todoist)).DateUpdates);
        Assert.Empty(service.ListBackupInfo());
    }

    [Fact]
    public async Task Restore_refuses_pending_provider_writes_in_selected_snapshot()
    {
        var currentPath = await CreateDatabaseAsync("provider-queue-current.db", taskId: "task_current");
        var restorePointPath = await CreateDatabaseAsync("provider-queue-point.db", taskId: "task_snapshot");
        var snapshotStore = new SqliteTaskStore(restorePointPath);
        await snapshotStore.QueueTaskDateUpdateAsync(new PendingTaskDateUpdate
        {
            Id = "snapshot-date", TaskId = "task_snapshot", Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote-snapshot", PlannedOn = new DateOnly(2030, 1, 3), CreatedAt = DateTimeOffset.UtcNow,
        });
        var service = CreateService(currentPath);
        var currentStore = new SqliteTaskStore(currentPath);

        var exception = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => service.RestoreBackupAsync(restorePointPath));

        Assert.Equal("pending_provider_writes", exception.Code);
        Assert.NotNull(await currentStore.GetTaskAsync("task_current"));
        Assert.Null(await currentStore.GetTaskAsync("task_snapshot"));
        Assert.Equal(1, (await snapshotStore.GetPendingProviderWriteSummaryAsync(IntegrationIds.Todoist)).DateUpdates);
        Assert.Empty(service.ListBackupInfo());
    }

    [Fact]
    public async Task Restore_refuses_when_personal_cloud_is_connected()
    {
        var currentPath = await CreateDatabaseAsync("connected-cloud-current.db", taskId: "task_current");
        var restorePointPath = await CreateDatabaseAsync("connected-cloud-point.db", taskId: "task_snapshot");
        var service = CreateService(currentPath, personalCloudConnected: () => true);

        var exception = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => service.RestoreBackupAsync(restorePointPath));

        Assert.Equal("cloud_connected", exception.Code);
        Assert.Contains("rollback isn't supported with the cloud on yet", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(await new SqliteTaskStore(currentPath).GetTaskAsync("task_current"));
        Assert.Empty(service.ListBackupInfo());
    }

    [Fact]
    public async Task Restore_refuses_live_pending_personal_cloud_intents_without_replacing_the_database()
    {
        const string instanceId = "instance_00000000000000000000000000000061";
        const string deviceId = "device_00000000000000000000000000000061";
        var currentPath = await CreateDatabaseAsync("live-pending-cloud.db");
        var store = new SqliteTaskStore(currentPath);
        await store.InitializeAsync();
        var service = CreateService(currentPath);
        var restorePoint = await service.CreateBackupAsync();
        var pending = await store.CreateTaskWithPersonalCloudIntentAsync(
            new TaskItem { Id = "live_pending_cloud_task", Title = "Unresolved live edit", SpaceId = SpaceIds.Default },
            instanceId, deviceId);
        var backupCount = service.ListBackupInfo().Count;

        var exception = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => service.RestoreBackupAsync(restorePoint));

        Assert.Equal("pending_intents", exception.Code);
        Assert.NotNull(await store.GetTaskAsync(pending.EntityId));
        Assert.Equal(pending, Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(instanceId, deviceId)));
        Assert.Equal(backupCount, service.ListBackupInfo().Count);
    }

    [Fact]
    public async Task Restore_refuses_pending_personal_cloud_intents_in_snapshot_without_replacing_live_database()
    {
        const string instanceId = "instance_00000000000000000000000000000062";
        const string deviceId = "device_00000000000000000000000000000062";
        var currentPath = await CreateDatabaseAsync("snapshot-pending-cloud.db");
        var store = new SqliteTaskStore(currentPath);
        await store.InitializeAsync();
        var pendingAtSnapshot = await store.CreateTaskWithPersonalCloudIntentAsync(
            new TaskItem { Id = "snapshot_pending_cloud_task", Title = "Snapshot edit", SpaceId = SpaceIds.Default },
            instanceId, deviceId);
        var service = CreateService(currentPath);
        var restorePoint = await service.CreateBackupAsync();
        await AcceptAsync(store, pendingAtSnapshot);
        var liveOnly = await store.CreateTaskWithPersonalCloudIntentAsync(
            new TaskItem { Id = "live_only_cloud_task", Title = "Accepted after snapshot", SpaceId = SpaceIds.Default },
            instanceId, deviceId);
        await AcceptAsync(store, liveOnly);
        var backupCount = service.ListBackupInfo().Count;

        var exception = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => service.RestoreBackupAsync(restorePoint));

        Assert.Equal("pending_intents", exception.Code);
        Assert.NotNull(await store.GetTaskAsync(liveOnly.EntityId));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(instanceId, deviceId));
        Assert.Equal(backupCount, service.ListBackupInfo().Count);
    }

    [Fact]
    public async Task Restore_refuses_incomplete_personal_cloud_sync_without_replacing_live_database()
    {
        const string instanceId = "instance_00000000000000000000000000000063";
        const string deviceId = "device_00000000000000000000000000000063";
        var currentPath = await CreateDatabaseAsync("incomplete-cloud-sync.db", taskId: "task_live");
        var store = new SqliteTaskStore(currentPath);
        await store.InitializeAsync();
        await store.BeginPersonalCloudInitialSyncAsync(instanceId, deviceId);
        var service = CreateService(currentPath);
        var restorePoint = await CreateDatabaseAsync("local-only-restore-point.db", taskId: "task_restore");
        var backupCount = service.ListBackupInfo().Count;

        var exception = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => service.RestoreBackupAsync(restorePoint));

        Assert.Equal("pending_intents", exception.Code);
        Assert.NotNull(await store.GetTaskAsync("task_live"));
        Assert.Equal(PersonalCloudInitialSyncPhase.Preparing,
            (await store.GetPersonalCloudInitialSyncProgressAsync(instanceId, deviceId))!.Phase);
        Assert.Equal(backupCount, service.ListBackupInfo().Count);
    }

    [Fact]
    public async Task Restore_refuses_incompatible_personal_cloud_identities()
    {
        var currentPath = await CreateDatabaseAsync("identity-current.db", taskId: "task_current_identity");
        var restorePoint = await CreateDatabaseAsync("identity-snapshot.db", taskId: "task_snapshot_identity");
        var currentStore = new SqliteTaskStore(currentPath);
        var snapshotStore = new SqliteTaskStore(restorePoint);
        await currentStore.InitializeAsync();
        await snapshotStore.InitializeAsync();
        await currentStore.BeginPersonalCloudInitialSyncAsync("instance_current", "device_current");
        await snapshotStore.BeginPersonalCloudInitialSyncAsync("instance_snapshot", "device_snapshot");
        await currentStore.SetPersonalCloudInitialSyncPhaseAsync("instance_current", "device_current", PersonalCloudInitialSyncPhase.Complete);
        await snapshotStore.SetPersonalCloudInitialSyncPhaseAsync("instance_snapshot", "device_snapshot", PersonalCloudInitialSyncPhase.Complete);
        var service = CreateService(currentPath);

        var exception = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => service.RestoreBackupAsync(restorePoint));

        Assert.Equal("identity_mismatch", exception.Code);
        Assert.NotNull(await currentStore.GetTaskAsync("task_current_identity"));
        Assert.Null(await currentStore.GetTaskAsync("task_snapshot_identity"));
        Assert.Empty(service.ListBackupInfo());
    }

    [Fact]
    public async Task Restore_refuses_cloud_snapshot_when_live_database_is_missing_but_restores_local_snapshot()
    {
        const string instanceId = "instance_00000000000000000000000000000065";
        const string deviceId = "device_00000000000000000000000000000065";
        var cloudSnapshot = await CreateDatabaseAsync("missing-live-cloud-snapshot.db", taskId: "cloud_snapshot_task");
        var cloudStore = new SqliteTaskStore(cloudSnapshot);
        await cloudStore.InitializeAsync();
        await cloudStore.BeginPersonalCloudInitialSyncAsync(instanceId, deviceId);
        await cloudStore.SetPersonalCloudInitialSyncPhaseAsync(instanceId, deviceId, PersonalCloudInitialSyncPhase.Complete);
        var missingCloudDatabase = Path.Combine(_directory, "missing-live-cloud.db");
        var cloudService = CreateService(missingCloudDatabase);

        var exception = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => cloudService.RestoreBackupAsync(cloudSnapshot));

        Assert.Equal("pending_intents", exception.Code);
        Assert.Contains("live database is missing", exception.Message);
        Assert.False(File.Exists(missingCloudDatabase));

        var localSnapshot = await CreateDatabaseAsync("missing-live-local-snapshot.db", taskId: "local_snapshot_task");
        var missingLocalDatabase = Path.Combine(_directory, "missing-live-local.db");
        await CreateService(missingLocalDatabase).RestoreBackupAsync(localSnapshot);

        Assert.True(File.Exists(missingLocalDatabase));
        Assert.NotNull(await new SqliteTaskStore(missingLocalDatabase).GetTaskAsync("local_snapshot_task"));
    }

    [Fact]
    public async Task Restore_preserves_quiescent_cloud_high_water_across_repeated_restores()
    {
        const string instanceId = "instance_00000000000000000000000000000064";
        const string deviceId = "device_00000000000000000000000000000064";
        var currentPath = await CreateDatabaseAsync("sequence-current.db", taskId: "local_before_restore");
        var store = new SqliteTaskStore(currentPath);
        await store.InitializeAsync();
        await store.BeginPersonalCloudInitialSyncAsync(instanceId, deviceId);
        Assert.Equal(1, await store.AdvancePersonalCloudInitialReportSequenceAsync(instanceId, deviceId));
        var first = await store.CreateTaskWithPersonalCloudIntentAsync(
            new TaskItem { Id = "sequence_before_restore", Title = "Before restore", SpaceId = SpaceIds.Default },
            instanceId, deviceId);
        await AcceptAsync(store, first);
        await store.SetPersonalCloudInitialSyncPhaseAsync(instanceId, deviceId, PersonalCloudInitialSyncPhase.Complete);

        var service = CreateService(currentPath);
        var restorePoint = await service.CreateBackupAsync();
        var second = await store.CreateTaskWithPersonalCloudIntentAsync(
            new TaskItem { Id = "sequence_after_backup", Title = "Accepted after backup", SpaceId = SpaceIds.Default },
            instanceId, deviceId);
        await AcceptAsync(store, second);
        Assert.Equal(2, await store.AdvancePersonalCloudInitialReportSequenceAsync(instanceId, deviceId));

        await service.RestoreBackupAsync(restorePoint);

        var restoredStore = new SqliteTaskStore(currentPath);
        await restoredStore.InitializeAsync();
        Assert.NotNull(await restoredStore.GetTaskAsync("local_before_restore"));
        Assert.Null(await restoredStore.GetTaskAsync("sequence_after_backup"));
        var next = await restoredStore.CreateTaskWithPersonalCloudIntentAsync(
            new TaskItem { Id = "sequence_after_first_restore", Title = "After first restore", SpaceId = SpaceIds.Default },
            instanceId, deviceId);
        Assert.Equal(second.DeviceSequence + 1, next.DeviceSequence);
        Assert.Equal(3, await restoredStore.AdvancePersonalCloudInitialReportSequenceAsync(instanceId, deviceId));
        await AcceptAsync(restoredStore, next);

        await service.RestoreBackupAsync(restorePoint);

        var afterRepeatedRestore = new SqliteTaskStore(currentPath);
        var following = await afterRepeatedRestore.CreateTaskWithPersonalCloudIntentAsync(
            new TaskItem { Id = "sequence_after_second_restore", Title = "After second restore", SpaceId = SpaceIds.Default },
            instanceId, deviceId);
        Assert.Equal(next.DeviceSequence + 1, following.DeviceSequence);
        Assert.Equal(4, await afterRepeatedRestore.AdvancePersonalCloudInitialReportSequenceAsync(instanceId, deviceId));
    }

    private static async Task AcceptAsync(SqliteTaskStore store, PersonalCloudK1OutboxEntry entry)
    {
        using var document = JsonDocument.Parse(entry.CanonicalJson);
        var baseVersion = document.RootElement.GetProperty("baseEntityVersion");
        await store.AcknowledgePersonalCloudMutationAsync(entry.InstanceId, entry.DeviceId, PersonalCloudK0Codec.Serialize(new K0MutationReceipt
        {
            ProtocolVersion = 1,
            Product = "tasks",
            MutationId = entry.MutationId,
            EntityId = entry.EntityId,
            Outcome = "accepted",
            EntityVersion = baseVersion.ValueKind == JsonValueKind.Null ? 1 : baseVersion.GetInt64() + 1,
            ChangeSeq = entry.DeviceSequence,
            ServerAcceptedAt = "2026-11-28T12:00:00.000Z",
        }));
    }

    [Fact]
    public async Task Restore_holds_the_configured_database_replacement_lease()
    {
        var currentPath = await CreateDatabaseAsync("leased-current.db", taskId: "task_current");
        var restoreSourcePath = await CreateDatabaseAsync("leased-source.db", taskId: "task_restored");
        var lease = new TrackingDisposable();
        var service = new BackupService(
            currentPath,
            Path.Combine(_directory, "leased-backups"),
            context: new BackupContext("test.identity", "test", "1.2.3.4"),
            databaseReplacementLeaseFactory: () => lease);

        await service.RestoreBackupAsync(restoreSourcePath);

        Assert.True(lease.WasDisposed);
        Assert.NotNull(await new SqliteTaskStore(currentPath).GetTaskAsync("task_restored"));
    }

    [Fact]
    public async Task Restore_does_not_prune_selected_backup_before_copying_it()
    {
        var currentPath = await CreateDatabaseAsync("current-prune.db", taskId: "task_current");
        var restoreSourcePath = await CreateDatabaseAsync("restore-prune-source.db", taskId: "task_restored");
        var backupDirectory = Path.Combine(_directory, "restore-prune-backups");
        var permissiveService = CreateService(
            restoreSourcePath,
            backupDirectory,
            new BackupRetentionPolicy(DailyBackups: 10, WeeklyBackups: 0, MonthlyBackups: 0, EventBackups: 10));
        var selectedBackup = await permissiveService.CreateBackupAsync(BackupReasons.Manual);
        await permissiveService.CreateBackupAsync(BackupReasons.Manual);
        await permissiveService.CreateBackupAsync(BackupReasons.PreImport);
        var restoringService = CreateService(
            currentPath,
            backupDirectory,
            new BackupRetentionPolicy(DailyBackups: 0, WeeklyBackups: 0, MonthlyBackups: 0, EventBackups: 1));

        await restoringService.RestoreBackupAsync(selectedBackup);

        var restoredStore = new SqliteTaskStore(currentPath);
        await restoredStore.InitializeAsync();
        Assert.True(File.Exists(selectedBackup));
        Assert.NotNull(await restoredStore.GetTaskAsync("task_restored"));
    }

    [Fact]
    public async Task Restore_recovers_hot_rollback_journal_before_read_only_preflight()
    {
        var templatePath = await CreateDatabaseAsync("hot-journal-template.db", taskId: "task_current");
        var restoreSourcePath = await CreateDatabaseAsync("hot-journal-restore.db", taskId: "task_restored");
        var livePath = Path.Combine(_directory, "hot-journal-live.db");
        var pristinePath = Path.Combine(_directory, "hot-journal-pristine.db");
        var templateStore = new SqliteTaskStore(templatePath);
        for (var index = 0; index < 128; index++)
            await templateStore.UpsertTaskAsync(new TaskItem { Id = $"task_{index:D3}", Title = $"Task {index}" });
        SqliteConnection.ClearAllPools();
        File.Copy(templatePath, pristinePath);
        var journalPath = $"{templatePath}-journal";
        await using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = templatePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            await writer.OpenAsync();
            async Task ExecuteAsync(string sql)
            {
                await using var command = writer.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }

            await ExecuteAsync("PRAGMA journal_mode=DELETE");
            await ExecuteAsync("PRAGMA synchronous=FULL");
            await ExecuteAsync("PRAGMA cache_size=1");
            await ExecuteAsync("PRAGMA cache_spill=ON");
            await ExecuteAsync("BEGIN EXCLUSIVE");
            for (var index = 0; index < 128; index++)
                await ExecuteAsync($"UPDATE tasks SET title='Uncommitted {index}' WHERE id='task_{index:D3}'");
            Assert.False(File.ReadAllBytes(templatePath).SequenceEqual(File.ReadAllBytes(pristinePath)));
            Assert.True(File.Exists(journalPath));
            Assert.True(new FileInfo(journalPath).Length > 0);
            File.Copy(templatePath, livePath);
            File.Copy(journalPath, $"{livePath}-journal");
            await writer.CloseAsync();
        }

        var readOnlyFailure = await Record.ExceptionAsync(async () =>
        {
            await using var readOnly = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = livePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await readOnly.OpenAsync();
            await using var command = readOnly.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            await command.ExecuteScalarAsync();
        });
        Assert.IsType<SqliteException>(readOnlyFailure);

        await CreateService(livePath).RestoreBackupAsync(restoreSourcePath);

        Assert.False(File.Exists($"{livePath}-journal"));
        var restoredStore = new SqliteTaskStore(livePath);
        await restoredStore.InitializeAsync();
        Assert.NotNull(await restoredStore.GetTaskAsync("task_restored"));
    }

    [Fact]
    public async Task FindLatestRestorableBackup_returns_candidate_for_fresh_database()
    {
        var backupSourcePath = await CreateDatabaseAsync("backup-source.db", taskId: "task_backup");
        var freshPath = await CreateDatabaseAsync("fresh.db");
        var backupDirectory = Path.Combine(_directory, "fresh-backups");
        await CreateService(backupSourcePath, backupDirectory).CreateBackupAsync(BackupReasons.Daily);
        var service = CreateService(freshPath, backupDirectory);

        var isFresh = await service.IsCurrentDatabaseFreshAsync();
        var candidate = await service.FindLatestRestorableBackupAsync();

        Assert.True(isFresh);
        Assert.NotNull(candidate);
        Assert.Equal(BackupReasons.Daily, candidate.Reason);
    }

    [Fact]
    public async Task FindLatestRestorableBackup_skips_cloud_snapshot_when_live_database_is_missing()
    {
        const string instanceId = "instance_00000000000000000000000000000066";
        const string deviceId = "device_00000000000000000000000000000066";
        var backupDirectory = Path.Combine(_directory, "missing-live-discovery-backups");
        var localPath = await CreateDatabaseAsync("discovery-local.db", taskId: "local_snapshot_task");
        var localSnapshot = await CreateService(localPath, backupDirectory).CreateBackupAsync(BackupReasons.Manual);
        await Task.Delay(25);
        var cloudPath = await CreateDatabaseAsync("discovery-cloud.db", taskId: "cloud_snapshot_task");
        var cloudStore = new SqliteTaskStore(cloudPath);
        await cloudStore.InitializeAsync();
        await cloudStore.BeginPersonalCloudInitialSyncAsync(instanceId, deviceId);
        await cloudStore.SetPersonalCloudInitialSyncPhaseAsync(instanceId, deviceId, PersonalCloudInitialSyncPhase.Complete);
        var cloudSnapshot = await CreateService(cloudPath, backupDirectory).CreateBackupAsync(BackupReasons.Manual);
        var missingDatabasePath = Path.Combine(_directory, "discovery-live-missing.db");
        var service = CreateService(missingDatabasePath, backupDirectory);

        Assert.Equal(cloudSnapshot, service.ListBackupInfo()[0].Path);
        var candidate = await service.FindLatestRestorableBackupAsync();

        Assert.Equal(localSnapshot, candidate?.Path);
        Assert.False(File.Exists(missingDatabasePath));
    }

    [Fact]
    public async Task FindLatestRestorableBackup_skips_snapshot_with_pending_provider_writes_when_live_database_exists()
    {
        var livePath = await CreateDatabaseAsync("discovery-live-empty.db");
        var snapshotPath = await CreateDatabaseAsync("discovery-snapshot-pending.db", taskId: "task_snapshot");
        var snapshotStore = new SqliteTaskStore(snapshotPath);
        await snapshotStore.QueueTaskDateUpdateAsync(new PendingTaskDateUpdate
        {
            Id = "snapshot-date", TaskId = "task_snapshot", Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote-snapshot", PlannedOn = new DateOnly(2030, 1, 3), CreatedAt = DateTimeOffset.UtcNow,
        });
        var backupDirectory = Path.Combine(_directory, "existing-live-discovery-backups");
        await CreateService(snapshotPath, backupDirectory).CreateBackupAsync(BackupReasons.Manual);

        var candidate = await CreateService(livePath, backupDirectory).FindLatestRestorableBackupAsync();

        Assert.Null(candidate);
        Assert.True(File.Exists(livePath));
    }

    [Fact]
    public async Task ShouldCreatePreMigrationBackup_detects_older_database_with_user_tables()
    {
        var databasePath = await CreateDatabaseAsync("older-schema.db", taskId: "task_old");
        await SetUserVersionAsync(databasePath, SqliteTaskStore.CurrentSchemaVersion - 1);
        var service = CreateService(databasePath);

        var shouldCreateBackup = await service.ShouldCreatePreMigrationBackupAsync(SqliteTaskStore.CurrentSchemaVersion);

        Assert.True(shouldCreateBackup);
    }

    [Fact]
    public async Task Restore_rejects_corrupt_database()
    {
        var databasePath = await CreateDatabaseAsync("valid.db", taskId: "task_1");
        var corruptPath = Path.Combine(_directory, "corrupt.db");
        await File.WriteAllTextAsync(corruptPath, "not sqlite");
        var service = CreateService(databasePath);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreBackupAsync(corruptPath));
    }

    [Fact]
    public async Task List_export_and_delete_backups()
    {
        var databasePath = await CreateDatabaseAsync("export.db", taskId: "task_1");
        var service = CreateService(databasePath);

        var backupPath = await service.CreateBackupAsync(BackupReasons.Manual);
        var secondBackupPath = await service.CreateBackupAsync(BackupReasons.Manual);
        var backups = service.ListBackupInfo();

        Assert.Equal(2, backups.Count);
        var backup = backups.Single(info => info.Path == backupPath);
        Assert.Equal(backupPath, backup.Path);
        Assert.NotEmpty(backup.DisplayName);
        Assert.NotEqual(backupPath, secondBackupPath);

        var exportedPath = Path.Combine(_directory, "exported.db");
        await service.ExportBackupAsync(backup.Path, exportedPath);
        Assert.True(File.Exists(exportedPath));

        await service.DeleteBackupAsync(backup.Path);
        Assert.DoesNotContain(service.ListBackupInfo(), info => info.Path == backup.Path);
        Assert.Contains(service.ListBackupInfo(), info => info.Path == secondBackupPath);
        Assert.False(File.Exists($"{backup.Path}.json"));
    }

    private BackupService CreateService(
        string databasePath,
        string? backupDirectory = null,
        BackupRetentionPolicy? retentionPolicy = null,
        Func<bool>? personalCloudConnected = null) =>
        new(
            databasePath,
            backupDirectory ?? Path.Combine(_directory, "backups"),
            retentionPolicy,
            new BackupContext("test.identity", "test", "1.2.3.4"),
            personalCloudConnected: personalCloudConnected);

    private sealed class TrackingDisposable : IDisposable
    {
        public bool WasDisposed { get; private set; }
        public void Dispose() => WasDisposed = true;
    }

    private async Task<string> CreateDatabaseAsync(string fileName, string? taskId = null, string? projectId = null)
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, fileName);
        var store = new SqliteTaskStore(databasePath);
        await store.InitializeAsync();
        if (projectId is not null)
        {
            await store.UpsertProjectAsync(new ProjectItem
            {
                Id = projectId,
                Name = "Backed up project",
            });
        }

        if (taskId is not null)
        {
            await store.UpsertTaskAsync(new TaskItem
            {
                Id = taskId,
                Title = "Backed up task",
                ProjectId = projectId,
            });
        }

        SqliteConnection.ClearAllPools();
        return databasePath;
    }

    private static async Task SetUserVersionAsync(string databasePath, int userVersion)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {userVersion}";
        await command.ExecuteNonQueryAsync();
        SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        TestDirectory.Delete(_directory);
    }
}
