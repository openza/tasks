import '../../core/constants/storage_keys.dart';
import '../../domain/entities/backup.dart';
import '../datasources/local/secure_storage.dart';

/// Service for managing backup preferences
class BackupPreferencesService {
  final SecureStorageService _storage;

  BackupPreferencesService({SecureStorageService? storage})
      : _storage = storage ?? SecureStorageService.instance;

  /// Get whether auto-backup is enabled (defaults to true for new users)
  Future<bool> isAutoBackupEnabled() async {
    final value = await _storage.read(key: StorageKeys.backupAutoEnabled);
    // Default to true if no preference is set (new users get daily backups)
    if (value == null) return true;
    return value == 'true';
  }

  /// Set auto-backup enabled status
  Future<void> setAutoBackupEnabled(bool enabled) async {
    await _storage.write(
      key: StorageKeys.backupAutoEnabled,
      value: enabled.toString(),
    );
  }

  /// Get backup frequency
  Future<BackupFrequency> getBackupFrequency() async {
    final value = await _storage.read(key: StorageKeys.backupFrequency);
    if (value == null) return BackupFrequency.daily;
    return BackupFrequency.fromString(value);
  }

  /// Set backup frequency
  Future<void> setBackupFrequency(BackupFrequency frequency) async {
    await _storage.write(
      key: StorageKeys.backupFrequency,
      value: frequency.name,
    );
  }

  /// Get last backup time
  Future<DateTime?> getLastBackupTime() async {
    final value = await _storage.read(key: StorageKeys.lastBackupTime);
    if (value == null) return null;
    try {
      return DateTime.parse(value);
    } catch (_) {
      return null;
    }
  }

  /// Set last backup time
  Future<void> setLastBackupTime(DateTime time) async {
    await _storage.write(
      key: StorageKeys.lastBackupTime,
      value: time.toIso8601String(),
    );
  }
}
