using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Openza.Tasks.Core.Services;

public interface ICloudBackupProvider
{
    Task UploadFileAsync(string remotePath, string localPath, string contentType, CancellationToken cancellationToken = default);

    Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudBackupInfo>> ListBackupsAsync(string appFlavor, CancellationToken cancellationToken = default);

    Task DeleteBackupAsync(CloudBackupInfo backup, CancellationToken cancellationToken = default);

    Task UploadManifestAsync(string appFlavor, CloudBackupManifest manifest, CancellationToken cancellationToken = default);
}

public sealed class CloudBackupService(
    ICloudBackupProvider provider,
    BackupRetentionPolicy? retentionPolicy = null)
{
    private const int EncryptionSaltBytes = 16;
    private const int EncryptionNonceBytes = 12;
    private const int EncryptionTagBytes = 16;
    private const int EncryptionKeyBytes = 32;
    private const int KdfIterations = 210_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public BackupRetentionPolicy RetentionPolicy { get; } = retentionPolicy ?? BackupRetentionPolicy.Default;

    public static bool IsAvailableForAppFlavor(string appFlavor) =>
        string.Equals(appFlavor, BackupPaths.ProductionFlavor, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(appFlavor, BackupPaths.DevFlavor, StringComparison.OrdinalIgnoreCase);

    public async Task<CloudBackupInfo?> UploadBackupAsync(
        BackupInfo backup,
        CloudBackupOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Enabled || !IsAvailableForAppFlavor(options.AppFlavor))
        {
            return null;
        }

        if (!File.Exists(backup.Path))
        {
            throw new FileNotFoundException("The local backup file no longer exists.", backup.Path);
        }

        if (options.Encrypt && string.IsNullOrWhiteSpace(options.Passphrase))
        {
            throw new InvalidOperationException("A cloud backup passphrase is required before encrypted backups can upload.");
        }

        var backupId = BuildBackupId(backup);
        var existing = await provider.ListBackupsAsync(options.AppFlavor, cancellationToken).ConfigureAwait(false);
        var alreadyUploaded = existing.FirstOrDefault(item => string.Equals(item.BackupId, backupId, StringComparison.Ordinal));
        if (alreadyUploaded is not null)
        {
            return alreadyUploaded;
        }

        var tempDirectory = CreateTempDirectory();
        try
        {
            var stem = BuildRemoteFileStem(backup, backupId);
            var compressedPath = Path.Combine(tempDirectory, $"{stem}.db.gz");
            await CompressAsync(backup.Path, compressedPath, cancellationToken).ConfigureAwait(false);

            var contentHash = ComputeFileHash(backup.Path);
            var compressedSize = new FileInfo(compressedPath).Length;
            var metadata = CreateMetadata(backup, options, backupId, stem, contentHash, compressedSize);
            var uploadPath = compressedPath;
            if (options.Encrypt)
            {
                uploadPath = Path.Combine(tempDirectory, $"{stem}.db.gz.enc");
                var encryption = await EncryptFileAsync(compressedPath, uploadPath, options.Passphrase!, cancellationToken).ConfigureAwait(false);
                metadata.EncryptionMode = CloudBackupEncryptionModes.Passphrase;
                metadata.Kdf = "PBKDF2-HMAC-SHA256";
                metadata.KdfIterations = KdfIterations;
                metadata.Salt = Convert.ToBase64String(encryption.Salt);
                metadata.Nonce = Convert.ToBase64String(encryption.Nonce);
                metadata.Tag = Convert.ToBase64String(encryption.Tag);
                metadata.EncryptedSize = new FileInfo(uploadPath).Length;
                metadata.CloudPath = CloudBackupPaths.BackupFilePath(options.AppFlavor, $"{stem}.db.gz.enc");
            }

            var metadataPath = CloudBackupPaths.BackupFilePath(options.AppFlavor, $"{stem}.json");
            metadata.MetadataPath = metadataPath;
            var localMetadataPath = Path.Combine(tempDirectory, $"{stem}.json");
            await WriteJsonAsync(localMetadataPath, metadata, cancellationToken).ConfigureAwait(false);

            await provider.UploadFileAsync(metadata.CloudPath, uploadPath, ContentTypeFor(metadata.CloudPath), cancellationToken).ConfigureAwait(false);
            await provider.UploadFileAsync(metadata.MetadataPath, localMetadataPath, "application/json", cancellationToken).ConfigureAwait(false);
            await RefreshManifestAndPruneAsync(options.AppFlavor, metadata, cancellationToken).ConfigureAwait(false);
            return metadata;
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    public async Task<IReadOnlyList<CloudBackupInfo>> UploadPendingBackupsAsync(
        IEnumerable<BackupInfo> backups,
        CloudBackupOptions options,
        CancellationToken cancellationToken = default)
    {
        var uploaded = new List<CloudBackupInfo>();
        if (!options.Enabled || !IsAvailableForAppFlavor(options.AppFlavor))
        {
            return uploaded;
        }

        foreach (var backup in backups.OrderBy(item => item.CreatedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await UploadBackupAsync(backup, options, cancellationToken).ConfigureAwait(false) is { } result)
            {
                uploaded.Add(result);
            }
        }

        return uploaded;
    }

    public Task<IReadOnlyList<CloudBackupInfo>> ListBackupsAsync(string appFlavor, CancellationToken cancellationToken = default) =>
        provider.ListBackupsAsync(appFlavor, cancellationToken);

    public async Task DownloadBackupAsync(
        CloudBackupInfo backup,
        string destinationPath,
        string? passphrase,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        var tempDirectory = CreateTempDirectory();
        try
        {
            var payloadPath = Path.Combine(tempDirectory, Path.GetFileName(backup.CloudPath));
            await provider.DownloadFileAsync(backup.CloudPath, payloadPath, cancellationToken).ConfigureAwait(false);

            var compressedPath = payloadPath;
            if (string.Equals(backup.EncryptionMode, CloudBackupEncryptionModes.Passphrase, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(passphrase))
                {
                    throw new InvalidOperationException("Enter the cloud backup passphrase to restore this encrypted backup.");
                }

                compressedPath = Path.Combine(tempDirectory, $"{backup.BackupId}.db.gz");
                await DecryptFileAsync(payloadPath, compressedPath, backup, passphrase, cancellationToken).ConfigureAwait(false);
            }

            await DecompressAsync(compressedPath, destinationPath, cancellationToken).ConfigureAwait(false);
            VerifyRestoredContentHash(destinationPath, backup);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private async Task RefreshManifestAndPruneAsync(
        string appFlavor,
        CloudBackupInfo newestBackup,
        CancellationToken cancellationToken)
    {
        var backups = await provider.ListBackupsAsync(appFlavor, cancellationToken).ConfigureAwait(false);
        if (backups.All(item => !string.Equals(item.BackupId, newestBackup.BackupId, StringComparison.Ordinal)))
        {
            backups = backups.Concat([newestBackup]).ToList();
        }

        backups = await PruneBackupsAsync(appFlavor, backups, cancellationToken).ConfigureAwait(false);
        await provider.UploadManifestAsync(appFlavor, new CloudBackupManifest
        {
            AppFlavor = appFlavor,
            GeneratedAt = DateTimeOffset.UtcNow,
            Backups = backups.OrderByDescending(item => item.CreatedAt).ToList(),
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CloudBackupInfo>> PruneBackupsAsync(
        string appFlavor,
        IReadOnlyList<CloudBackupInfo> backups,
        CancellationToken cancellationToken)
    {
        var ordered = backups
            .Where(item => string.Equals(item.AppFlavor, appFlavor, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAt)
            .ToList();
        var retained = new HashSet<string>(StringComparer.Ordinal);

        foreach (var backup in ordered.Where(IsDailyBackup).Take(RetentionPolicy.DailyBackups))
        {
            retained.Add(backup.BackupId);
        }

        foreach (var backup in ordered.Where(IsDailyBackup)
                     .GroupBy(backup => $"{ISOWeek.GetYear(backup.CreatedAt.DateTime)}-{ISOWeek.GetWeekOfYear(backup.CreatedAt.DateTime):D2}")
                     .Take(RetentionPolicy.WeeklyBackups)
                     .Select(group => group.First()))
        {
            retained.Add(backup.BackupId);
        }

        foreach (var backup in ordered.Where(IsDailyBackup)
                     .GroupBy(backup => backup.CreatedAt.ToString("yyyy-MM", CultureInfo.InvariantCulture))
                     .Take(RetentionPolicy.MonthlyBackups)
                     .Select(group => group.First()))
        {
            retained.Add(backup.BackupId);
        }

        foreach (var backup in ordered.Where(backup => !IsDailyBackup(backup)).Take(RetentionPolicy.EventBackups))
        {
            retained.Add(backup.BackupId);
        }

        foreach (var backup in ordered.Where(backup => !retained.Contains(backup.BackupId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await provider.DeleteBackupAsync(backup, cancellationToken).ConfigureAwait(false);
        }

        return ordered.Where(backup => retained.Contains(backup.BackupId)).ToList();
    }

    private static CloudBackupInfo CreateMetadata(
        BackupInfo backup,
        CloudBackupOptions options,
        string backupId,
        string stem,
        string contentHash,
        long compressedSize)
    {
        var createdAt = new DateTimeOffset(backup.CreatedAt);
        var cloudPath = CloudBackupPaths.BackupFilePath(options.AppFlavor, $"{stem}.db.gz");
        return new CloudBackupInfo
        {
            BackupId = backupId,
            CloudProvider = CloudBackupProviders.OneDrive,
            CloudPath = cloudPath,
            UploadedAt = DateTimeOffset.UtcNow,
            CreatedAt = createdAt,
            Reason = backup.Reason,
            TaskCount = backup.TaskCount,
            ProjectCount = backup.ProjectCount,
            SpaceCount = backup.SpaceCount,
            AppFlavor = options.AppFlavor,
            AppVersion = backup.AppVersion,
            IntegrityStatus = backup.IntegrityStatus,
            EncryptionMode = CloudBackupEncryptionModes.None,
            ContentHash = contentHash,
            CompressedSize = compressedSize,
            DatabaseSizeBytes = backup.SizeBytes,
            SourceFileName = backup.FileName,
        };
    }

    private static string BuildBackupId(BackupInfo backup)
    {
        var input = string.Join("|", backup.FileName, backup.CreatedAt.ToString("O", CultureInfo.InvariantCulture), ComputeFileHash(backup.Path));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..24].ToLowerInvariant();
    }

    private static string BuildRemoteFileStem(BackupInfo backup, string backupId)
    {
        var createdAt = new DateTimeOffset(backup.CreatedAt).UtcDateTime;
        var reason = SanitizeReason(backup.Reason);
        return $"{createdAt:yyyyMMddTHHmmssZ}_{reason}_{backupId}";
    }

    private static string SanitizeReason(string reason)
    {
        var normalized = string.IsNullOrWhiteSpace(reason) ? BackupReasons.Unknown : reason.Trim().ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }

        return builder.ToString().Trim('-');
    }

    private static async Task CompressAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(sourcePath);
        await using var destination = File.Create(destinationPath);
        await using var gzip = new GZipStream(destination, CompressionLevel.SmallestSize);
        await source.CopyToAsync(gzip, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DecompressAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(sourcePath);
        await using var gzip = new GZipStream(source, CompressionMode.Decompress);
        await using var destination = File.Create(destinationPath);
        await gzip.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<EncryptionResult> EncryptFileAsync(
        string sourcePath,
        string destinationPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        var salt = RandomNumberGenerator.GetBytes(EncryptionSaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(EncryptionNonceBytes);
        var tag = new byte[EncryptionTagBytes];
        var plaintext = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var ciphertext = new byte[plaintext.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, KdfIterations, HashAlgorithmName.SHA256, EncryptionKeyBytes);
        using var aes = new AesGcm(key, EncryptionTagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        await File.WriteAllBytesAsync(destinationPath, ciphertext, cancellationToken).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plaintext);
        return new EncryptionResult(salt, nonce, tag);
    }

    private static async Task DecryptFileAsync(
        string sourcePath,
        string destinationPath,
        CloudBackupInfo backup,
        string passphrase,
        CancellationToken cancellationToken)
    {
        var salt = Convert.FromBase64String(backup.Salt);
        var nonce = Convert.FromBase64String(backup.Nonce);
        var tag = Convert.FromBase64String(backup.Tag);
        var ciphertext = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var plaintext = new byte[ciphertext.Length];
        var iterations = backup.KdfIterations <= 0 ? KdfIterations : backup.KdfIterations;
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, EncryptionKeyBytes);
        using var aes = new AesGcm(key, EncryptionTagBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        await File.WriteAllBytesAsync(destinationPath, plaintext, cancellationToken).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plaintext);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void VerifyRestoredContentHash(string destinationPath, CloudBackupInfo backup)
    {
        if (string.IsNullOrWhiteSpace(backup.ContentHash))
        {
            return;
        }

        var actualHash = ComputeFileHash(destinationPath);
        if (string.Equals(actualHash, backup.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(destinationPath);
        }
        catch
        {
        }

        throw new InvalidDataException("Downloaded cloud backup does not match its metadata hash.");
    }

    private static bool IsDailyBackup(CloudBackupInfo backup) =>
        string.Equals(backup.Reason, BackupReasons.Daily, StringComparison.OrdinalIgnoreCase);

    private static string ContentTypeFor(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "application/octet-stream";

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "openza-cloud-backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed record EncryptionResult(byte[] Salt, byte[] Nonce, byte[] Tag);
}

public static class CloudBackupProviders
{
    public const string OneDrive = "onedrive";
}

public static class CloudBackupEncryptionModes
{
    public const string None = "none";
    public const string Passphrase = "passphrase";
}

public static class CloudBackupPaths
{
    public static string ManifestPath(string appFlavor) => $"v1/{NormalizeAppFlavor(appFlavor)}/manifest.json";

    public static string BackupDirectory(string appFlavor) => $"v1/{NormalizeAppFlavor(appFlavor)}/backups";

    public static string BackupFilePath(string appFlavor, string fileName) =>
        $"{BackupDirectory(appFlavor)}/{Path.GetFileName(fileName)}";

    private static string NormalizeAppFlavor(string appFlavor) =>
        string.Equals(appFlavor, BackupPaths.DevFlavor, StringComparison.OrdinalIgnoreCase)
            ? BackupPaths.DevFlavor
            : BackupPaths.ProductionFlavor;
}

public sealed record CloudBackupOptions(
    bool Enabled,
    bool Encrypt,
    string? Passphrase,
    string AppFlavor);

public sealed class CloudBackupManifest
{
    public int Version { get; set; } = 1;
    public string AppFlavor { get; set; } = BackupPaths.ProductionFlavor;
    public DateTimeOffset GeneratedAt { get; set; }
    public List<CloudBackupInfo> Backups { get; set; } = [];
}

public sealed class CloudBackupInfo
{
    public string BackupId { get; set; } = string.Empty;
    public string CloudProvider { get; set; } = CloudBackupProviders.OneDrive;
    public string CloudPath { get; set; } = string.Empty;
    public string MetadataPath { get; set; } = string.Empty;
    public DateTimeOffset UploadedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Reason { get; set; } = BackupReasons.Unknown;
    public int? TaskCount { get; set; }
    public int? ProjectCount { get; set; }
    public int? SpaceCount { get; set; }
    public string AppFlavor { get; set; } = BackupPaths.ProductionFlavor;
    public string AppVersion { get; set; } = string.Empty;
    public string IntegrityStatus { get; set; } = BackupIntegrityStatuses.Unknown;
    public string EncryptionMode { get; set; } = CloudBackupEncryptionModes.None;
    public string Kdf { get; set; } = string.Empty;
    public int KdfIterations { get; set; }
    public string Salt { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public long DatabaseSizeBytes { get; set; }
    public long CompressedSize { get; set; }
    public long EncryptedSize { get; set; }
    public string SourceFileName { get; set; } = string.Empty;

    public string DisplayName
    {
        get
        {
            var counts = TaskCount is null
                ? "counts unavailable"
                : $"{TaskCount} tasks, {ProjectCount} projects, {SpaceCount} spaces";
            var encrypted = string.Equals(EncryptionMode, CloudBackupEncryptionModes.Passphrase, StringComparison.OrdinalIgnoreCase)
                ? "encrypted"
                : "OneDrive protected";
            return $"{CreatedAt.LocalDateTime:g} - {FormatReason(Reason)} - {counts} - {encrypted}";
        }
    }

    private static string FormatReason(string reason) => reason switch
    {
        BackupReasons.Daily => "Daily",
        BackupReasons.Manual => "Manual",
        BackupReasons.PreRestore => "Before restore",
        BackupReasons.PreImport => "Before import",
        BackupReasons.PreMigration => "Before migration",
        BackupReasons.Legacy => "Legacy",
        _ => "Backup",
    };
}
