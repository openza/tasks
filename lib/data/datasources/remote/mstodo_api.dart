import 'package:dio/dio.dart';

import '../../../core/constants/api_endpoints.dart';
import '../../../core/utils/logger.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import 'auth/token_manager.dart';

/// Microsoft Graph API client for To-Do with automatic token refresh
class MsToDoApi {
  final Dio _dio;
  String _accessToken;
  final TokenManager _tokenManager = TokenManager.instance;

  MsToDoApi({required String accessToken})
      : _accessToken = accessToken,
        _dio = Dio(BaseOptions(
          baseUrl: ApiEndpoints.msGraphBaseUrl,
          headers: {
            'Authorization': 'Bearer $accessToken',
            'Content-Type': 'application/json',
          },
        )) {
    _setupInterceptors();
  }

  void _setupInterceptors() {
    _dio.interceptors.add(InterceptorsWrapper(
      onError: (error, handler) async {
        // Handle 401 Unauthorized - try to refresh token
        if (error.response?.statusCode == 401) {
          AppLogger.warning('MS To-Do: Received 401, attempting token refresh');

          try {
            final newToken = await _tokenManager.forceRefreshMsToDoToken();
            if (newToken != null) {
              _accessToken = newToken;

              // Update the authorization header
              _dio.options.headers['Authorization'] = 'Bearer $newToken';

              // Retry the failed request with new token
              final options = error.requestOptions;
              options.headers['Authorization'] = 'Bearer $newToken';

              AppLogger.info('MS To-Do: Retrying request with refreshed token');
              final response = await _dio.fetch(options);
              return handler.resolve(response);
            } else {
              AppLogger.warning('MS To-Do: Token refresh returned null (cooldown or max attempts), not retrying');
            }
          } catch (e) {
            AppLogger.error('MS To-Do: Token refresh failed during retry', e);
          }
        }
        return handler.next(error);
      },
    ));
  }

  /// Update the access token (e.g., after manual refresh)
  void updateAccessToken(String token) {
    _accessToken = token;
    _dio.options.headers['Authorization'] = 'Bearer $token';
  }

  // ============ TASK LISTS (Projects) ============

  /// Get all task lists
  Future<List<ProjectEntity>> getAllLists() async {
    try {
      final response = await _dio.get(ApiEndpoints.msToDoLists);
      final List<dynamic> lists = response.data['value'] ?? [];

      return lists.map((l) => _mapMsToDoList(l)).toList();
    } catch (e, stack) {
      AppLogger.error('Failed to fetch MS To-Do lists', e, stack);
      rethrow;
    }
  }

  /// Get a single list
  Future<ProjectEntity?> getList(String id) async {
    try {
      final response = await _dio.get('${ApiEndpoints.msToDoLists}/$id');
      return _mapMsToDoList(response.data);
    } catch (e) {
      AppLogger.error('Failed to fetch MS To-Do list: $id', e);
      return null;
    }
  }

  // ============ TASKS ============

  /// Get all tasks from all lists
  Future<List<TaskEntity>> getAllTasks() async {
    try {
      final lists = await getAllLists();
      final List<TaskEntity> allTasks = [];

      for (final list in lists) {
        final tasks = await getTasksFromList(list.id.replaceFirst('mstodo_', ''));
        allTasks.addAll(tasks);
      }

      AppLogger.info('Fetched ${allTasks.length} tasks from MS To-Do');
      return allTasks;
    } catch (e, stack) {
      AppLogger.error('Failed to fetch all MS To-Do tasks', e, stack);
      rethrow;
    }
  }

  /// Get tasks from a specific list
  Future<List<TaskEntity>> getTasksFromList(String listId) async {
    final List<TaskEntity> tasks = [];
    String? nextLink;

    try {
      do {
        final Response response;
        if (nextLink != null) {
          response = await _dio.get(nextLink.replaceFirst(ApiEndpoints.msGraphBaseUrl, ''));
        } else {
          response = await _dio.get(
            '${ApiEndpoints.msToDoLists}/$listId/tasks',
            queryParameters: {'\$top': 100},
          );
        }

        final List<dynamic> results = response.data['value'] ?? [];
        nextLink = response.data['@odata.nextLink'];

        for (final task in results) {
          tasks.add(_mapMsToDoTask(task, listId));
        }
      } while (nextLink != null);

      return tasks;
    } catch (e, stack) {
      AppLogger.error('Failed to fetch MS To-Do tasks from list: $listId', e, stack);
      rethrow;
    }
  }

  /// Get a single task
  Future<TaskEntity?> getTask(String listId, String taskId) async {
    try {
      final response = await _dio.get(
        '${ApiEndpoints.msToDoLists}/$listId/tasks/$taskId',
      );
      return _mapMsToDoTask(response.data, listId);
    } catch (e) {
      AppLogger.error('Failed to fetch MS To-Do task: $taskId', e);
      return null;
    }
  }

  /// Create a new task
  Future<TaskEntity> createTask({
    required String listId,
    required String title,
    String? body,
    DateTime? dueDate,
    String? importance,
  }) async {
    try {
      final data = <String, dynamic>{
        'title': title,
      };

      if (body != null) {
        data['body'] = {'content': body, 'contentType': 'text'};
      }

      if (dueDate != null) {
        data['dueDateTime'] = {
          'dateTime': dueDate.toIso8601String(),
          'timeZone': 'UTC',
        };
      }

      if (importance != null) {
        data['importance'] = importance;
      }

      final response = await _dio.post(
        '${ApiEndpoints.msToDoLists}/$listId/tasks',
        data: data,
      );

      return _mapMsToDoTask(response.data, listId);
    } catch (e, stack) {
      AppLogger.error('Failed to create MS To-Do task', e, stack);
      rethrow;
    }
  }

  /// Update a task
  Future<TaskEntity?> updateTask({
    required String listId,
    required String taskId,
    String? title,
    String? body,
    DateTime? dueDate,
    String? importance,
  }) async {
    try {
      final data = <String, dynamic>{};

      if (title != null) data['title'] = title;
      if (body != null) data['body'] = {'content': body, 'contentType': 'text'};
      if (dueDate != null) {
        data['dueDateTime'] = {
          'dateTime': dueDate.toIso8601String(),
          'timeZone': 'UTC',
        };
      }
      if (importance != null) data['importance'] = importance;

      final response = await _dio.patch(
        '${ApiEndpoints.msToDoLists}/$listId/tasks/$taskId',
        data: data,
      );
      return _mapMsToDoTask(response.data, listId);
    } catch (e, stack) {
      AppLogger.error('Failed to update MS To-Do task: $taskId', e, stack);
      rethrow;
    }
  }

  /// Reopen a completed task
  Future<void> reopenTask(String listId, String taskId) async {
    try {
      await _dio.patch(
        '${ApiEndpoints.msToDoLists}/$listId/tasks/$taskId',
        data: {'status': 'notStarted'},
      );
      AppLogger.info('Reopened MS To-Do task: $taskId');
    } catch (e, stack) {
      AppLogger.error('Failed to reopen MS To-Do task: $taskId', e, stack);
      rethrow;
    }
  }

  /// Complete a task
  Future<void> completeTask(String listId, String taskId) async {
    try {
      await _dio.patch(
        '${ApiEndpoints.msToDoLists}/$listId/tasks/$taskId',
        data: {'status': 'completed'},
      );
      AppLogger.info('Completed MS To-Do task: $taskId');
    } catch (e, stack) {
      AppLogger.error('Failed to complete MS To-Do task: $taskId', e, stack);
      rethrow;
    }
  }

  /// Delete a task
  Future<void> deleteTask(String listId, String taskId) async {
    try {
      await _dio.delete('${ApiEndpoints.msToDoLists}/$listId/tasks/$taskId');
      AppLogger.info('Deleted MS To-Do task: $taskId');
    } catch (e, stack) {
      AppLogger.error('Failed to delete MS To-Do task: $taskId', e, stack);
      rethrow;
    }
  }

  // ============ MAPPERS ============

  TaskEntity _mapMsToDoTask(Map<String, dynamic> data, String listId) {
    DateTime? dueDate;
    String? dueTime;

    if (data['dueDateTime'] != null) {
      final dueDt = data['dueDateTime'];
      if (dueDt['dateTime'] != null) {
        final dt = DateTime.parse(dueDt['dateTime']);
        dueDate = dt;
        if (dt.hour != 0 || dt.minute != 0) {
          dueTime = '${dt.hour.toString().padLeft(2, '0')}:${dt.minute.toString().padLeft(2, '0')}';
        }
      }
    }

    // Map MS To-Do importance to priority
    final importance = data['importance'] as String? ?? 'normal';
    int priority;
    switch (importance) {
      case 'high':
        priority = 1;
        break;
      case 'low':
        priority = 4;
        break;
      default:
        priority = 2;
    }

    // Map status
    final status = data['status'] as String? ?? 'notStarted';
    TaskStatus taskStatus;
    switch (status) {
      case 'completed':
        taskStatus = TaskStatus.completed;
        break;
      case 'inProgress':
        taskStatus = TaskStatus.inProgress;
        break;
      default:
        taskStatus = TaskStatus.pending;
    }

    return TaskEntity(
      id: 'mstodo_${data['id']}',
      title: data['title'] ?? '',
      description: data['body']?['content'],
      projectId: 'mstodo_$listId',
      priority: priority,
      status: taskStatus,
      dueDate: dueDate,
      dueTime: dueTime,
      createdAt: data['createdDateTime'] != null
          ? DateTime.parse(data['createdDateTime'])
          : DateTime.now(),
      completedAt: data['completedDateTime'] != null
          ? DateTime.parse(data['completedDateTime']['dateTime'])
          : null,
      sourceTask: data,
      integrations: {
        'msToDo': {
          'id': data['id'],
          'listId': listId,
          'synced_at': DateTime.now().toIso8601String(),
        }
      },
      provider: TaskProvider.msToDo,
    );
  }

  ProjectEntity _mapMsToDoList(Map<String, dynamic> data) {
    // MS To-Do doesn't have colors by default, use a default based on wellknownListName
    String color = '#3b82f6';
    final wellknown = data['wellknownListName'] as String?;

    if (wellknown == 'defaultList') {
      color = '#808080'; // Inbox color
    } else if (wellknown == 'flaggedEmails') {
      color = '#ef4444';
    }

    return ProjectEntity(
      id: 'mstodo_${data['id']}',
      name: data['displayName'] ?? '',
      color: color,
      isFavorite: data['isOwner'] ?? false,
      createdAt: DateTime.now(),
      integrations: {
        'msToDo': {
          'id': data['id'],
          'wellknownListName': wellknown,
        }
      },
      provider: TaskProvider.msToDo,
    );
  }
}
