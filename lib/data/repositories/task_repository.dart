import 'dart:convert';

import 'package:drift/drift.dart';

import '../../core/utils/logger.dart';
import '../../domain/entities/task.dart';
import '../../domain/entities/label.dart';
import '../datasources/local/database/database.dart';
import '../datasources/remote/todoist_api.dart';
import '../datasources/remote/mstodo_api.dart';

/// Repository for task operations across all providers
class TaskRepository {
  final AppDatabase _database;
  final TodoistApi? _todoistApi;
  final MsToDoApi? _msToDoApi;

  TaskRepository({
    required AppDatabase database,
    TodoistApi? todoistApi,
    MsToDoApi? msToDoApi,
  })  : _database = database,
        _todoistApi = todoistApi,
        _msToDoApi = msToDoApi;

  // ============ CREATE ============

  /// Create a new task
  Future<TaskEntity> createTask(TaskEntity task) async {
    switch (task.integrationId) {
      case 'todoist':
        if (_todoistApi != null) {
          return _createTodoistTask(task);
        }
        break;
      case 'msToDo':
        if (_msToDoApi != null) {
          return _createMsToDoTask(task);
        }
        break;
    }
    return _createLocalTask(task);
  }

  Future<TaskEntity> _createLocalTask(TaskEntity task) async {
    final providerMetadataJson = task.providerMetadata != null
        ? jsonEncode(task.providerMetadata)
        : null;

    await _database.createTask(TasksCompanion.insert(
      id: task.id,
      integrationId: task.integrationId,
      title: task.title,
      description: Value(task.description),
      projectId: Value(task.projectId),
      parentId: Value(task.parentId),
      priority: Value(task.priority),
      status: Value(task.status.value),
      dueDate: Value(task.dueDate),
      dueTime: Value(task.dueTime),
      notes: Value(task.notes),
      providerMetadata: Value(providerMetadataJson),
      externalId: Value(task.externalId),
    ));

    // Add labels
    for (final label in task.labels) {
      await _database.addLabelToTask(task.id, label.id);
    }

    return task;
  }

  Future<TaskEntity> _createTodoistTask(TaskEntity task) async {
    // Create via API
    final createdTask = await _todoistApi!.createTask(
      content: task.title,
      description: task.description,
      projectId: task.projectId,
      priority: _mapPriorityToTodoist(task.priority),
      dueDate: task.dueDate,
      labels: task.labels.map((l) => l.name).toList(),
    );

    if (createdTask == null) {
      throw Exception('Failed to create task in Todoist');
    }

    // Also save locally for offline access
    await _createLocalTask(createdTask);

    return createdTask;
  }

  Future<TaskEntity> _createMsToDoTask(TaskEntity task) async {
    // Create via API
    final createdTask = await _msToDoApi!.createTask(
      listId: task.projectId ?? 'tasks', // Default to main tasks list
      title: task.title,
      body: task.description,
      dueDate: task.dueDate,
      importance: _mapPriorityToMsToDo(task.priority),
    );

    // Also save locally for offline access
    await _createLocalTask(createdTask);

    return createdTask;
  }

  // ============ READ ============

  /// Get all tasks from all sources
  Future<List<TaskEntity>> getAllTasks() async {
    final tasks = <TaskEntity>[];

    // Get local tasks
    final localTasks = await _getLocalTasks();
    tasks.addAll(localTasks);

    // Get Todoist tasks
    if (_todoistApi != null) {
      try {
        final todoistTasks = await _todoistApi!.getAllTasks();
        tasks.addAll(todoistTasks);
      } catch (e) {
        AppLogger.warning('Failed to fetch Todoist tasks', e);
      }
    }

    // Get MS To-Do tasks
    if (_msToDoApi != null) {
      try {
        final msToDoTasks = await _msToDoApi!.getAllTasks();
        tasks.addAll(msToDoTasks);
      } catch (e) {
        AppLogger.warning('Failed to fetch MS To-Do tasks', e);
      }
    }

    return tasks;
  }

  Future<List<TaskEntity>> _getLocalTasks() async {
    final dbTasks = await _database.getAllTasks();
    final tasks = <TaskEntity>[];

    for (final t in dbTasks) {
      final labels = await _database.getLabelsForTask(t.id);

      Map<String, dynamic>? providerMetadata;
      if (t.providerMetadata != null) {
        try {
          providerMetadata = jsonDecode(t.providerMetadata!) as Map<String, dynamic>;
        } catch (_) {}
      }

      tasks.add(TaskEntity(
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
        labels: labels
            .map((l) => LabelEntity(
                  id: l.id,
                  externalId: l.externalId,
                  integrationId: l.integrationId,
                  name: l.name,
                  color: l.color,
                  createdAt: l.createdAt,
                ))
            .toList(),
      ));
    }

    return tasks;
  }

  /// Get task by ID
  Future<TaskEntity?> getTaskById(String id) async {
    final dbTask = await _database.getTaskById(id);
    if (dbTask == null) return null;

    final labels = await _database.getLabelsForTask(id);

    Map<String, dynamic>? providerMetadata;
    if (dbTask.providerMetadata != null) {
      try {
        providerMetadata = jsonDecode(dbTask.providerMetadata!) as Map<String, dynamic>;
      } catch (_) {}
    }

    return TaskEntity(
      id: dbTask.id,
      externalId: dbTask.externalId,
      integrationId: dbTask.integrationId,
      title: dbTask.title,
      description: dbTask.description,
      projectId: dbTask.projectId,
      parentId: dbTask.parentId,
      priority: dbTask.priority,
      status: TaskStatus.fromString(dbTask.status),
      dueDate: dbTask.dueDate,
      dueTime: dbTask.dueTime,
      notes: dbTask.notes,
      providerMetadata: providerMetadata,
      createdAt: dbTask.createdAt,
      updatedAt: dbTask.updatedAt,
      completedAt: dbTask.completedAt,
      labels: labels
          .map((l) => LabelEntity(
                id: l.id,
                externalId: l.externalId,
                integrationId: l.integrationId,
                name: l.name,
                color: l.color,
                createdAt: l.createdAt,
              ))
          .toList(),
    );
  }

  // ============ UPDATE ============

  /// Update a task
  Future<TaskEntity> updateTask(TaskEntity task) async {
    switch (task.integrationId) {
      case 'todoist':
        if (_todoistApi != null) {
          return _updateTodoistTask(task);
        }
        break;
      case 'msToDo':
        if (_msToDoApi != null) {
          return _updateMsToDoTask(task);
        }
        break;
    }
    return _updateLocalTask(task);
  }

  Future<TaskEntity> _updateLocalTask(TaskEntity task) async {
    // Check if this is a synced task (has external_id or integration prefix)
    // For synced tasks, only update local-enhancement fields to avoid
    // conflicting with Rust sync engine which owns the core sync fields
    final isSyncedTask = task.integrationId != 'openza_tasks';

    final providerMetadataJson = task.providerMetadata != null
        ? jsonEncode(task.providerMetadata)
        : null;

    if (isSyncedTask) {
      // Only update local-enhancement fields for synced tasks
      // Core fields (title, description, priority, status, due_date, etc.)
      // are owned by the sync engine and should not be modified directly
      await _database.updateTask(
        task.id,
        TasksCompanion(
          notes: Value(task.notes),
          providerMetadata: Value(providerMetadataJson),
          updatedAt: Value(DateTime.now()),
        ),
      );
    } else {
      // Full update for local tasks
      await _database.updateTask(
        task.id,
        TasksCompanion(
          title: Value(task.title),
          description: Value(task.description),
          projectId: Value(task.projectId),
          priority: Value(task.priority),
          status: Value(task.status.value),
          dueDate: Value(task.dueDate),
          dueTime: Value(task.dueTime),
          notes: Value(task.notes),
          providerMetadata: Value(providerMetadataJson),
          updatedAt: Value(DateTime.now()),
        ),
      );
    }
    return task;
  }

  Future<TaskEntity> _updateTodoistTask(TaskEntity task) async {
    // Get the external ID for API call
    final apiTaskId = task.externalId ?? task.id;

    await _todoistApi!.updateTask(
      taskId: apiTaskId,
      content: task.title,
      description: task.description,
      priority: _mapPriorityToTodoist(task.priority),
      dueDate: task.dueDate,
      labels: task.labels.map((l) => l.name).toList(),
    );
    await _updateLocalTask(task);
    return task;
  }

  Future<TaskEntity> _updateMsToDoTask(TaskEntity task) async {
    // Get the external ID and list ID for API call
    final apiTaskId = task.externalId ?? task.id;
    final listId = task.projectId ?? 'tasks';

    await _msToDoApi!.updateTask(
      listId: listId,
      taskId: apiTaskId,
      title: task.title,
      body: task.description,
      dueDate: task.dueDate,
      importance: _mapPriorityToMsToDo(task.priority),
    );
    await _updateLocalTask(task);
    return task;
  }

  // ============ DELETE ============

  /// Delete a task
  Future<void> deleteTask(TaskEntity task) async {
    // Get external ID for API calls
    final apiTaskId = task.externalId ?? task.id;

    switch (task.integrationId) {
      case 'todoist':
        if (_todoistApi != null) {
          await _todoistApi!.deleteTask(apiTaskId);
        }
        break;
      case 'msToDo':
        if (_msToDoApi != null) {
          final listId = task.projectId ?? 'tasks';
          await _msToDoApi!.deleteTask(listId, apiTaskId);
        }
        break;
    }
    await _database.deleteTask(task.id);
  }

  // ============ COMPLETE ============

  /// Complete a task
  /// Uses outbox pattern: updates local DB immediately and queues for sync
  /// This ensures crash safety and offline support
  Future<TaskEntity> completeTask(TaskEntity task) async {
    final completedTask = task.copyWith(
      status: TaskStatus.completed,
      completedAt: DateTime.now(),
    );

    // Get external ID for provider API
    final providerTaskId = task.externalId ?? task.id;

    // For synced tasks, use transactional outbox pattern
    // For local tasks, just update directly
    switch (task.integrationId) {
      case 'todoist':
        // Queue for sync - Rust will push to API later
        await _database.completeTaskWithQueue(
          taskId: task.id,
          provider: 'todoist',
          providerTaskId: providerTaskId,
        );
        break;
      case 'msToDo':
        // Queue for sync - Rust will push to API later
        await _database.completeTaskWithQueue(
          taskId: task.id,
          provider: 'msToDo',
          providerTaskId: providerTaskId,
        );
        break;
      default:
        // Local tasks don't need sync queueing
        await _database.completeTask(task.id);
        break;
    }

    return completedTask;
  }

  /// Reopen a completed task
  /// Uses outbox pattern: updates local DB immediately and queues for sync
  Future<TaskEntity> reopenTask(TaskEntity task) async {
    final reopenedTask = task.copyWith(
      status: TaskStatus.pending,
      completedAt: null,
    );

    // Get external ID for provider API
    final providerTaskId = task.externalId ?? task.id;

    // For synced tasks, use transactional outbox pattern
    switch (task.integrationId) {
      case 'todoist':
        // Queue for sync - Rust will push to API later
        await _database.reopenTaskWithQueue(
          taskId: task.id,
          provider: 'todoist',
          providerTaskId: providerTaskId,
        );
        break;
      case 'msToDo':
        // Queue for sync - Rust will push to API later
        await _database.reopenTaskWithQueue(
          taskId: task.id,
          provider: 'msToDo',
          providerTaskId: providerTaskId,
        );
        break;
      default:
        // Local tasks don't need sync queueing
        await _database.updateTask(
          task.id,
          TasksCompanion(
            status: const Value('pending'),
            completedAt: const Value(null),
            updatedAt: Value(DateTime.now()),
          ),
        );
        break;
    }

    return reopenedTask;
  }

  // ============ HELPERS ============

  int _mapPriorityToTodoist(int priority) {
    // Our: 1=High, 2=Medium, 3=Normal, 4=Low
    // Todoist: 4=Urgent, 3=High, 2=Medium, 1=Low
    switch (priority) {
      case 1:
        return 4;
      case 2:
        return 3;
      case 3:
        return 2;
      case 4:
        return 1;
      default:
        return 1;
    }
  }

  String _mapPriorityToMsToDo(int priority) {
    // Our: 1=High, 2=Medium, 3=Normal, 4=Low
    // MS To-Do: high, normal, low
    switch (priority) {
      case 1:
        return 'high';
      case 2:
      case 3:
        return 'normal';
      case 4:
        return 'low';
      default:
        return 'normal';
    }
  }
}
