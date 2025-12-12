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
    required String title,
    String? description,
    String? projectId,
    String? parentId,
    @Default(2) int priority,
    @Default(TaskStatus.pending) TaskStatus status,
    DateTime? dueDate,
    String? dueTime,

    // Enhanced local features
    int? estimatedDuration,
    int? actualDuration,
    @Default(2) int energyLevel,
    @Default(TaskContext.work) TaskContext context,
    @Default(false) bool focusTime,
    String? notes,

    // External integration data
    Map<String, dynamic>? sourceTask,
    Map<String, dynamic>? integrations,

    // Timestamps
    required DateTime createdAt,
    DateTime? updatedAt,
    DateTime? completedAt,

    // Joined data (populated from relations)
    @Default([]) List<LabelEntity> labels,
    ProjectEntity? project,

    // Source provider
    TaskProvider? provider,
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

enum TaskContext {
  work,
  personal,
  errands,
  home,
  office,
  anywhere,
  phone,
  computer,
  waiting;

  String get value {
    switch (this) {
      case TaskContext.work:
        return 'work';
      case TaskContext.personal:
        return 'personal';
      case TaskContext.errands:
        return 'errands';
      case TaskContext.home:
        return 'home';
      case TaskContext.office:
        return 'office';
      case TaskContext.anywhere:
        return 'anywhere';
      case TaskContext.phone:
        return 'phone';
      case TaskContext.computer:
        return 'computer';
      case TaskContext.waiting:
        return 'waiting';
    }
  }

  String get displayName {
    switch (this) {
      case TaskContext.work:
        return 'Work';
      case TaskContext.personal:
        return 'Personal';
      case TaskContext.errands:
        return 'Errands';
      case TaskContext.home:
        return 'Home';
      case TaskContext.office:
        return 'Office';
      case TaskContext.anywhere:
        return 'Anywhere';
      case TaskContext.phone:
        return 'Phone';
      case TaskContext.computer:
        return 'Computer';
      case TaskContext.waiting:
        return 'Waiting';
    }
  }

  static TaskContext fromString(String value) {
    switch (value) {
      case 'work':
        return TaskContext.work;
      case 'personal':
        return TaskContext.personal;
      case 'errands':
        return TaskContext.errands;
      case 'home':
        return TaskContext.home;
      case 'office':
        return TaskContext.office;
      case 'anywhere':
        return TaskContext.anywhere;
      case 'phone':
        return TaskContext.phone;
      case 'computer':
        return TaskContext.computer;
      case 'waiting':
        return TaskContext.waiting;
      default:
        return TaskContext.work;
    }
  }
}

enum TaskProvider {
  local,
  todoist,
  msToDo;

  String get displayName {
    switch (this) {
      case TaskProvider.local:
        return 'Local';
      case TaskProvider.todoist:
        return 'Todoist';
      case TaskProvider.msToDo:
        return 'Microsoft To-Do';
    }
  }
}
