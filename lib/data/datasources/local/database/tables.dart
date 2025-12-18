import 'package:drift/drift.dart';

/// Integration providers configuration - source of truth for all provider metadata
class Integrations extends Table {
  TextColumn get id => text()();                    // 'openza_tasks', 'todoist', 'msToDo'
  TextColumn get name => text()();                  // Same as id (legacy compat)
  TextColumn get displayName => text()();           // 'Openza Tasks', 'Todoist', 'Microsoft To-Do'
  TextColumn get color => text().withDefault(const Constant('#808080'))();
  TextColumn get icon => text().nullable()();       // Lucide icon name
  TextColumn get logoPath => text().nullable()();   // 'assets/logos/todoist.svg'
  BoolColumn get isActive => boolean().withDefault(const Constant(false))();
  BoolColumn get isConfigured => boolean().withDefault(const Constant(false))();
  TextColumn get config => text().nullable()();     // JSON: Service configuration
  DateTimeColumn get lastSyncAt => dateTime().nullable()();
  TextColumn get syncToken => text().nullable()();
  DateTimeColumn get createdAt => dateTime().withDefault(currentDateAndTime)();

  @override
  Set<Column> get primaryKey => {id};
}

/// Projects table - with proper FK to integrations
class Projects extends Table {
  TextColumn get id => text()();                    // UUID only (no prefix)
  TextColumn get externalId => text().nullable()(); // Provider's original ID
  TextColumn get integrationId => text().references(Integrations, #id)(); // FK to integrations
  TextColumn get name => text()();
  TextColumn get description => text().nullable()();
  TextColumn get color => text().withDefault(const Constant('#808080'))();
  TextColumn get icon => text().nullable()();
  TextColumn get parentId => text().nullable()();   // Self-reference for subprojects
  IntColumn get sortOrder => integer().withDefault(const Constant(0))();
  BoolColumn get isFavorite => boolean().withDefault(const Constant(false))();
  BoolColumn get isArchived => boolean().withDefault(const Constant(false))();
  TextColumn get providerMetadata => text().nullable()(); // JSON: Provider-specific unmapped data
  DateTimeColumn get createdAt => dateTime().withDefault(currentDateAndTime)();
  DateTimeColumn get updatedAt => dateTime().nullable()();

  @override
  Set<Column> get primaryKey => {id};
}

/// Tasks table - with proper FK to integrations
class Tasks extends Table {
  TextColumn get id => text()();                    // UUID only (no prefix)
  TextColumn get externalId => text().nullable()(); // Provider's original ID
  TextColumn get integrationId => text().references(Integrations, #id)(); // FK to integrations
  TextColumn get title => text()();
  TextColumn get description => text().nullable()();
  TextColumn get projectId => text().nullable().references(Projects, #id)();
  TextColumn get parentId => text().nullable()();   // Self-reference for subtasks
  IntColumn get priority => integer().withDefault(const Constant(2))();
  TextColumn get status => text().withDefault(const Constant('pending'))();
  DateTimeColumn get dueDate => dateTime().nullable()();
  TextColumn get dueTime => text().nullable()();
  TextColumn get notes => text().nullable()();      // Long description/reference material
  TextColumn get providerMetadata => text().nullable()(); // JSON: Provider-specific unmapped data
  DateTimeColumn get createdAt => dateTime().withDefault(currentDateAndTime)();
  DateTimeColumn get updatedAt => dateTime().nullable()();
  DateTimeColumn get completedAt => dateTime().nullable()();

  @override
  Set<Column> get primaryKey => {id};
}

/// Labels table with proper FK to integrations
class Labels extends Table {
  TextColumn get id => text()();                    // UUID only (no prefix)
  TextColumn get externalId => text().nullable()(); // Provider's original ID
  TextColumn get integrationId => text().references(Integrations, #id)(); // FK to integrations
  TextColumn get name => text()();
  TextColumn get color => text().withDefault(const Constant('#808080'))();
  TextColumn get description => text().nullable()();
  IntColumn get sortOrder => integer().withDefault(const Constant(0))();
  BoolColumn get isFavorite => boolean().withDefault(const Constant(false))();
  TextColumn get providerMetadata => text().nullable()(); // JSON: Provider-specific unmapped data
  DateTimeColumn get createdAt => dateTime().withDefault(currentDateAndTime)();

  @override
  Set<Column> get primaryKey => {id};
}

/// Task-label relationships (many-to-many)
class TaskLabels extends Table {
  TextColumn get taskId => text().references(Tasks, #id, onDelete: KeyAction.cascade)();
  TextColumn get labelId => text().references(Labels, #id, onDelete: KeyAction.cascade)();

  @override
  Set<Column> get primaryKey => {taskId, labelId};
}

/// Time tracking entries
class TimeEntries extends Table {
  TextColumn get id => text()();
  TextColumn get taskId => text().references(Tasks, #id, onDelete: KeyAction.cascade)();
  DateTimeColumn get startTime => dateTime()();
  DateTimeColumn get endTime => dateTime().nullable()();
  IntColumn get duration => integer().nullable()(); // calculated minutes
  TextColumn get description => text().nullable()();
  IntColumn get energyUsed => integer().nullable()(); // 1-5 scale
  IntColumn get focusQuality => integer().nullable()(); // 1-5 scale
  DateTimeColumn get createdAt => dateTime().withDefault(currentDateAndTime)();

  @override
  Set<Column> get primaryKey => {id};
}

/// Task enhancements (notes, checkpoints, resources)
class TaskEnhancements extends Table {
  TextColumn get id => text()();
  TextColumn get taskId => text().references(Tasks, #id, onDelete: KeyAction.cascade)();
  TextColumn get type => text()(); // 'note', 'checkpoint', 'resource'
  TextColumn get content => text()();
  IntColumn get sortOrder => integer().withDefault(const Constant(0))();
  BoolColumn get completed => boolean().withDefault(const Constant(false))();
  DateTimeColumn get createdAt => dateTime().withDefault(currentDateAndTime)();

  @override
  Set<Column> get primaryKey => {id};
}
