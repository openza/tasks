import 'package:freezed_annotation/freezed_annotation.dart';

import 'label.dart';
import 'project.dart';

part 'task.freezed.dart';
part 'task.g.dart';

/// Task entity representing a unified task from any source
@freezed
sealed class TaskEntity with _$TaskEntity {
  const TaskEntity._(); // Enable custom getters

  const factory TaskEntity({
    required String id,
    String? externalId,           // Provider's original ID
    required String integrationId, // FK to integrations table
    required String title,
    String? description,
    String? projectId,
    String? parentId,
    @Default(2) int priority,
    @Default(TaskStatus.pending) TaskStatus status,
    DateTime? dueDate,
    String? dueTime,
    String? notes,                // Long description/reference material
    Map<String, dynamic>? providerMetadata, // Provider-specific unmapped data

    // Timestamps
    required DateTime createdAt,
    DateTime? updatedAt,
    DateTime? completedAt,

    // Joined data (populated from relations)
    @Default([]) List<LabelEntity> labels,
    ProjectEntity? project,
  }) = _TaskEntity;

  bool get isCompleted => status == TaskStatus.completed;

  bool get isOverdue {
    if (dueDate == null || isCompleted) return false;
    final now = DateTime.now();
    final today = DateTime(now.year, now.month, now.day);
    return dueDate!.isBefore(today);
  }

  bool get isDueToday {
    if (dueDate == null) return false;
    final now = DateTime.now();
    return dueDate!.year == now.year &&
        dueDate!.month == now.month &&
        dueDate!.day == now.day;
  }

  bool get hasLabels => labels.isNotEmpty;

  /// Check if this is a native (Openza Tasks) task
  bool get isNative => integrationId == 'openza_tasks';

  /// Check if this is an external provider task
  bool get isExternal => !isNative;

  /// Get the provider's original project ID from metadata (wrapper pattern)
  /// This is the project ID from Todoist/MS To-Do, NOT the local organization
  String? get sourceProjectId {
    final sourceTask = providerMetadata?['sourceTask'] as Map<String, dynamic>?;
    return sourceTask?['projectId'] as String?;
  }

  /// Get the provider's original project NAME from metadata
  String? get sourceProjectName {
    final sourceTask = providerMetadata?['sourceTask'] as Map<String, dynamic>?;
    return sourceTask?['projectName'] as String?;
  }

  /// Get the provider's original parent task ID from metadata (for subtasks)
  String? get sourceParentId {
    final sourceTask = providerMetadata?['sourceTask'] as Map<String, dynamic>?;
    return sourceTask?['parentId'] as String?;
  }

  /// Check if this task has a provider source (imported from external provider)
  bool get hasProviderSource => providerMetadata?['sourceTask'] != null;

  factory TaskEntity.fromJson(Map<String, dynamic> json) =>
      _$TaskEntityFromJson(json);
}

enum TaskStatus {
  pending,
  inProgress,
  completed,
  cancelled;

  String get value {
    switch (this) {
      case TaskStatus.pending:
        return 'pending';
      case TaskStatus.inProgress:
        return 'in_progress';
      case TaskStatus.completed:
        return 'completed';
      case TaskStatus.cancelled:
        return 'cancelled';
    }
  }

  static TaskStatus fromString(String value) {
    switch (value) {
      case 'pending':
        return TaskStatus.pending;
      case 'in_progress':
        return TaskStatus.inProgress;
      case 'completed':
        return TaskStatus.completed;
      case 'cancelled':
        return TaskStatus.cancelled;
      default:
        return TaskStatus.pending;
    }
  }
}
