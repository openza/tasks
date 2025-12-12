import 'package:dio/dio.dart';

import '../../../core/constants/api_endpoints.dart';
import '../../../core/utils/logger.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import '../../../domain/entities/label.dart';

/// Todoist REST API client
class TodoistApi {
  final Dio _dio;
  final String _accessToken;

  TodoistApi({required String accessToken})
      : _accessToken = accessToken,
        _dio = Dio(BaseOptions(
          baseUrl: ApiEndpoints.todoistBaseUrl,
          headers: {
            'Authorization': 'Bearer $accessToken',
            'Content-Type': 'application/json',
          },
        ));

  // ============ TASKS ============

  /// Get all tasks with pagination
  Future<List<TaskEntity>> getAllTasks() async {
    final List<TaskEntity> allTasks = [];

    try {
      final response = await _dio.get(ApiEndpoints.todoistTasks);

      // Todoist REST API v2 returns tasks directly as a List
      final List<dynamic> results;
      if (response.data is List) {
        results = response.data;
      } else if (response.data is Map && response.data['results'] != null) {
        // Handle potential paginated response format
        results = response.data['results'];
      } else {
        results = [];
      }

      for (final task in results) {
        allTasks.add(_mapTodoistTask(task));
      }

      AppLogger.info('Fetched ${allTasks.length} tasks from Todoist');
      return allTasks;
    } catch (e, stack) {
      AppLogger.error('Failed to fetch Todoist tasks', e, stack);
      rethrow;
    }
  }

  /// Get a single task by ID
  Future<TaskEntity?> getTask(String id) async {
    try {
      final response = await _dio.get('${ApiEndpoints.todoistTasks}/$id');
      return _mapTodoistTask(response.data);
    } catch (e) {
      AppLogger.error('Failed to fetch Todoist task: $id', e);
      return null;
    }
  }

  /// Create a new task
  Future<TaskEntity?> createTask({
    required String content,
    String? description,
    String? projectId,
    int? priority,
    String? dueString,
    DateTime? dueDate,
    List<String>? labels,
  }) async {
    try {
      final response = await _dio.post(
        ApiEndpoints.todoistTasks,
        data: {
          'content': content,
          if (description != null) 'description': description,
          if (projectId != null) 'project_id': projectId,
          if (priority != null) 'priority': priority,
          if (dueString != null) 'due_string': dueString,
          if (dueDate != null) 'due_date': dueDate.toIso8601String().split('T')[0],
          if (labels != null && labels.isNotEmpty) 'labels': labels,
        },
      );
      return _mapTodoistTask(response.data);
    } catch (e, stack) {
      AppLogger.error('Failed to create Todoist task', e, stack);
      rethrow;
    }
  }

  /// Update a task
  Future<TaskEntity?> updateTask({
    required String taskId,
    String? content,
    String? description,
    int? priority,
    DateTime? dueDate,
    List<String>? labels,
  }) async {
    try {
      final response = await _dio.post(
        '${ApiEndpoints.todoistTasks}/$taskId',
        data: {
          if (content != null) 'content': content,
          if (description != null) 'description': description,
          if (priority != null) 'priority': priority,
          if (dueDate != null) 'due_date': dueDate.toIso8601String().split('T')[0],
          if (labels != null) 'labels': labels,
        },
      );
      return _mapTodoistTask(response.data);
    } catch (e, stack) {
      AppLogger.error('Failed to update Todoist task: $taskId', e, stack);
      rethrow;
    }
  }

  /// Reopen a completed task
  Future<void> reopenTask(String id) async {
    try {
      await _dio.post('${ApiEndpoints.todoistTasks}/$id/reopen');
      AppLogger.info('Reopened Todoist task: $id');
    } catch (e, stack) {
      AppLogger.error('Failed to reopen Todoist task: $id', e, stack);
      rethrow;
    }
  }

  /// Complete a task
  Future<void> completeTask(String id) async {
    try {
      await _dio.post('${ApiEndpoints.todoistTasks}/$id/close');
      AppLogger.info('Completed Todoist task: $id');
    } catch (e, stack) {
      AppLogger.error('Failed to complete Todoist task: $id', e, stack);
      rethrow;
    }
  }

  /// Delete a task
  Future<void> deleteTask(String id) async {
    try {
      await _dio.delete('${ApiEndpoints.todoistTasks}/$id');
      AppLogger.info('Deleted Todoist task: $id');
    } catch (e, stack) {
      AppLogger.error('Failed to delete Todoist task: $id', e, stack);
      rethrow;
    }
  }

  // ============ PROJECTS ============

  /// Get all projects
  Future<List<ProjectEntity>> getAllProjects() async {
    try {
      final response = await _dio.get(ApiEndpoints.todoistProjects);
      final List<dynamic> projects = response.data;

      return projects.map((p) => _mapTodoistProject(p)).toList();
    } catch (e, stack) {
      AppLogger.error('Failed to fetch Todoist projects', e, stack);
      rethrow;
    }
  }

  /// Get a single project
  Future<ProjectEntity?> getProject(String id) async {
    try {
      final response = await _dio.get('${ApiEndpoints.todoistProjects}/$id');
      return _mapTodoistProject(response.data);
    } catch (e) {
      AppLogger.error('Failed to fetch Todoist project: $id', e);
      return null;
    }
  }

  // ============ LABELS ============

  /// Get all labels
  Future<List<LabelEntity>> getAllLabels() async {
    try {
      final response = await _dio.get(ApiEndpoints.todoistLabels);
      final List<dynamic> labels = response.data;

      return labels.map((l) => _mapTodoistLabel(l)).toList();
    } catch (e, stack) {
      AppLogger.error('Failed to fetch Todoist labels', e, stack);
      rethrow;
    }
  }

  // ============ MAPPERS ============

  TaskEntity _mapTodoistTask(Map<String, dynamic> data) {
    DateTime? dueDate;
    String? dueTime;

    if (data['due'] != null) {
      final due = data['due'];
      if (due['date'] != null) {
        dueDate = DateTime.parse(due['date']);
      }
      if (due['datetime'] != null) {
        final dt = DateTime.parse(due['datetime']);
        dueDate = dt;
        dueTime = '${dt.hour.toString().padLeft(2, '0')}:${dt.minute.toString().padLeft(2, '0')}';
      }
    }

    // Map Todoist priority (4=highest, 1=lowest) to our system (1=highest, 4=lowest)
    final todoistPriority = data['priority'] as int? ?? 1;
    final priority = 5 - todoistPriority; // Invert: 4->1, 3->2, 2->3, 1->4

    return TaskEntity(
      id: 'todoist_${data['id']}',
      title: data['content'] ?? '',
      description: data['description'],
      projectId: data['project_id']?.toString(),
      parentId: data['parent_id']?.toString(),
      priority: priority,
      status: data['is_completed'] == true ? TaskStatus.completed : TaskStatus.pending,
      dueDate: dueDate,
      dueTime: dueTime,
      createdAt: DateTime.parse(data['created_at'] ?? DateTime.now().toIso8601String()),
      completedAt: data['completed_at'] != null ? DateTime.parse(data['completed_at']) : null,
      labels: (data['labels'] as List<dynamic>?)
              ?.map((l) => LabelEntity(
                    id: 'todoist_label_$l',
                    name: l.toString(),
                    createdAt: DateTime.now(),
                    provider: TaskProvider.todoist,
                  ))
              .toList() ??
          [],
      sourceTask: data,
      integrations: {'todoist': {'id': data['id'], 'synced_at': DateTime.now().toIso8601String()}},
      provider: TaskProvider.todoist,
    );
  }

  ProjectEntity _mapTodoistProject(Map<String, dynamic> data) {
    return ProjectEntity(
      id: 'todoist_${data['id']}',
      name: data['name'] ?? '',
      color: _todoistColorToHex(data['color']),
      parentId: data['parent_id']?.toString(),
      sortOrder: data['order'] ?? 0,
      isFavorite: data['is_favorite'] ?? false,
      isArchived: data['is_archived'] ?? false,
      createdAt: DateTime.now(),
      integrations: {'todoist': {'id': data['id']}},
      provider: TaskProvider.todoist,
    );
  }

  LabelEntity _mapTodoistLabel(Map<String, dynamic> data) {
    return LabelEntity(
      id: 'todoist_${data['id']}',
      name: data['name'] ?? '',
      color: _todoistColorToHex(data['color']),
      sortOrder: data['order'] ?? 0,
      createdAt: DateTime.now(),
      integrations: {'todoist': {'id': data['id']}},
      provider: TaskProvider.todoist,
    );
  }

  String _todoistColorToHex(String? colorName) {
    const colorMap = {
      'berry_red': '#b8255f',
      'red': '#db4035',
      'orange': '#ff9933',
      'yellow': '#fad000',
      'olive_green': '#afb83b',
      'lime_green': '#7ecc49',
      'green': '#299438',
      'mint_green': '#6accbc',
      'teal': '#158fad',
      'sky_blue': '#14aaf5',
      'light_blue': '#96c3eb',
      'blue': '#4073ff',
      'grape': '#884dff',
      'violet': '#af38eb',
      'lavender': '#eb96eb',
      'magenta': '#e05194',
      'salmon': '#ff8d85',
      'charcoal': '#808080',
      'grey': '#b8b8b8',
      'taupe': '#ccac93',
    };
    return colorMap[colorName] ?? '#808080';
  }
}
