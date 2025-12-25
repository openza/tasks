import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
// ignore: deprecated_member_use
import 'package:flutter_riverpod/legacy.dart';

import '../../core/services/api_error_handler.dart';
import '../../core/utils/logger.dart';
import '../../data/datasources/remote/todoist_api.dart';
import '../../data/datasources/remote/mstodo_api.dart';
import '../../domain/entities/task.dart';
import '../../domain/entities/project.dart';
import '../../domain/entities/label.dart';
import '../../domain/entities/integration.dart';
import 'auth_provider.dart';
import 'database_provider.dart';
import 'integration_provider.dart';

/// Task source selection
/// @deprecated Use selectedProjectIdProvider instead for project-based filtering.
/// The 4-pane layout shows all sources unified, with projects grouped by provider.
enum TaskSource { all, openzaTasks, todoist, msToDo }

/// Task source state provider
/// @deprecated Use selectedProjectIdProvider from selected_project_provider.dart instead.
/// This provider will be removed in a future version.
final taskSourceProvider = StateProvider<TaskSource>((ref) => TaskSource.all);

/// Unified data containing tasks, projects, and labels
class UnifiedData {
  final List<TaskEntity> tasks;
  final List<ProjectEntity> projects;
  final List<LabelEntity> labels;
  final String? activeIntegrationId;
  final List<IntegrationEntity> configuredIntegrations;

  const UnifiedData({
    this.tasks = const [],
    this.projects = const [],
    this.labels = const [],
    this.activeIntegrationId,
    this.configuredIntegrations = const [],
  });
}

/// Provider for Todoist API client
final todoistApiProvider = FutureProvider<TodoistApi?>((ref) async {
  final token = await ref.watch(todoistTokenProvider.future);
  if (token == null) return null;
  return TodoistApi(accessToken: token);
});

/// Provider for MS To-Do API client
final msToDoApiProvider = FutureProvider<MsToDoApi?>((ref) async {
  final token = await ref.watch(msToDoTokenProvider.future);
  if (token == null) return null;
  return MsToDoApi(accessToken: token);
});

/// Provider for Todoist tasks with error handling
final todoistTasksProvider = FutureProvider<List<TaskEntity>>((ref) async {
  final api = await ref.watch(todoistApiProvider.future);
  if (api == null) return [];

  try {
    return await api.getAllTasks();
  } on DioException catch (e) {
    final error = ApiErrorHandler.handleDioError(e, context: 'Todoist');
    ApiErrorHandler.reportError(error);
    AppLogger.error('Todoist tasks fetch failed', e);
    return [];
  } catch (e) {
    final error = ApiErrorHandler.handleError(e, context: 'Todoist');
    ApiErrorHandler.reportError(error);
    return [];
  }
});

/// Provider for Todoist projects with error handling
final todoistProjectsProvider = FutureProvider<List<ProjectEntity>>((ref) async {
  final api = await ref.watch(todoistApiProvider.future);
  if (api == null) return [];

  try {
    return await api.getAllProjects();
  } on DioException catch (e) {
    final error = ApiErrorHandler.handleDioError(e, context: 'Todoist');
    ApiErrorHandler.reportError(error);
    return [];
  } catch (e) {
    final error = ApiErrorHandler.handleError(e, context: 'Todoist');
    ApiErrorHandler.reportError(error);
    return [];
  }
});

/// Provider for Todoist labels with error handling
final todoistLabelsProvider = FutureProvider<List<LabelEntity>>((ref) async {
  final api = await ref.watch(todoistApiProvider.future);
  if (api == null) return [];

  try {
    return await api.getAllLabels();
  } on DioException catch (e) {
    final error = ApiErrorHandler.handleDioError(e, context: 'Todoist');
    ApiErrorHandler.reportError(error);
    return [];
  } catch (e) {
    final error = ApiErrorHandler.handleError(e, context: 'Todoist');
    ApiErrorHandler.reportError(error);
    return [];
  }
});

/// Provider for MS To-Do tasks with error handling
final msToDoTasksProvider = FutureProvider<List<TaskEntity>>((ref) async {
  final api = await ref.watch(msToDoApiProvider.future);
  if (api == null) return [];

  try {
    return await api.getAllTasks();
  } on DioException catch (e) {
    final error = ApiErrorHandler.handleDioError(e, context: 'MS To-Do');
    ApiErrorHandler.reportError(error);
    return [];
  } catch (e) {
    final error = ApiErrorHandler.handleError(e, context: 'MS To-Do');
    ApiErrorHandler.reportError(error);
    return [];
  }
});

/// Provider for MS To-Do lists (projects) with error handling
final msToDoProjectsProvider = FutureProvider<List<ProjectEntity>>((ref) async {
  final api = await ref.watch(msToDoApiProvider.future);
  if (api == null) return [];

  try {
    return await api.getAllLists();
  } on DioException catch (e) {
    final error = ApiErrorHandler.handleDioError(e, context: 'MS To-Do');
    ApiErrorHandler.reportError(error);
    return [];
  } catch (e) {
    final error = ApiErrorHandler.handleError(e, context: 'MS To-Do');
    ApiErrorHandler.reportError(error);
    return [];
  }
});

/// Provider for local tasks from database
final localTasksProvider = FutureProvider<List<TaskEntity>>((ref) async {
  ref.keepAlive();
  final db = ref.watch(databaseProvider);
  final tasks = await db.getAllTasks();

  return tasks.map((t) {
    // Parse providerMetadata JSON if present
    Map<String, dynamic>? providerMetadata;
    if (t.providerMetadata != null) {
      try {
        providerMetadata = jsonDecode(t.providerMetadata!) as Map<String, dynamic>;
      } catch (_) {
        // Invalid JSON, ignore
      }
    }

    return TaskEntity(
      id: t.id,
      externalId: t.externalId,
      integrationId: t.integrationId,
      title: t.title,
      description: t.description,
      projectId: t.projectId,
      parentId: t.parentId,
      priority: t.priority,
      status: TaskStatus.fromString(t.status),
      dueDate: t.dueDate,
      dueTime: t.dueTime,
      notes: t.notes,
      providerMetadata: providerMetadata,
      createdAt: t.createdAt,
      updatedAt: t.updatedAt,
      completedAt: t.completedAt,
    );
  }).toList();
});

/// Provider for local projects
final localProjectsProvider = FutureProvider<List<ProjectEntity>>((ref) async {
  ref.keepAlive();
  final db = ref.watch(databaseProvider);
  final projects = await db.getAllProjects();

  return projects.map((p) {
    Map<String, dynamic>? providerMetadata;
    if (p.providerMetadata != null) {
      try {
        providerMetadata = jsonDecode(p.providerMetadata!) as Map<String, dynamic>;
      } catch (_) {}
    }

    return ProjectEntity(
      id: p.id,
      externalId: p.externalId,
      integrationId: p.integrationId,
      name: p.name,
      description: p.description,
      color: p.color,
      icon: p.icon,
      parentId: p.parentId,
      sortOrder: p.sortOrder,
      isFavorite: p.isFavorite,
      isArchived: p.isArchived,
      providerMetadata: providerMetadata,
      createdAt: p.createdAt,
      updatedAt: p.updatedAt,
    );
  }).toList();
});

/// Provider for local labels
final localLabelsProvider = FutureProvider<List<LabelEntity>>((ref) async {
  ref.keepAlive();
  final db = ref.watch(databaseProvider);
  final labels = await db.getAllLabels();

  return labels.map((l) {
    Map<String, dynamic>? providerMetadata;
    if (l.providerMetadata != null) {
      try {
        providerMetadata = jsonDecode(l.providerMetadata!) as Map<String, dynamic>;
      } catch (_) {}
    }

    return LabelEntity(
      id: l.id,
      externalId: l.externalId,
      integrationId: l.integrationId,
      name: l.name,
      color: l.color,
      description: l.description,
      sortOrder: l.sortOrder,
      isFavorite: l.isFavorite,
      providerMetadata: providerMetadata,
      createdAt: l.createdAt,
    );
  }).toList();
});

/// Unified tasks provider - combines all sources based on taskSource
final unifiedDataProvider = FutureProvider<UnifiedData>((ref) async {
  ref.keepAlive();
  final authState = ref.watch(authProvider);
  final taskSource = ref.watch(taskSourceProvider);
  final configuredIntegrations = await ref.watch(configuredIntegrationsProvider.future);

  List<TaskEntity> tasks = [];
  List<ProjectEntity> projects = [];
  List<LabelEntity> labels = [];

  // Get all local data from database
  final localTasks = await ref.watch(localTasksProvider.future);
  final localProjects = await ref.watch(localProjectsProvider.future);
  final localLabels = await ref.watch(localLabelsProvider.future);

  // Filter by source using integrationId
  if (taskSource == TaskSource.all) {
    tasks.addAll(localTasks);
    projects.addAll(localProjects);
    labels.addAll(localLabels);
  } else if (taskSource == TaskSource.openzaTasks) {
    // Only native Openza Tasks
    tasks.addAll(localTasks.where((t) => t.integrationId == 'openza_tasks'));
    projects.addAll(localProjects.where((p) => p.integrationId == 'openza_tasks'));
    labels.addAll(localLabels.where((l) => l.integrationId == 'openza_tasks'));
  } else if (taskSource == TaskSource.todoist) {
    // Only Todoist tasks from local DB
    tasks.addAll(localTasks.where((t) => t.integrationId == 'todoist'));
    projects.addAll(localProjects.where((p) => p.integrationId == 'todoist'));
    labels.addAll(localLabels.where((l) => l.integrationId == 'todoist'));
  } else if (taskSource == TaskSource.msToDo) {
    // Only MS To-Do tasks from local DB
    tasks.addAll(localTasks.where((t) => t.integrationId == 'msToDo'));
    projects.addAll(localProjects.where((p) => p.integrationId == 'msToDo'));
    labels.addAll(localLabels.where((l) => l.integrationId == 'msToDo'));
  }

  // MS To-Do sync not yet implemented in Rust, fetch from API for now
  // TODO: Once MS To-Do sync is implemented in Rust, remove this section
  if ((taskSource == TaskSource.msToDo || taskSource == TaskSource.all) &&
      authState.msToDoAuthenticated) {
    try {
      final msToDoTasks = await ref.watch(msToDoTasksProvider.future);
      final msToDoProjects = await ref.watch(msToDoProjectsProvider.future);

      final completedCount = msToDoTasks.where((t) => t.isCompleted).length;
      final activeCount = msToDoTasks.where((t) => !t.isCompleted).length;
      AppLogger.info('UnifiedData: Adding ${msToDoTasks.length} MS To-Do tasks (active: $activeCount, completed: $completedCount)');

      tasks.addAll(msToDoTasks);
      projects.addAll(msToDoProjects);
    } catch (e) {
      AppLogger.error('UnifiedData: Failed to fetch MS To-Do tasks', e);
    }
  }

  // Sort tasks by priority, then due date, then created date
  tasks.sort((a, b) {
    // Priority first (lower number = higher priority)
    final priorityCompare = a.priority.compareTo(b.priority);
    if (priorityCompare != 0) return priorityCompare;

    // Then due date (nulls last)
    if (a.dueDate != null && b.dueDate != null) {
      return a.dueDate!.compareTo(b.dueDate!);
    }
    if (a.dueDate != null) return -1;
    if (b.dueDate != null) return 1;

    // Finally created date (newest first)
    return b.createdAt.compareTo(a.createdAt);
  });

  // Sort projects (favorites first, then by sort order)
  projects.sort((a, b) {
    if (a.isFavorite != b.isFavorite) {
      return a.isFavorite ? -1 : 1;
    }
    return a.sortOrder.compareTo(b.sortOrder);
  });

  final totalCompleted = tasks.where((t) => t.isCompleted).length;
  final totalActive = tasks.where((t) => !t.isCompleted).length;
  AppLogger.info('UnifiedData final: ${tasks.length} tasks (active: $totalActive, completed: $totalCompleted)');

  return UnifiedData(
    tasks: tasks,
    projects: projects,
    labels: labels,
    activeIntegrationId: authState.activeIntegrationId,
    configuredIntegrations: configuredIntegrations,
  );
});

/// Provider for today's tasks
final todayTasksProvider = FutureProvider<List<TaskEntity>>((ref) async {
  final data = await ref.watch(unifiedDataProvider.future);
  return data.tasks.where((t) => t.isDueToday && !t.isCompleted).toList();
});

/// Provider for overdue tasks
final overdueTasksProvider = FutureProvider<List<TaskEntity>>((ref) async {
  final data = await ref.watch(unifiedDataProvider.future);
  return data.tasks.where((t) => t.isOverdue && !t.isCompleted).toList();
});

/// Provider for completed tasks
final completedTasksProvider = FutureProvider<List<TaskEntity>>((ref) async {
  final data = await ref.watch(unifiedDataProvider.future);
  final completed = data.tasks.where((t) => t.isCompleted).toList();
  // Sort by completion date (most recent first)
  completed.sort((a, b) {
    if (a.completedAt == null && b.completedAt == null) return 0;
    if (a.completedAt == null) return 1;
    if (b.completedAt == null) return -1;
    return b.completedAt!.compareTo(a.completedAt!);
  });
  return completed;
});

/// Provider for labeled tasks (Next Actions)
final labeledTasksProvider = FutureProvider<List<TaskEntity>>((ref) async {
  final data = await ref.watch(unifiedDataProvider.future);
  return data.tasks.where((t) => t.hasLabels && !t.isCompleted).toList();
});

/// Provider for task statistics
final taskStatisticsProvider = FutureProvider<Map<String, int>>((ref) async {
  final data = await ref.watch(unifiedDataProvider.future);
  final tasks = data.tasks;

  return {
    'total': tasks.length,
    'active': tasks.where((t) => !t.isCompleted).length,
    'completed': tasks.where((t) => t.isCompleted).length,
    'overdue': tasks.where((t) => t.isOverdue).length,
    'today': tasks.where((t) => t.isDueToday && !t.isCompleted).length,
  };
});
