import 'dart:io';

import 'package:drift/drift.dart';
import 'package:drift/native.dart';
import 'package:path_provider/path_provider.dart';
import 'package:path/path.dart' as p;

import 'tables.dart';

part 'database.g.dart';

@DriftDatabase(tables: [
  Projects,
  Tasks,
  Labels,
  TaskLabels,
  TimeEntries,
  TaskEnhancements,
  Integrations,
])
class AppDatabase extends _$AppDatabase {
  AppDatabase() : super(_openConnection());

  // For testing
  AppDatabase.forTesting(super.e);

  @override
  int get schemaVersion => 1;

  @override
  MigrationStrategy get migration => MigrationStrategy(
        onCreate: (Migrator m) async {
          await m.createAll();
          await _insertDefaultData();
        },
        onUpgrade: (Migrator m, int from, int to) async {
          // Handle future migrations here
        },
      );

  Future<void> _insertDefaultData() async {
    await batch((batch) {
      // Insert default projects
      batch.insertAll(projects, [
        ProjectsCompanion.insert(
          id: 'proj_inbox',
          name: 'Inbox',
          description: const Value('Default inbox for new tasks'),
          color: const Value('#808080'),
          icon: const Value('inbox'),
        ),
        ProjectsCompanion.insert(
          id: 'proj_work',
          name: 'Work',
          description: const Value('Work-related tasks'),
          color: const Value('#3b82f6'),
          icon: const Value('briefcase'),
        ),
        ProjectsCompanion.insert(
          id: 'proj_personal',
          name: 'Personal',
          description: const Value('Personal tasks and goals'),
          color: const Value('#10b981'),
          icon: const Value('user'),
        ),
      ]);

      // Insert default labels
      batch.insertAll(labels, [
        LabelsCompanion.insert(
          id: 'label_urgent',
          name: 'urgent',
          color: const Value('#ef4444'),
        ),
        LabelsCompanion.insert(
          id: 'label_important',
          name: 'important',
          color: const Value('#f59e0b'),
        ),
        LabelsCompanion.insert(
          id: 'label_learning',
          name: 'learning',
          color: const Value('#3b82f6'),
        ),
        LabelsCompanion.insert(
          id: 'label_review',
          name: 'review',
          color: const Value('#8b5cf6'),
        ),
      ]);
    });
  }

  // ============ TASK OPERATIONS ============

  /// Get all tasks with optional filters
  Future<List<Task>> getAllTasks({
    String? projectId,
    String? status,
    DateTime? dueBefore,
    DateTime? dueAfter,
  }) async {
    final query = select(tasks);

    if (projectId != null) {
      query.where((t) => t.projectId.equals(projectId));
    }
    if (status != null) {
      query.where((t) => t.status.equals(status));
    }
    if (dueBefore != null) {
      query.where((t) => t.dueDate.isSmallerOrEqualValue(dueBefore));
    }
    if (dueAfter != null) {
      query.where((t) => t.dueDate.isBiggerOrEqualValue(dueAfter));
    }

    query.orderBy([
      (t) => OrderingTerm(expression: t.priority),
      (t) => OrderingTerm(expression: t.dueDate),
      (t) => OrderingTerm(expression: t.createdAt, mode: OrderingMode.desc),
    ]);

    return query.get();
  }

  /// Get tasks due today
  Future<List<Task>> getTodayTasks() async {
    final now = DateTime.now();
    final todayStart = DateTime(now.year, now.month, now.day);
    final todayEnd = todayStart.add(const Duration(days: 1));

    return (select(tasks)
          ..where((t) =>
              t.dueDate.isBiggerOrEqualValue(todayStart) &
              t.dueDate.isSmallerThanValue(todayEnd) &
              t.status.equals('pending').not() |
              t.status.equals('in_progress')))
        .get();
  }

  /// Get overdue tasks
  Future<List<Task>> getOverdueTasks() async {
    final now = DateTime.now();
    final todayStart = DateTime(now.year, now.month, now.day);

    return (select(tasks)
          ..where((t) =>
              t.dueDate.isSmallerThanValue(todayStart) &
              (t.status.equals('pending') | t.status.equals('in_progress'))))
        .get();
  }

  /// Get task by ID
  Future<Task?> getTaskById(String id) async {
    return (select(tasks)..where((t) => t.id.equals(id))).getSingleOrNull();
  }

  /// Create a new task
  Future<void> createTask(TasksCompanion task) async {
    await into(tasks).insert(task);
  }

  /// Update a task
  Future<void> updateTask(String id, TasksCompanion task) async {
    await (update(tasks)..where((t) => t.id.equals(id))).write(task);
  }

  /// Delete a task
  Future<void> deleteTask(String id) async {
    await (delete(tasks)..where((t) => t.id.equals(id))).go();
  }

  /// Complete a task
  Future<void> completeTask(String id) async {
    await (update(tasks)..where((t) => t.id.equals(id))).write(
      TasksCompanion(
        status: const Value('completed'),
        completedAt: Value(DateTime.now()),
        updatedAt: Value(DateTime.now()),
      ),
    );
  }

  /// Complete a task and queue for sync in the same transaction
  /// This ensures atomicity - if the app crashes, both operations either
  /// happen together or not at all
  Future<void> completeTaskWithQueue({
    required String taskId,
    required String provider,
    required String providerTaskId,
  }) async {
    await transaction(() async {
      final now = DateTime.now();
      final nowTimestamp = now.millisecondsSinceEpoch ~/ 1000;

      // 1. Update task status
      await (update(tasks)..where((t) => t.id.equals(taskId))).write(
        TasksCompanion(
          status: const Value('completed'),
          completedAt: Value(now),
          updatedAt: Value(now),
        ),
      );

      // 2. Queue completion for sync (raw SQL since table is managed by Rust)
      // Uses INSERT OR REPLACE to handle retries
      final completionId = 'completion_${taskId}_$nowTimestamp';
      await customStatement(
        '''INSERT OR REPLACE INTO pending_completions
           (id, task_id, provider, provider_task_id, completed, completed_at, created_at, retry_count)
           VALUES (?, ?, ?, ?, 1, ?, ?, 0)''',
        [completionId, taskId, provider, providerTaskId, nowTimestamp, nowTimestamp],
      );
    });
  }

  /// Reopen a task and queue for sync in the same transaction
  Future<void> reopenTaskWithQueue({
    required String taskId,
    required String provider,
    required String providerTaskId,
  }) async {
    await transaction(() async {
      final now = DateTime.now();
      final nowTimestamp = now.millisecondsSinceEpoch ~/ 1000;

      // 1. Update task status
      await (update(tasks)..where((t) => t.id.equals(taskId))).write(
        TasksCompanion(
          status: const Value('pending'),
          completedAt: const Value(null),
          updatedAt: Value(now),
        ),
      );

      // 2. Queue reopen for sync (completed=0 means reopen)
      final completionId = 'completion_${taskId}_$nowTimestamp';
      await customStatement(
        '''INSERT OR REPLACE INTO pending_completions
           (id, task_id, provider, provider_task_id, completed, completed_at, created_at, retry_count)
           VALUES (?, ?, ?, ?, 0, NULL, ?, 0)''',
        [completionId, taskId, provider, providerTaskId, nowTimestamp],
      );
    });
  }

  // ============ PROJECT OPERATIONS ============

  /// Get all projects
  Future<List<Project>> getAllProjects({bool includeArchived = false}) async {
    final query = select(projects);
    if (!includeArchived) {
      query.where((p) => p.isArchived.equals(false));
    }
    query.orderBy([
      (p) => OrderingTerm(
          expression: p.isFavorite, mode: OrderingMode.desc),
      (p) => OrderingTerm(expression: p.sortOrder),
      (p) => OrderingTerm(expression: p.name),
    ]);
    return query.get();
  }

  /// Get project by ID
  Future<Project?> getProjectById(String id) async {
    return (select(projects)..where((p) => p.id.equals(id))).getSingleOrNull();
  }

  /// Create a new project
  Future<void> createProject(ProjectsCompanion project) async {
    await into(projects).insert(project);
  }

  /// Update a project
  Future<void> updateProject(String id, ProjectsCompanion project) async {
    await (update(projects)..where((p) => p.id.equals(id))).write(project);
  }

  /// Delete a project
  Future<void> deleteProject(String id) async {
    await (delete(projects)..where((p) => p.id.equals(id))).go();
  }

  // ============ LABEL OPERATIONS ============

  /// Get all labels
  Future<List<Label>> getAllLabels() async {
    return (select(labels)..orderBy([(l) => OrderingTerm(expression: l.sortOrder)])).get();
  }

  /// Get labels for a task
  Future<List<Label>> getLabelsForTask(String taskId) async {
    final query = select(labels).join([
      innerJoin(taskLabels, taskLabels.labelId.equalsExp(labels.id)),
    ])
      ..where(taskLabels.taskId.equals(taskId));

    final results = await query.get();
    return results.map((row) => row.readTable(labels)).toList();
  }

  /// Add label to task
  Future<void> addLabelToTask(String taskId, String labelId) async {
    await into(taskLabels).insert(
      TaskLabelsCompanion.insert(taskId: taskId, labelId: labelId),
      mode: InsertMode.insertOrIgnore,
    );
  }

  /// Remove label from task
  Future<void> removeLabelFromTask(String taskId, String labelId) async {
    await (delete(taskLabels)
          ..where((tl) => tl.taskId.equals(taskId) & tl.labelId.equals(labelId)))
        .go();
  }

  // ============ STATISTICS ============

  /// Get task statistics
  Future<Map<String, int>> getTaskStatistics() async {
    final allTasks = await select(tasks).get();

    final now = DateTime.now();
    final todayStart = DateTime(now.year, now.month, now.day);

    int total = allTasks.length;
    int pending = 0;
    int inProgress = 0;
    int completed = 0;
    int overdue = 0;

    for (final task in allTasks) {
      switch (task.status) {
        case 'pending':
          pending++;
          if (task.dueDate != null && task.dueDate!.isBefore(todayStart)) {
            overdue++;
          }
          break;
        case 'in_progress':
          inProgress++;
          if (task.dueDate != null && task.dueDate!.isBefore(todayStart)) {
            overdue++;
          }
          break;
        case 'completed':
          completed++;
          break;
      }
    }

    return {
      'total': total,
      'pending': pending,
      'inProgress': inProgress,
      'completed': completed,
      'overdue': overdue,
      'active': pending + inProgress,
    };
  }

  // ============ SEARCH ============

  /// Search tasks by title, description, or notes
  Future<List<Task>> searchTasks(String query) async {
    if (query.length < 2) return [];

    final searchPattern = '%$query%';
    return (select(tasks)
          ..where((t) =>
              t.title.like(searchPattern) |
              t.description.like(searchPattern) |
              t.notes.like(searchPattern)))
        .get();
  }
}

LazyDatabase _openConnection() {
  return LazyDatabase(() async {
    final dbFolder = await getApplicationDocumentsDirectory();
    final file = File(p.join(dbFolder.path, 'openza.db'));
    return NativeDatabase.createInBackground(
      file,
      setup: (db) {
        // Configure SQLite for safe concurrent access with Rust sync engine
        // WAL mode allows concurrent readers during writes
        // busy_timeout waits instead of failing immediately on lock contention
        db.execute('PRAGMA journal_mode = WAL');
        db.execute('PRAGMA busy_timeout = 5000');
        db.execute('PRAGMA synchronous = NORMAL');
        db.execute('PRAGMA foreign_keys = ON');
      },
    );
  });
}
