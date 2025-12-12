import 'package:drift/drift.dart';

/// Projects table - mirrors Electron schema
class Projects extends Table {
  TextColumn get id => text()();
  TextColumn get name => text()();
  TextColumn get description => text().nullable()();
  TextColumn get color => text().withDefault(const Constant('#808080'))();
  TextColumn get icon => text().nullable()();
  TextColumn get parentId => text().nullable().references(Projects, #id)();
  IntColumn get sortOrder => integer().withDefault(const Constant(0))();
  BoolColumn get isFavorite => boolean().withDefault(const Constant(false))();
  BoolColumn get isArchived => boolean().withDefault(const Constant(false))();

  // Integration extensions (JSON)
  TextColumn get integrations => text().nullable()();

  DateTimeColumn get createdAt => dateTime().withDefault(currentDateAndTime)();
  DateTimeColumn get updatedAt => dateTime().withDefault(currentDateAndTime)();

  @override
  Set<Column> get primaryKey => {id};
}

/// Tasks table - with wrapper pattern for external integrations
class Tasks extends Table {
  TextColumn get id => text()();
  TextColumn get title => text()();
  TextColumn get description => text().nullable()();
  TextColumn get projectId => text().nullable().references(Projects, #id)();
  TextColumn get parentId => text().nullable().references(Tasks, #id)();
  IntColumn get priority => integer().withDefault(const Constant(2))();
  TextColumn get status => text().withDefault(const Constant('pending'))();
  DateTimeColumn get dueDate => dateTime().nullable()();
  TextColumn get dueTime => text().nullable()();

  // Enhanced local features
  IntColumn get estimatedDuration => integer().nullable()();
  IntColumn get actualDuration => integer().nullable()();
  IntColumn get energyLevel => integer().withDefault(const Constant(2))();
  TextColumn get context => text().withDefault(const Constant('work'))();
  BoolColumn get focusTime => boolean().withDefault(const Constant(false))();
  TextColumn get notes => text().nullable()();

  // External task integration (wrapper pattern)
  TextColumn get sourceTask => text().nullable()(); // JSON: Complete original external task
  TextColumn get integrations => text().nullable()(); // JSON: Sync configuration

  DateTimeColumn get createdAt => dateTime().withDefault(currentDateAndTime)();
  DateTimeColumn get updatedAt => dateTime().withDefault(currentDateAndTime)();
  DateTimeColumn get completedAt => dateTime().nullable()();

  @override
  Set<Column> get primaryKey => {id};
}

/// Labels table with integration support
class Labels extends Table {
  TextColumn get id => text()();
  TextColumn get name => text().unique()();
  TextColumn get color => text().withDefault(const Constant('#808080'))();
  TextColumn get description => text().nullable()();
  IntColumn get sortOrder => integer().withDefault(const Constant(0))();

  // Integration extensions
  TextColumn get integrations => text().nullable()();

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

/// Integration configuration
class Integrations extends Table {
  TextColumn get id => text()();
  TextColumn get name => text()(); // 'todoist', 'mstodo', etc.
  BoolColumn get isActive => boolean().withDefault(const Constant(false))();
  TextColumn get config => text().nullable()(); // JSON: Service configuration
  DateTimeColumn get lastSyncAt => dateTime().nullable()();
  TextColumn get syncToken => text().nullable()();
  DateTimeColumn get createdAt => dateTime().withDefault(currentDateAndTime)();

  @override
  Set<Column> get primaryKey => {id};
}
