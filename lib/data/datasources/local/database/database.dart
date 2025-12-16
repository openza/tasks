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
  int get schemaVersion => 2;

  @override
  MigrationStrategy get migration => MigrationStrategy(
        onCreate: (Migrator m) async {
          await m.createAll();
          await _insertDefaultIntegrations();
          await _insertDefaultData();
        },
        onUpgrade: (Migrator m, int from, int to) async {
          if (from < 2) {
            await _migrateV1toV2(m);
          }
        },
      );

  /// Migrate from schema v1 to v2
  /// This handles:
  /// - Adding new columns to integrations table
  /// - Adding external_id, integration_id, provider_metadata to tasks/projects/labels
  /// - Migrating prefixed IDs to proper FK relationships
  /// - Removing deprecated columns via table recreation
  Future<void> _migrateV1toV2(Migrator m) async {
    // Step 1: Update integrations table with new columns
    await customStatement('ALTER TABLE integrations ADD COLUMN display_name TEXT');
    await customStatement("ALTER TABLE integrations ADD COLUMN color TEXT DEFAULT '#808080'");
    await customStatement('ALTER TABLE integrations ADD COLUMN icon TEXT');
    await customStatement('ALTER TABLE integrations ADD COLUMN logo_path TEXT');
    await customStatement('ALTER TABLE integrations ADD COLUMN is_configured INTEGER DEFAULT 0');

    // Step 2: Insert default integrations
    await customStatement('''
      INSERT OR IGNORE INTO integrations (id, name, display_name, color, icon, is_active, is_configured, created_at)
      VALUES
        ('openza_tasks', 'openza_tasks', 'Openza Tasks', '#6366f1', 'database', 1, 1, strftime('%s', 'now')),
        ('todoist', 'todoist', 'Todoist', '#E44332', 'check-circle', 0, 0, strftime('%s', 'now')),
        ('msToDo', 'msToDo', 'Microsoft To-Do', '#00A4EF', 'layout-grid', 0, 0, strftime('%s', 'now'))
    ''');

    // Update existing todoist/msToDo integrations
    await customStatement('''
      UPDATE integrations SET
        display_name = 'Todoist', color = '#E44332', icon = 'check-circle',
        logo_path = 'assets/logos/todoist.svg', is_configured = 1
      WHERE id = 'todoist' AND display_name IS NULL
    ''');
    await customStatement('''
      UPDATE integrations SET
        display_name = 'Microsoft To-Do', color = '#00A4EF', icon = 'layout-grid',
        logo_path = 'assets/logos/microsoft.svg', is_configured = 1
      WHERE id = 'msToDo' AND display_name IS NULL
    ''');

    // Step 3: Add new columns to tasks
    await customStatement('ALTER TABLE tasks ADD COLUMN external_id TEXT');
    await customStatement("ALTER TABLE tasks ADD COLUMN integration_id TEXT DEFAULT 'openza_tasks'");
    await customStatement('ALTER TABLE tasks ADD COLUMN provider_metadata TEXT');

    // Migrate existing tasks data
    await customStatement('UPDATE tasks SET provider_metadata = integrations WHERE integrations IS NOT NULL');
    await customStatement("UPDATE tasks SET external_id = SUBSTR(id, 9), integration_id = 'todoist' WHERE id LIKE 'todoist_%'");
    await customStatement("UPDATE tasks SET external_id = SUBSTR(id, 8), integration_id = 'msToDo' WHERE id LIKE 'mstodo_%'");

    // Step 4: Add new columns to projects
    await customStatement('ALTER TABLE projects ADD COLUMN external_id TEXT');
    await customStatement("ALTER TABLE projects ADD COLUMN integration_id TEXT DEFAULT 'openza_tasks'");
    await customStatement('ALTER TABLE projects ADD COLUMN provider_metadata TEXT');

    // Migrate existing projects data
    await customStatement('UPDATE projects SET provider_metadata = integrations WHERE integrations IS NOT NULL');
    await customStatement("UPDATE projects SET external_id = SUBSTR(id, 9), integration_id = 'todoist' WHERE id LIKE 'todoist_%'");
    await customStatement("UPDATE projects SET external_id = SUBSTR(id, 8), integration_id = 'msToDo' WHERE id LIKE 'mstodo_%'");

    // Step 5: Add new columns to labels
    await customStatement('ALTER TABLE labels ADD COLUMN external_id TEXT');
    await customStatement("ALTER TABLE labels ADD COLUMN integration_id TEXT DEFAULT 'openza_tasks'");
    await customStatement('ALTER TABLE labels ADD COLUMN is_favorite INTEGER DEFAULT 0');
    await customStatement('ALTER TABLE labels ADD COLUMN provider_metadata TEXT');

    // Migrate existing labels data
    await customStatement('UPDATE labels SET provider_metadata = integrations WHERE integrations IS NOT NULL');
    await customStatement("UPDATE labels SET external_id = SUBSTR(id, 15), integration_id = 'todoist' WHERE id LIKE 'todoist_label_%'");

    // Step 6: Recreate tables to remove deprecated columns
    // Tasks table - remove: estimated_duration, actual_duration, energy_level, context, focus_time, source_task
    await customStatement('''
      CREATE TABLE IF NOT EXISTS tasks_new (
        id TEXT PRIMARY KEY NOT NULL,
        external_id TEXT,
        integration_id TEXT NOT NULL DEFAULT 'openza_tasks',
        title TEXT NOT NULL,
        description TEXT,
        project_id TEXT,
        parent_id TEXT,
        priority INTEGER NOT NULL DEFAULT 2,
        status TEXT NOT NULL DEFAULT 'pending',
        due_date INTEGER,
        due_time TEXT,
        notes TEXT,
        provider_metadata TEXT,
        created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
        updated_at INTEGER,
        completed_at INTEGER
      )
    ''');

    await customStatement('''
      INSERT INTO tasks_new (
        id, external_id, integration_id, title, description, project_id, parent_id,
        priority, status, due_date, due_time, notes, provider_metadata,
        created_at, updated_at, completed_at
      )
      SELECT
        id, external_id, COALESCE(integration_id, 'openza_tasks'), title, description, project_id, parent_id,
        priority, status, due_date, due_time, notes, provider_metadata,
        created_at, updated_at, completed_at
      FROM tasks
    ''');

    await customStatement('DROP TABLE tasks');
    await customStatement('ALTER TABLE tasks_new RENAME TO tasks');

    // Recreate projects table
    await customStatement('''
      CREATE TABLE IF NOT EXISTS projects_new (
        id TEXT PRIMARY KEY NOT NULL,
        external_id TEXT,
        integration_id TEXT NOT NULL DEFAULT 'openza_tasks',
        name TEXT NOT NULL,
        description TEXT,
        color TEXT,
        icon TEXT,
        parent_id TEXT,
        sort_order INTEGER NOT NULL DEFAULT 0,
        is_favorite INTEGER NOT NULL DEFAULT 0,
        is_archived INTEGER NOT NULL DEFAULT 0,
        provider_metadata TEXT,
        created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
        updated_at INTEGER
      )
    ''');

    await customStatement('''
      INSERT INTO projects_new (
        id, external_id, integration_id, name, description, color, icon, parent_id,
        sort_order, is_favorite, is_archived, provider_metadata, created_at, updated_at
      )
      SELECT
        id, external_id, COALESCE(integration_id, 'openza_tasks'), name, description, color, icon, parent_id,
        sort_order, is_favorite, is_archived, provider_metadata, created_at, updated_at
      FROM projects
    ''');

    await customStatement('DROP TABLE projects');
    await customStatement('ALTER TABLE projects_new RENAME TO projects');

    // Recreate labels table
    await customStatement('''
      CREATE TABLE IF NOT EXISTS labels_new (
        id TEXT PRIMARY KEY NOT NULL,
        external_id TEXT,
        integration_id TEXT NOT NULL DEFAULT 'openza_tasks',
        name TEXT NOT NULL,
        color TEXT,
        description TEXT,
        sort_order INTEGER NOT NULL DEFAULT 0,
        is_favorite INTEGER NOT NULL DEFAULT 0,
        provider_metadata TEXT,
        created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now'))
      )
    ''');

    await customStatement('''
      INSERT INTO labels_new (
        id, external_id, integration_id, name, color, description, sort_order, is_favorite,
        provider_metadata, created_at
      )
      SELECT
        id, external_id, COALESCE(integration_id, 'openza_tasks'), name, color, description, sort_order,
        COALESCE(is_favorite, 0), provider_metadata, created_at
      FROM labels
    ''');

    await customStatement('DROP TABLE labels');
    await customStatement('ALTER TABLE labels_new RENAME TO labels');

    // Step 7: Create indexes
    await customStatement('CREATE INDEX IF NOT EXISTS idx_tasks_integration_id ON tasks(integration_id)');
    await customStatement('CREATE INDEX IF NOT EXISTS idx_tasks_project_id ON tasks(project_id)');
    await customStatement('CREATE INDEX IF NOT EXISTS idx_tasks_status ON tasks(status)');
    await customStatement('CREATE INDEX IF NOT EXISTS idx_tasks_due_date ON tasks(due_date)');
    await customStatement('CREATE INDEX IF NOT EXISTS idx_projects_integration_id ON projects(integration_id)');
    await customStatement('CREATE INDEX IF NOT EXISTS idx_labels_integration_id ON labels(integration_id)');
  }

  /// Insert default integration providers
  Future<void> _insertDefaultIntegrations() async {
    await batch((batch) {
      batch.insertAll(integrations, [
        IntegrationsCompanion.insert(
          id: 'openza_tasks',
          name: 'openza_tasks',
          displayName: 'Openza Tasks',
          color: const Value('#6366f1'),
          icon: const Value('database'),
          isActive: const Value(true),
          isConfigured: const Value(true),
        ),
        IntegrationsCompanion.insert(
          id: 'todoist',
          name: 'todoist',
          displayName: 'Todoist',
          color: const Value('#E44332'),
          icon: const Value('check-circle'),
          logoPath: const Value('assets/logos/todoist.svg'),
        ),
        IntegrationsCompanion.insert(
          id: 'msToDo',
          name: 'msToDo',
          displayName: 'Microsoft To-Do',
          color: const Value('#00A4EF'),
          icon: const Value('layout-grid'),
          logoPath: const Value('assets/logos/microsoft.svg'),
        ),
      ], mode: InsertMode.insertOrIgnore);
    });
  }

  /// Insert default projects and labels for native integration
  Future<void> _insertDefaultData() async {
    await batch((batch) {
      // Insert default projects
      batch.insertAll(projects, [
        ProjectsCompanion.insert(
          id: 'proj_inbox',
          integrationId: 'openza_tasks',
          name: 'Inbox',
          description: const Value('Default inbox for new tasks'),
          color: const Value('#808080'),
          icon: const Value('inbox'),
        ),
        ProjectsCompanion.insert(
          id: 'proj_work',
          integrationId: 'openza_tasks',
          name: 'Work',
          description: const Value('Work-related tasks'),
          color: const Value('#3b82f6'),
          icon: const Value('briefcase'),
        ),
        ProjectsCompanion.insert(
          id: 'proj_personal',
          integrationId: 'openza_tasks',
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
          integrationId: 'openza_tasks',
          name: 'urgent',
          color: const Value('#ef4444'),
        ),
        LabelsCompanion.insert(
          id: 'label_important',
          integrationId: 'openza_tasks',
          name: 'important',
          color: const Value('#f59e0b'),
        ),
        LabelsCompanion.insert(
          id: 'label_learning',
          integrationId: 'openza_tasks',
          name: 'learning',
          color: const Value('#3b82f6'),
        ),
        LabelsCompanion.insert(
          id: 'label_review',
          integrationId: 'openza_tasks',
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

  // ============ INTEGRATION OPERATIONS ============

  /// Get all integrations
  Future<List<Integration>> getAllIntegrations() async {
    return select(integrations).get();
  }

  /// Get integration by ID
  Future<Integration?> getIntegrationById(String id) async {
    return (select(integrations)..where((i) => i.id.equals(id))).getSingleOrNull();
  }

  /// Get configured integrations
  Future<List<Integration>> getConfiguredIntegrations() async {
    return (select(integrations)..where((i) => i.isConfigured.equals(true))).get();
  }

  /// Get active integrations
  Future<List<Integration>> getActiveIntegrations() async {
    return (select(integrations)..where((i) => i.isActive.equals(true))).get();
  }

  /// Update integration configured status
  Future<void> setIntegrationConfigured(String id, bool configured) async {
    await (update(integrations)..where((i) => i.id.equals(id))).write(
      IntegrationsCompanion(isConfigured: Value(configured)),
    );
  }

  /// Update integration active status
  Future<void> setIntegrationActive(String id, bool active) async {
    await (update(integrations)..where((i) => i.id.equals(id))).write(
      IntegrationsCompanion(isActive: Value(active)),
    );
  }

  /// Update integration last sync timestamp
  Future<void> updateIntegrationLastSync(String id, DateTime lastSyncAt, {String? syncToken}) async {
    await (update(integrations)..where((i) => i.id.equals(id))).write(
      IntegrationsCompanion(
        lastSyncAt: Value(lastSyncAt),
        syncToken: syncToken != null ? Value(syncToken) : const Value.absent(),
      ),
    );
  }
}

LazyDatabase _openConnection() {
  return LazyDatabase(() async {
    final dbFolder = await getApplicationSupportDirectory();
    final newDbFile = File(p.join(dbFolder.path, 'openza_tasks.db'));

    // Check if we need to migrate from old database location
    if (!await newDbFile.exists()) {
      await _migrateOldDatabaseIfExists(newDbFile);
    }

    return NativeDatabase.createInBackground(
      newDbFile,
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

/// Migrate old database from ~/Documents/openza.db to new location
/// This runs once when upgrading from v1 to v2
Future<void> _migrateOldDatabaseIfExists(File newDbFile) async {
  try {
    // Old database was in Documents folder
    final documentsDir = await getApplicationDocumentsDirectory();
    final oldDbFile = File(p.join(documentsDir.path, 'openza.db'));

    if (await oldDbFile.exists()) {
      // Ensure new directory exists
      await newDbFile.parent.create(recursive: true);

      // Copy old database to new location
      await oldDbFile.copy(newDbFile.path);

      // Also copy WAL and SHM files if they exist (for WAL mode databases)
      final oldWalFile = File('${oldDbFile.path}-wal');
      final oldShmFile = File('${oldDbFile.path}-shm');

      if (await oldWalFile.exists()) {
        await oldWalFile.copy('${newDbFile.path}-wal');
      }
      if (await oldShmFile.exists()) {
        await oldShmFile.copy('${newDbFile.path}-shm');
      }

      // Optionally rename old database to mark it as migrated
      // (don't delete in case user needs to rollback)
      final backupFile = File('${oldDbFile.path}.migrated');
      if (!await backupFile.exists()) {
        await oldDbFile.rename(backupFile.path);
      }
    }
  } catch (e) {
    // If migration fails, continue with fresh database
    // Log error but don't crash the app
    // ignore: avoid_print
    print('Warning: Failed to migrate old database: $e');
  }
}
