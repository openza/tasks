import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
// ignore: deprecated_member_use
import 'package:flutter_riverpod/legacy.dart';

import '../../core/utils/logger.dart';
import '../../data/services/backup_preferences_service.dart';
import '../../data/services/backup_service.dart';
import '../../domain/entities/backup.dart';
import 'database_provider.dart';

/// Provider for backup service
final backupServiceProvider = Provider<BackupService>((ref) {
  final database = ref.watch(databaseProvider);
  return BackupService(database);
});

/// Provider for backup preferences service
final backupPreferencesProvider = Provider<BackupPreferencesService>((ref) {
  return BackupPreferencesService();
});

/// Provider for backup state management
final backupProvider = StateNotifierProvider<BackupNotifier, BackupState>((ref) {
  return BackupNotifier(ref);
});

/// State notifier for backup operations
class BackupNotifier extends StateNotifier<BackupState> {
  final Ref _ref;
  Timer? _backupTimer;
  Timer? _statusResetTimer;

  BackupNotifier(this._ref) : super(const BackupState()) {
    _initialize();
  }

  BackupService get _backupService => _ref.read(backupServiceProvider);
  BackupPreferencesService get _prefsService =>
      _ref.read(backupPreferencesProvider);

  /// Initialize backup system
  Future<void> _initialize() async {
    await _loadPreferences();
    await loadBackups();
    _scheduleNextBackup();
  }

  /// Load saved preferences
  Future<void> _loadPreferences() async {
    try {
      final autoEnabled = await _prefsService.isAutoBackupEnabled();
      final frequency = await _prefsService.getBackupFrequency();
      final lastBackup = await _prefsService.getLastBackupTime();

      state = state.copyWith(
        autoBackupEnabled: autoEnabled,
        frequency: frequency,
        lastBackupTime: lastBackup,
      );
    } catch (e, stack) {
      AppLogger.error('Failed to load backup preferences', e, stack);
    }
  }

  /// Load list of available backups
  Future<void> loadBackups() async {
    state = state.copyWith(status: BackupStatus.loadingBackups);

    try {
      final backups = await _backupService.listBackups();
      state = state.copyWith(
        status: BackupStatus.idle,
        availableBackups: backups,
      );
    } catch (e, stack) {
      AppLogger.error('Failed to load backups', e, stack);
      state = state.copyWith(
        status: BackupStatus.error,
        error: 'Failed to load backups: ${e.toString()}',
      );
    }
  }

  /// Schedule next automatic backup
  void _scheduleNextBackup() {
    _backupTimer?.cancel();

    if (!state.autoBackupEnabled) {
      AppLogger.debug('Auto backup disabled, not scheduling');
      return;
    }

    final lastBackup = state.lastBackupTime;
    final frequency = state.frequency;

    Duration delay;
    if (lastBackup == null) {
      // No previous backup, schedule immediately
      delay = Duration.zero;
    } else {
      final nextBackup = lastBackup.add(frequency.interval);
      delay = nextBackup.difference(DateTime.now());

      // If already past due, backup immediately
      if (delay.isNegative) {
        delay = Duration.zero;
      }
    }

    AppLogger.info(
        'Next backup scheduled in ${delay.inMinutes} minutes (${frequency.displayName})');

    _backupTimer = Timer(delay, () async {
      await backupNow();
      _scheduleNextBackup(); // Schedule next backup after this one completes
    });
  }

  /// Perform backup now
  Future<bool> backupNow() async {
    if (state.status == BackupStatus.backingUp) {
      AppLogger.info('Backup already in progress');
      return false;
    }

    state = state.copyWith(status: BackupStatus.backingUp, error: null);

    try {
      final result = await _backupService.createBackup();

      return result.when(
        success: (backup) async {
          await _prefsService.setLastBackupTime(DateTime.now());
          await loadBackups(); // Refresh list

          state = state.copyWith(
            status: BackupStatus.success,
            lastBackupTime: DateTime.now(),
          );

          _scheduleStatusReset();
          AppLogger.info('Backup completed: ${backup.fileName}');
          return true;
        },
        error: (message) {
          state = state.copyWith(
            status: BackupStatus.error,
            error: message,
          );
          AppLogger.error('Backup failed: $message');
          return false;
        },
      );
    } catch (e, stack) {
      AppLogger.error('Backup failed', e, stack);
      state = state.copyWith(
        status: BackupStatus.error,
        error: 'Backup failed: ${e.toString()}',
      );
      return false;
    }
  }

  /// Restore from a backup
  Future<bool> restoreFromBackup(String backupPath) async {
    if (state.status == BackupStatus.restoring) {
      return false;
    }

    state = state.copyWith(status: BackupStatus.restoring, error: null);

    try {
      final result = await _backupService.restoreFromBackup(backupPath);

      return result.when(
        success: () {
          state = state.copyWith(status: BackupStatus.success);
          _scheduleStatusReset();
          AppLogger.info('Restore completed');
          return true;
        },
        error: (message) {
          state = state.copyWith(
            status: BackupStatus.error,
            error: message,
          );
          AppLogger.error('Restore failed: $message');
          return false;
        },
      );
    } catch (e, stack) {
      AppLogger.error('Restore failed', e, stack);
      state = state.copyWith(
        status: BackupStatus.error,
        error: 'Restore failed: ${e.toString()}',
      );
      return false;
    }
  }

  /// Delete a backup
  Future<bool> deleteBackup(String filePath) async {
    try {
      final success = await _backupService.deleteBackup(filePath);
      if (success) {
        await loadBackups(); // Refresh list
      }
      return success;
    } catch (e) {
      return false;
    }
  }

  /// Export a backup to a user-specified location
  Future<String?> exportBackup(String backupPath, String destinationPath) async {
    try {
      return await _backupService.exportBackupToPath(backupPath, destinationPath);
    } catch (e) {
      return null;
    }
  }

  /// Import a backup from an external file
  Future<bool> importBackup(String externalPath) async {
    state = state.copyWith(status: BackupStatus.loadingBackups, error: null);

    try {
      final result = await _backupService.importBackupFromPath(externalPath);

      return result.when(
        success: (backup) async {
          await loadBackups(); // Refresh list
          state = state.copyWith(status: BackupStatus.success);
          _scheduleStatusReset();
          return true;
        },
        error: (message) {
          state = state.copyWith(
            status: BackupStatus.error,
            error: message,
          );
          return false;
        },
      );
    } catch (e) {
      state = state.copyWith(
        status: BackupStatus.error,
        error: 'Import failed: ${e.toString()}',
      );
      return false;
    }
  }

  /// Set auto backup enabled
  Future<void> setAutoBackupEnabled(bool enabled) async {
    await _prefsService.setAutoBackupEnabled(enabled);
    state = state.copyWith(autoBackupEnabled: enabled);
    _scheduleNextBackup();
  }

  /// Set backup frequency
  Future<void> setBackupFrequency(BackupFrequency frequency) async {
    await _prefsService.setBackupFrequency(frequency);
    state = state.copyWith(frequency: frequency);
    _scheduleNextBackup();
  }

  /// Clear error state
  void clearError() {
    state = state.copyWith(status: BackupStatus.idle, error: null);
  }

  /// Schedule auto-reset of status after success
  void _scheduleStatusReset() {
    _statusResetTimer?.cancel();
    _statusResetTimer = Timer(const Duration(seconds: 3), () {
      if (state.status == BackupStatus.success) {
        state = state.copyWith(status: BackupStatus.idle);
      }
    });
  }

  @override
  void dispose() {
    _backupTimer?.cancel();
    _statusResetTimer?.cancel();
    super.dispose();
  }
}
