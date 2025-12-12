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
import 'auth_provider.dart';
import 'database_provider.dart';

/// Task source selection
enum TaskSource { local, provider, all }

/// Task source state provider
final taskSourceProvider = StateProvider<TaskSource>((ref) => TaskSource.all);

/// Unified data containing tasks, projects, and labels
class UnifiedData {
  final List<TaskEntity> tasks;
  final List<ProjectEntity> projects;
  final List<LabelEntity> labels;
  final TaskProvider? activeProvider;
  final List<TaskProvider> availableProviders;

  const UnifiedData({
    this.tasks = const [],
    this.projects = const [],
    this.labels = const [],
    this.activeProvider,
    this.availableProviders = const [],
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
    AppLogger.error('MS To-Do tasks fetch failed', e);
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
  final db = ref.watch(databaseProvider);
  final tasks = await db.getAllTasks();

  return tasks.map((t) => TaskEntity(
    id: t.id,
    title: t.title,
    description: t.description,
    projectId: t.projectId,
    parentId: t.parentId,
    priority: t.priority,
    status: TaskStatus.fromString(t.status),
    dueDate: t.dueDate,
    dueTime: t.dueTime,
    estimatedDuration: t.estimatedDuration,
    actualDuration: t.actualDuration,
    energyLevel: t.energyLevel,
    context: TaskContext.fromString(t.context),
    focusTime: t.focusTime,
    notes: t.notes,
    createdAt: t.createdAt,
    updatedAt: t.updatedAt,
    completedAt: t.completedAt,
    provider: TaskProvider.local,
  )).toList();
});

/// Provider for local projects
final localProjectsProvider = FutureProvider<List<ProjectEntity>>((ref) async {
  final db = ref.watch(databaseProvider);
  final projects = await db.getAllProjects();

  return projects.map((p) => ProjectEntity(
    id: p.id,
    name: p.name,
    description: p.description,
    color: p.color,
    icon: p.icon,
    parentId: p.parentId,
    sortOrder: p.sortOrder,
    isFavorite: p.isFavorite,
    isArchived: p.isArchived,
    createdAt: p.createdAt,
    updatedAt: p.updatedAt,
    provider: TaskProvider.local,
  )).toList();
});

/// Provider for local labels
final localLabelsProvider = FutureProvider<List<LabelEntity>>((ref) async {
  final db = ref.watch(databaseProvider);
  final labels = await db.getAllLabels();

  return labels.map((l) => LabelEntity(
    id: l.id,
    name: l.name,
    color: l.color,
    description: l.description,
    sortOrder: l.sortOrder,
    createdAt: l.createdAt,
    provider: TaskProvider.local,
  )).toList();
});

/// Unified tasks provider - combines all sources based on taskSource
final unifiedDataProvider = FutureProvider<UnifiedData>((ref) async {
  final authState = ref.watch(authProvider);
  final taskSource = ref.watch(taskSourceProvider);

  List<TaskEntity> tasks = [];
  List<ProjectEntity> projects = [];
  List<LabelEntity> labels = [];
  List<TaskProvider> availableProviders = [TaskProvider.local];

  // Always include local data if source is 'local' or 'all'
  if (taskSource == TaskSource.local || taskSource == TaskSource.all) {
    final localTasks = await ref.watch(localTasksProvider.future);
    final localProjects = await ref.watch(localProjectsProvider.future);
    final localLabels = await ref.watch(localLabelsProvider.future);

    tasks.addAll(localTasks);
    projects.addAll(localProjects);
    labels.addAll(localLabels);
  }

  // Include provider data if source is 'provider' or 'all'
  if (taskSource == TaskSource.provider || taskSource == TaskSource.all) {
    // Todoist
    if (authState.todoistAuthenticated) {
      availableProviders.add(TaskProvider.todoist);

      try {
        final todoistTasks = await ref.watch(todoistTasksProvider.future);
        final todoistProjects = await ref.watch(todoistProjectsProvider.future);
        final todoistLabels = await ref.watch(todoistLabelsProvider.future);

        tasks.addAll(todoistTasks);
        projects.addAll(todoistProjects);
        labels.addAll(todoistLabels);
      } catch (_) {
        // Handle error silently for now
      }
    }

    // MS To-Do
    if (authState.msToDoAuthenticated) {
      availableProviders.add(TaskProvider.msToDo);

      try {
        final msToDoTasks = await ref.watch(msToDoTasksProvider.future);
        final msToDoProjects = await ref.watch(msToDoProjectsProvider.future);

        tasks.addAll(msToDoTasks);
        projects.addAll(msToDoProjects);
      } catch (_) {
        // Handle error silently for now
      }
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

  return UnifiedData(
    tasks: tasks,
    projects: projects,
    labels: labels,
    activeProvider: authState.activeProvider,
    availableProviders: availableProviders,
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
