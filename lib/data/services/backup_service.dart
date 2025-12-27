import 'dart:io';
import 'dart:isolate';

import 'package:path/path.dart' as p;
import 'package:path_provider/path_provider.dart';

import '../../core/utils/logger.dart';
import '../../domain/entities/backup.dart';
import '../datasources/local/database/database.dart';

/// Parameters for backup operation in isolate
class _BackupParams {
  final String dbPath;
  final String backupDir;
  final String fileName;

  _BackupParams({
    required this.dbPath,
    required this.backupDir,
    required this.fileName,
  });
}

/// Parameters for restore operation in isolate
class _RestoreParams {
  final String backupPath;
  final String dbPath;

  _RestoreParams({
    required this.backupPath,
    required this.dbPath,
  });
}

/// Service for managing database backups
class BackupService {
  final AppDatabase _database;

  BackupService(this._database);

  static const String _backupFolderName = 'backups';
  static const int _maxBackups = 7;

  /// Get the backup directory path
  Future<Directory> _getBackupDir() async {
    final appDir = await getApplicationSupportDirectory();
    final backupDir = Directory(p.join(appDir.path, _backupFolderName));
    if (!await backupDir.exists()) {
      await backupDir.create(recursive: true);
    }
    return backupDir;
  }

  /// Get the database file path
  Future<String> _getDatabasePath() async {
    final appDir = await getApplicationSupportDirectory();
    return p.join(appDir.path, 'openza_tasks.db');
  }

  /// Checkpoint WAL to ensure all data is written to main database file
  Future<void> _checkpointWal() async {
    try {
      await _database.customStatement('PRAGMA wal_checkpoint(TRUNCATE)');
      AppLogger.debug('WAL checkpoint completed');
    } catch (e, stack) {
      AppLogger.error('WAL checkpoint failed', e, stack);
      // Continue anyway - backup may have uncommitted data but better than failing
    }
  }

  /// Generate backup filename with timestamp
  String _generateBackupFileName() {
    final now = DateTime.now();
    final timestamp =
        '${now.year}${now.month.toString().padLeft(2, '0')}${now.day.toString().padLeft(2, '0')}_'
        '${now.hour.toString().padLeft(2, '0')}${now.minute.toString().padLeft(2, '0')}${now.second.toString().padLeft(2, '0')}';
    return 'backup_$timestamp.db';
  }

  /// Create a backup of the database
  /// Runs file copy in a separate isolate to avoid blocking UI
  Future<BackupResult> createBackup({String? customName}) async {
    try {
      // First checkpoint WAL on main thread (needs database connection)
      await _checkpointWal();

      final dbPath = await _getDatabasePath();
      final backupDir = await _getBackupDir();
      final fileName = customName ?? _generateBackupFileName();

      // Run file copy in isolate
      final result = await Isolate.run(() => _copyDatabaseInIsolate(
            _BackupParams(
              dbPath: dbPath,
              backupDir: backupDir.path,
              fileName: fileName,
            ),
          ));

      if (result != null) {
        AppLogger.info('Backup created: $fileName');

        // Clean up old backups
        await _deleteOldBackups();

        return BackupResult.success(result);
      } else {
        return const BackupResult.error('Failed to create backup file');
      }
    } catch (e, stack) {
      AppLogger.error('Backup failed', e, stack);
      return BackupResult.error('Backup failed: ${e.toString()}');
    }
  }

  /// Copy database file in isolate (static for isolate execution)
  static BackupInfo? _copyDatabaseInIsolate(_BackupParams params) {
    try {
      final dbFile = File(params.dbPath);
      if (!dbFile.existsSync()) {
        return null;
      }

      final backupPath = p.join(params.backupDir, params.fileName);
      dbFile.copySync(backupPath);

      // Also copy WAL and SHM if they exist
      final walFile = File('${params.dbPath}-wal');
      final shmFile = File('${params.dbPath}-shm');

      if (walFile.existsSync()) {
        walFile.copySync('$backupPath-wal');
      }
      if (shmFile.existsSync()) {
        shmFile.copySync('$backupPath-shm');
      }

      final backupFile = File(backupPath);
      final stat = backupFile.statSync();

      return BackupInfo(
        fileName: params.fileName,
        filePath: backupPath,
        createdAt: DateTime.now(),
        sizeInBytes: stat.size,
      );
    } catch (e) {
      return null;
    }
  }

  /// List all available backups
  Future<List<BackupInfo>> listBackups() async {
    try {
      final backupDir = await _getBackupDir();
      final files = await backupDir.list().toList();

      final backups = <BackupInfo>[];
      for (final entity in files) {
        if (entity is File && entity.path.endsWith('.db')) {
          final fileName = p.basename(entity.path);
          // Skip WAL and SHM files
          if (fileName.contains('-wal') || fileName.contains('-shm')) {
            continue;
          }

          final stat = await entity.stat();
          final createdAt = _parseBackupTimestamp(fileName) ?? stat.modified;

          backups.add(BackupInfo(
            fileName: fileName,
            filePath: entity.path,
            createdAt: createdAt,
            sizeInBytes: stat.size,
          ));
        }
      }

      // Sort by creation time, newest first
      backups.sort((a, b) => b.createdAt.compareTo(a.createdAt));
      return backups;
    } catch (e, stack) {
      AppLogger.error('Failed to list backups', e, stack);
      return [];
    }
  }

  /// Parse timestamp from backup filename
  DateTime? _parseBackupTimestamp(String fileName) {
    // Format: backup_YYYYMMDD_HHMMSS.db
    final regex = RegExp(r'backup_(\d{8})_(\d{6})\.db');
    final match = regex.firstMatch(fileName);
    if (match == null) return null;

    try {
      final dateStr = match.group(1)!;
      final timeStr = match.group(2)!;
      return DateTime(
        int.parse(dateStr.substring(0, 4)), // year
        int.parse(dateStr.substring(4, 6)), // month
        int.parse(dateStr.substring(6, 8)), // day
        int.parse(timeStr.substring(0, 2)), // hour
        int.parse(timeStr.substring(2, 4)), // minute
        int.parse(timeStr.substring(4, 6)), // second
      );
    } catch (_) {
      return null;
    }
  }

  /// Delete old backups, keeping only the most recent ones
  Future<void> _deleteOldBackups({int keepCount = _maxBackups}) async {
    try {
      final backups = await listBackups();
      if (backups.length <= keepCount) return;

      // Delete oldest backups
      final toDelete = backups.sublist(keepCount);
      for (final backup in toDelete) {
        await _deleteBackupFile(backup.filePath);
      }

      AppLogger.info('Deleted ${toDelete.length} old backup(s)');
    } catch (e, stack) {
      AppLogger.error('Failed to delete old backups', e, stack);
    }
  }

  /// Delete a backup file and its associated WAL/SHM files
  Future<void> _deleteBackupFile(String path) async {
    try {
      final file = File(path);
      if (await file.exists()) {
        await file.delete();
      }

      // Also delete WAL and SHM files
      final walFile = File('$path-wal');
      final shmFile = File('$path-shm');

      if (await walFile.exists()) {
        await walFile.delete();
      }
      if (await shmFile.exists()) {
        await shmFile.delete();
      }
    } catch (e, stack) {
      AppLogger.error('Failed to delete backup: $path', e, stack);
    }
  }

  /// Restore from a backup file
  /// Runs in isolate to avoid blocking UI
  Future<RestoreResult> restoreFromBackup(String backupPath) async {
    try {
      // First checkpoint current database
      await _checkpointWal();

      final dbPath = await _getDatabasePath();

      // Close database connection (will need app restart after restore)
      await _database.close();

      // Run restore in isolate
      final success = await Isolate.run(() => _restoreInIsolate(
            _RestoreParams(
              backupPath: backupPath,
              dbPath: dbPath,
            ),
          ));

      if (success) {
        AppLogger.info('Database restored from: $backupPath');
        return const RestoreResult.success();
      } else {
        return const RestoreResult.error('Failed to restore backup');
      }
    } catch (e, stack) {
      AppLogger.error('Restore failed', e, stack);
      return RestoreResult.error('Restore failed: ${e.toString()}');
    }
  }

  /// Restore database in isolate (static for isolate execution)
  static bool _restoreInIsolate(_RestoreParams params) {
    try {
      final backupFile = File(params.backupPath);
      if (!backupFile.existsSync()) {
        return false;
      }

      final dbFile = File(params.dbPath);
      final walFile = File('${params.dbPath}-wal');
      final shmFile = File('${params.dbPath}-shm');

      // Create safety backup before replacing (can rollback if restore fails)
      final safetyBackupPath = '${params.dbPath}.restore_backup';
      if (dbFile.existsSync()) {
        dbFile.copySync(safetyBackupPath);
      }

      try {
        // Delete current database and WAL/SHM files
        if (dbFile.existsSync()) {
          dbFile.deleteSync();
        }
        if (walFile.existsSync()) {
          walFile.deleteSync();
        }
        if (shmFile.existsSync()) {
          shmFile.deleteSync();
        }

        // Copy backup to database location
        backupFile.copySync(params.dbPath);

        // Copy backup WAL/SHM if they exist
        final backupWal = File('${params.backupPath}-wal');
        final backupShm = File('${params.backupPath}-shm');

        if (backupWal.existsSync()) {
          backupWal.copySync('${params.dbPath}-wal');
        }
        if (backupShm.existsSync()) {
          backupShm.copySync('${params.dbPath}-shm');
        }

        // Success - delete the safety backup
        final safetyBackup = File(safetyBackupPath);
        if (safetyBackup.existsSync()) {
          safetyBackup.deleteSync();
        }

        return true;
      } catch (e) {
        // Restore failed - try to rollback from safety backup
        final safetyBackup = File(safetyBackupPath);
        if (safetyBackup.existsSync()) {
          safetyBackup.copySync(params.dbPath);
          safetyBackup.deleteSync();
        }
        rethrow;
      }
    } catch (e) {
      return false;
    }
  }

  /// Delete a specific backup
  Future<bool> deleteBackup(String filePath) async {
    try {
      await _deleteBackupFile(filePath);
      return true;
    } catch (e) {
      return false;
    }
  }

  /// Export a backup to a user-specified destination
  /// Returns the exported file path on success, null on failure
  Future<String?> exportBackupToPath(String backupPath, String destinationPath) async {
    try {
      final result = await Isolate.run(() => _exportBackupInIsolate(
            backupPath,
            destinationPath,
          ));
      if (result) {
        AppLogger.info('Backup exported to: $destinationPath');
        return destinationPath;
      }
      return null;
    } catch (e, stack) {
      AppLogger.error('Failed to export backup', e, stack);
      return null;
    }
  }

  /// Export backup in isolate (static for isolate execution)
  static bool _exportBackupInIsolate(String sourcePath, String destPath) {
    try {
      final sourceFile = File(sourcePath);
      if (!sourceFile.existsSync()) {
        return false;
      }

      // Ensure destination has .db extension
      final finalPath = destPath.endsWith('.db') ? destPath : '$destPath.db';
      sourceFile.copySync(finalPath);

      return true;
    } catch (e) {
      return false;
    }
  }

  /// Import a backup from an external file
  /// Validates the file is a valid SQLite database before importing
  Future<BackupResult> importBackupFromPath(String externalPath) async {
    try {
      final backupDir = await _getBackupDir();
      final fileName = 'imported_${_generateBackupFileName()}';

      final result = await Isolate.run(() => _importBackupInIsolate(
            externalPath,
            p.join(backupDir.path, fileName),
          ));

      if (result != null) {
        AppLogger.info('Backup imported: $fileName');
        return BackupResult.success(result);
      } else {
        return const BackupResult.error('Invalid backup file or import failed');
      }
    } catch (e, stack) {
      AppLogger.error('Failed to import backup', e, stack);
      return BackupResult.error('Import failed: ${e.toString()}');
    }
  }

  /// Import backup in isolate (static for isolate execution)
  static BackupInfo? _importBackupInIsolate(String sourcePath, String destPath) {
    try {
      final sourceFile = File(sourcePath);
      if (!sourceFile.existsSync()) {
        return null;
      }

      // Basic SQLite validation - check file header
      final bytes = sourceFile.readAsBytesSync();
      if (bytes.length < 16) {
        return null; // Too small to be a valid SQLite file
      }

      // SQLite files start with "SQLite format 3\0"
      final header = String.fromCharCodes(bytes.sublist(0, 16));
      if (!header.startsWith('SQLite format 3')) {
        return null; // Not a valid SQLite file
      }

      // Copy to backup directory
      sourceFile.copySync(destPath);

      final importedFile = File(destPath);
      final stat = importedFile.statSync();

      return BackupInfo(
        fileName: p.basename(destPath),
        filePath: destPath,
        createdAt: DateTime.now(),
        sizeInBytes: stat.size,
      );
    } catch (e) {
      return null;
    }
  }
}
