// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'label.dart';

// **************************************************************************
// JsonSerializableGenerator
// **************************************************************************

_LabelEntity _$LabelEntityFromJson(Map<String, dynamic> json) => _LabelEntity(
  id: json['id'] as String,
  name: json['name'] as String,
  color: json['color'] as String? ?? '#808080',
  description: json['description'] as String?,
  sortOrder: (json['sortOrder'] as num?)?.toInt() ?? 0,
  integrations: json['integrations'] as Map<String, dynamic>?,
  createdAt: DateTime.parse(json['createdAt'] as String),
  provider: $enumDecodeNullable(_$TaskProviderEnumMap, json['provider']),
  taskCount: (json['taskCount'] as num?)?.toInt(),
);

Map<String, dynamic> _$LabelEntityToJson(_LabelEntity instance) =>
    <String, dynamic>{
      'id': instance.id,
      'name': instance.name,
      'color': instance.color,
      'description': instance.description,
      'sortOrder': instance.sortOrder,
      'integrations': instance.integrations,
      'createdAt': instance.createdAt.toIso8601String(),
      'provider': _$TaskProviderEnumMap[instance.provider],
      'taskCount': instance.taskCount,
    };

const _$TaskProviderEnumMap = {
  TaskProvider.local: 'local',
  TaskProvider.todoist: 'todoist',
  TaskProvider.msToDo: 'msToDo',
};
