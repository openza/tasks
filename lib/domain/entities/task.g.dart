// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'task.dart';

// **************************************************************************
// JsonSerializableGenerator
// **************************************************************************

_TaskEntity _$TaskEntityFromJson(Map<String, dynamic> json) => _TaskEntity(
  id: json['id'] as String,
  title: json['title'] as String,
  description: json['description'] as String?,
  projectId: json['projectId'] as String?,
  parentId: json['parentId'] as String?,
  priority: (json['priority'] as num?)?.toInt() ?? 2,
  status:
      $enumDecodeNullable(_$TaskStatusEnumMap, json['status']) ??
      TaskStatus.pending,
  dueDate: json['dueDate'] == null
      ? null
      : DateTime.parse(json['dueDate'] as String),
  dueTime: json['dueTime'] as String?,
  estimatedDuration: (json['estimatedDuration'] as num?)?.toInt(),
  actualDuration: (json['actualDuration'] as num?)?.toInt(),
  energyLevel: (json['energyLevel'] as num?)?.toInt() ?? 2,
  context:
      $enumDecodeNullable(_$TaskContextEnumMap, json['context']) ??
      TaskContext.work,
  focusTime: json['focusTime'] as bool? ?? false,
  notes: json['notes'] as String?,
  sourceTask: json['sourceTask'] as Map<String, dynamic>?,
  integrations: json['integrations'] as Map<String, dynamic>?,
  createdAt: DateTime.parse(json['createdAt'] as String),
  updatedAt: json['updatedAt'] == null
      ? null
      : DateTime.parse(json['updatedAt'] as String),
  completedAt: json['completedAt'] == null
      ? null
      : DateTime.parse(json['completedAt'] as String),
  labels:
      (json['labels'] as List<dynamic>?)
          ?.map((e) => LabelEntity.fromJson(e as Map<String, dynamic>))
          .toList() ??
      const [],
  project: json['project'] == null
      ? null
      : ProjectEntity.fromJson(json['project'] as Map<String, dynamic>),
  provider: $enumDecodeNullable(_$TaskProviderEnumMap, json['provider']),
);

Map<String, dynamic> _$TaskEntityToJson(_TaskEntity instance) =>
    <String, dynamic>{
      'id': instance.id,
      'title': instance.title,
      'description': instance.description,
      'projectId': instance.projectId,
      'parentId': instance.parentId,
      'priority': instance.priority,
      'status': _$TaskStatusEnumMap[instance.status]!,
      'dueDate': instance.dueDate?.toIso8601String(),
      'dueTime': instance.dueTime,
      'estimatedDuration': instance.estimatedDuration,
      'actualDuration': instance.actualDuration,
      'energyLevel': instance.energyLevel,
      'context': _$TaskContextEnumMap[instance.context]!,
      'focusTime': instance.focusTime,
      'notes': instance.notes,
      'sourceTask': instance.sourceTask,
      'integrations': instance.integrations,
      'createdAt': instance.createdAt.toIso8601String(),
      'updatedAt': instance.updatedAt?.toIso8601String(),
      'completedAt': instance.completedAt?.toIso8601String(),
      'labels': instance.labels,
      'project': instance.project,
      'provider': _$TaskProviderEnumMap[instance.provider],
    };

const _$TaskStatusEnumMap = {
  TaskStatus.pending: 'pending',
  TaskStatus.inProgress: 'inProgress',
  TaskStatus.completed: 'completed',
  TaskStatus.cancelled: 'cancelled',
};

const _$TaskContextEnumMap = {
  TaskContext.work: 'work',
  TaskContext.personal: 'personal',
  TaskContext.errands: 'errands',
  TaskContext.home: 'home',
  TaskContext.office: 'office',
  TaskContext.anywhere: 'anywhere',
  TaskContext.phone: 'phone',
  TaskContext.computer: 'computer',
  TaskContext.waiting: 'waiting',
};

const _$TaskProviderEnumMap = {
  TaskProvider.local: 'local',
  TaskProvider.todoist: 'todoist',
  TaskProvider.msToDo: 'msToDo',
};
