import 'dart:async';
import 'dart:convert';

import 'package:path_provider/path_provider.dart';
import 'package:path/path.dart' as p;

import '../../core/utils/logger.dart';
import '../../domain/entities/task.dart';
import '../../domain/entities/project.dart';
import '../../domain/entities/label.dart';
import '../datasources/remote/todoist_api.dart';
import 'sync_ffi.dart';

/// Sync result summary from Rust
class SyncSummary {
  final int tasksAdded;
  final int tasksUpdated;
  final int tasksDeleted;
  final int projectsSynced;
  final int labelsSynced;
  final bool success;
  final String? error;
  final String? newSyncToken;

  const SyncSummary({
    this.tasksAdded = 0,
    this.tasksUpdated = 0,
    this.tasksDeleted = 0,
    this.projectsSynced = 0,
    this.labelsSynced = 0,
    this.success = true,
    this.error,
    this.newSyncToken,
  });

  factory SyncSummary.fromJson(Map<String, dynamic> json) {
    return SyncSummary(
      tasksAdded: json['tasks_added'] ?? 0,
      tasksUpdated: json['tasks_updated'] ?? 0,
      tasksDeleted: json['tasks_deleted'] ?? 0,
      projectsSynced: json['projects_synced'] ?? 0,
      labelsSynced: json['labels_synced'] ?? 0,
      success: json['success'] ?? false,
      error: json['error'],
      newSyncToken: json['new_sync_token'],
    );
  }

  Map<String, dynamic> toJson() => {
    'tasks_added': tasksAdded,
    'tasks_updated': tasksUpdated,
    'tasks_deleted': tasksDeleted,
    'projects_synced': projectsSynced,
    'labels_synced': labelsSynced,
    'success': success,
    'error': error,
    'new_sync_token': newSyncToken,
  };
}

/// Result of fetching data from a remote provider
class SyncResult {
  final bool success;
  final String? error;
  final List<TaskEntity> tasks;
  final List<ProjectEntity> projects;
  final List<LabelEntity> labels;

  const SyncResult({
    required this.success,
    this.error,
    this.tasks = const [],
    this.projects = const [],
    this.labels = const [],
  });
}

/// Sync engine that coordinates sync between API and local database via Rust FFI
class SyncEngine {
  final TodoistApi? _todoistApi;
  final SyncFfi _ffi = SyncFfi();
  bool _ffiAvailable = false;

  SyncEngine({TodoistApi? todoistApi}) : _todoistApi = todoistApi {
    _initFfi();
  }

  void _initFfi() {
    try {
      _ffi.initialize();
      _ffiAvailable = true;
      AppLogger.info('Rust FFI sync engine initialized');
    } catch (e) {
      _ffiAvailable = false;
      AppLogger.warning('Rust FFI not available, using Dart-only sync: $e');
    }
  }

  /// Get the database path
  Future<String> _getDatabasePath() async {
    final dbFolder = await getApplicationDocumentsDirectory();
    return p.join(dbFolder.path, 'openza.db');
  }

  /// Fetch all data from Todoist (does not write to DB)
  Future<SyncResult> fetchFromTodoist() async {
    if (_todoistApi == null) {
      return SyncResult(
        success: false,
        error: 'Todoist API not configured',
      );
    }

    try {
      AppLogger.info('Fetching data from Todoist...');

      final tasks = await _todoistApi.getAllTasks();
      final projects = await _todoistApi.getAllProjects();
      final labels = await _todoistApi.getAllLabels();

      AppLogger.info('Fetched ${tasks.length} tasks, ${projects.length} projects, ${labels.length} labels');

      return SyncResult(
        success: true,
        tasks: tasks,
        projects: projects,
        labels: labels,
      );
    } catch (e, stack) {
      AppLogger.error('Failed to fetch from Todoist', e, stack);
      return SyncResult(
        success: false,
        error: e.toString(),
      );
    }
  }

  /// Sanitize string for FFI - remove control characters that can cause issues
  String _sanitizeForFfi(String input) {
    // Remove null bytes and other problematic control characters
    // Keep only printable ASCII and standard whitespace
    final buffer = StringBuffer();
    for (int i = 0; i < input.length; i++) {
      final char = input.codeUnitAt(i);
      // Keep: tab (9), newline (10), carriage return (13), and printable chars (32-126)
      // Also keep extended Unicode (>127) for international text
      if (char == 9 || char == 10 || char == 13 || (char >= 32 && char != 127)) {
        buffer.writeCharCode(char);
      }
    }
    return buffer.toString();
  }

  /// Perform initial sync via Rust FFI (clear and re-sync)
  Future<SyncSummary> initialSync({
    required String provider,
    required List<TaskEntity> tasks,
    required List<ProjectEntity> projects,
    required List<LabelEntity> labels,
  }) async {
    if (!_ffiAvailable) {
      return SyncSummary(
        success: false,
        error: 'Rust FFI not available',
      );
    }

    try {
      final dbPath = await _getDatabasePath();
      final tasksJson = _sanitizeForFfi(jsonEncode(tasks.map((t) => _taskToJson(t)).toList()));
      final projectsJson = _sanitizeForFfi(jsonEncode(projects.map((p) => _projectToJson(p)).toList()));
      final labelsJson = _sanitizeForFfi(jsonEncode(labels.map((l) => _labelToJson(l)).toList()));

      AppLogger.info('Starting initial sync via Rust FFI...');
      AppLogger.info('JSON lengths - tasks: ${tasksJson.length}, projects: ${projectsJson.length}, labels: ${labelsJson.length}');

      final resultJson = _ffi.initialSync(
        dbPath: dbPath,
        provider: provider,
        tasksJson: tasksJson,
        projectsJson: projectsJson,
        labelsJson: labelsJson,
      );

      final result = jsonDecode(resultJson) as Map<String, dynamic>;
      final summary = SyncSummary.fromJson(result);

      if (summary.success) {
        AppLogger.info('Initial sync completed: ${summary.tasksAdded} added, ${summary.projectsSynced} projects, ${summary.labelsSynced} labels');
      } else {
        AppLogger.error('Initial sync failed: ${summary.error}');
      }

      return summary;
    } catch (e, stack) {
      AppLogger.error('Initial sync failed', e, stack);
      return SyncSummary(
        success: false,
        error: e.toString(),
      );
    }
  }

  /// Perform incremental sync via Rust FFI
  Future<SyncSummary> incrementalSync({
    required String provider,
    required List<TaskEntity> tasks,
    required List<ProjectEntity> projects,
    required List<LabelEntity> labels,
    String? syncToken,
  }) async {
    if (!_ffiAvailable) {
      return SyncSummary(
        success: false,
        error: 'Rust FFI not available',
      );
    }

    try {
      final dbPath = await _getDatabasePath();
      final tasksJson = jsonEncode(tasks.map((t) => _taskToJson(t)).toList());
      final projectsJson = jsonEncode(projects.map((p) => _projectToJson(p)).toList());
      final labelsJson = jsonEncode(labels.map((l) => _labelToJson(l)).toList());

      AppLogger.info('Starting incremental sync via Rust FFI...');

      final resultJson = _ffi.incrementalSync(
        dbPath: dbPath,
        provider: provider,
        tasksJson: tasksJson,
        projectsJson: projectsJson,
        labelsJson: labelsJson,
        syncToken: syncToken,
      );

      final result = jsonDecode(resultJson) as Map<String, dynamic>;
      final summary = SyncSummary.fromJson(result);

      if (summary.success) {
        AppLogger.info('Incremental sync completed: ${summary.tasksAdded} added, ${summary.tasksUpdated} updated, ${summary.tasksDeleted} deleted');
      } else {
        AppLogger.error('Incremental sync failed: ${summary.error}');
      }

      return summary;
    } catch (e, stack) {
      AppLogger.error('Incremental sync failed', e, stack);
      return SyncSummary(
        success: false,
        error: e.toString(),
      );
    }
  }

  /// Clear all data for a provider (for re-sync)
  Future<bool> clearProviderData(String provider) async {
    if (!_ffiAvailable) return false;

    try {
      final dbPath = await _getDatabasePath();
      final resultJson = _ffi.clearProviderData(dbPath: dbPath, provider: provider);
      final result = jsonDecode(resultJson) as Map<String, dynamic>;
      return result['success'] == true;
    } catch (e) {
      AppLogger.error('Failed to clear provider data', e);
      return false;
    }
  }

  /// Queue a task completion for sync
  Future<bool> queueCompletion({
    required String taskId,
    required String provider,
    required String providerTaskId,
    required bool completed,
  }) async {
    if (!_ffiAvailable) return false;

    try {
      final dbPath = await _getDatabasePath();
      return _ffi.queueCompletion(
        dbPath: dbPath,
        taskId: taskId,
        provider: provider,
        providerTaskId: providerTaskId,
        completed: completed,
      );
    } catch (e) {
      AppLogger.error('Failed to queue completion', e);
      return false;
    }
  }

  /// Push a task completion to Todoist
  Future<bool> pushCompletion({
    required String taskId,
    required bool completed,
  }) async {
    if (_todoistApi == null) return false;

    try {
      // Extract the actual Todoist task ID (remove 'todoist_' prefix)
      final actualTaskId = taskId.startsWith('todoist_')
          ? taskId.substring(8)
          : taskId;

      if (completed) {
        await _todoistApi.completeTask(actualTaskId);
      } else {
        await _todoistApi.reopenTask(actualTaskId);
      }
      return true;
    } catch (e) {
      AppLogger.error('Failed to push completion for $taskId', e);
      return false;
    }
  }

  /// Sync pending completions to provider
  Future<int> syncPendingCompletions(String provider) async {
    if (!_ffiAvailable || _todoistApi == null) return 0;

    try {
      final dbPath = await _getDatabasePath();
      final completionsJson = _ffi.getPendingCompletions(dbPath: dbPath, provider: provider);
      final completions = jsonDecode(completionsJson) as List<dynamic>;

      int synced = 0;
      for (final completion in completions) {
        final providerTaskId = completion['provider_task_id'] as String;
        final completed = completion['completed'] as bool;
        final completionId = completion['id'] as String;

        final success = await pushCompletion(
          taskId: providerTaskId,
          completed: completed,
        );

        if (success) {
          _ffi.markCompletionSynced(dbPath: dbPath, completionId: completionId);
          synced++;
        }
      }

      return synced;
    } catch (e) {
      AppLogger.error('Failed to sync pending completions', e);
      return 0;
    }
  }

  /// Get current sync token for a provider
  Future<String?> getSyncToken(String provider) async {
    if (!_ffiAvailable) return null;

    try {
      final dbPath = await _getDatabasePath();
      return _ffi.getSyncToken(dbPath: dbPath, provider: provider);
    } catch (e) {
      return null;
    }
  }

  /// Check if FFI is available
  bool get isFfiAvailable => _ffiAvailable;

  // Helper to format DateTime as ISO8601 with Z suffix for UTC
  String? _formatDateTime(DateTime? dt) {
    if (dt == null) return null;
    return '${dt.toUtc().toIso8601String().split('.').first}Z';
  }

  // JSON conversion helpers
  // Note: source_task and integrations are omitted to avoid serialization issues
  // with complex nested objects. These can be added back once basic sync works.
  Map<String, dynamic> _taskToJson(TaskEntity task) => {
    'id': task.id,
    'title': task.title,
    'description': task.description,
    'is_completed': task.isCompleted,
    'priority': task.priority,
    'due_date': _formatDateTime(task.dueDate),
    'project_id': task.projectId,
    'labels': task.labels.map((l) => l.id).toList(),
    'order': 0,
    'parent_id': task.parentId,
    'created_at': _formatDateTime(task.createdAt),
    'updated_at': _formatDateTime(task.updatedAt),
  };

  Map<String, dynamic> _projectToJson(ProjectEntity project) => {
    'id': project.id,
    'name': project.name,
    'color': project.color,
    'is_favorite': project.isFavorite,
    'order': project.sortOrder,
    'parent_id': project.parentId,
  };

  Map<String, dynamic> _labelToJson(LabelEntity label) => {
    'id': label.id,
    'name': label.name,
    'color': label.color,
    'order': label.sortOrder,
    'is_favorite': false,
  };

  /// Perform full sync with Todoist (legacy method for compatibility)
  /// Fetches all data from API and returns it for processing
  Future<SyncResult> syncTodoist() async {
    return fetchFromTodoist();
  }
}
