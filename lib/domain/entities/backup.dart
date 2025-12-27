import 'package:freezed_annotation/freezed_annotation.dart';

part 'backup.freezed.dart';
part 'backup.g.dart';

/// Backup frequency options for automatic backups
enum BackupFrequency {
  hourly('Hourly', Duration(hours: 1)),
  daily('Daily', Duration(days: 1)),
  weekly('Weekly', Duration(days: 7));

  const BackupFrequency(this.displayName, this.interval);

  final String displayName;
  final Duration interval;

  static BackupFrequency fromString(String value) {
    return BackupFrequency.values.firstWhere(
      (f) => f.name == value,
      orElse: () => BackupFrequency.daily,
    );
  }
}

/// Information about a backup file
@freezed
sealed class BackupInfo with _$BackupInfo {
  const BackupInfo._();

  const factory BackupInfo({
    required String fileName,
    required String filePath,
    required DateTime createdAt,
    required int sizeInBytes,
  }) = _BackupInfo;

  /// Human-readable file size
  String get formattedSize {
    if (sizeInBytes < 1024) {
      return '$sizeInBytes B';
    } else if (sizeInBytes < 1024 * 1024) {
      return '${(sizeInBytes / 1024).toStringAsFixed(1)} KB';
    } else {
      return '${(sizeInBytes / (1024 * 1024)).toStringAsFixed(1)} MB';
    }
  }

  factory BackupInfo.fromJson(Map<String, dynamic> json) =>
      _$BackupInfoFromJson(json);
}

/// Result of a backup operation
@freezed
sealed class BackupResult with _$BackupResult {
  const factory BackupResult.success(BackupInfo backup) = BackupResultSuccess;
  const factory BackupResult.error(String message) = BackupResultError;
}

/// Result of a restore operation
@freezed
sealed class RestoreResult with _$RestoreResult {
  const factory RestoreResult.success() = RestoreResultSuccess;
  const factory RestoreResult.error(String message) = RestoreResultError;
}

/// State for backup operations
@freezed
sealed class BackupState with _$BackupState {
  const factory BackupState({
    @Default(BackupStatus.idle) BackupStatus status,
    DateTime? lastBackupTime,
    String? error,
    @Default([]) List<BackupInfo> availableBackups,
    @Default(true) bool autoBackupEnabled,
    @Default(BackupFrequency.daily) BackupFrequency frequency,
  }) = _BackupState;
}

/// Status of backup operations
enum BackupStatus {
  idle,
  backingUp,
  restoring,
  loadingBackups,
  success,
  error,
}
