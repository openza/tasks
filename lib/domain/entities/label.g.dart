// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'label.dart';

// **************************************************************************
// JsonSerializableGenerator
// **************************************************************************

_LabelEntity _$LabelEntityFromJson(Map<String, dynamic> json) => _LabelEntity(
  id: json['id'] as String,
  externalId: json['externalId'] as String?,
  integrationId: json['integrationId'] as String,
  name: json['name'] as String,
  color: json['color'] as String? ?? '#808080',
  description: json['description'] as String?,
  sortOrder: (json['sortOrder'] as num?)?.toInt() ?? 0,
  isFavorite: json['isFavorite'] as bool? ?? false,
  providerMetadata: json['providerMetadata'] as Map<String, dynamic>?,
  createdAt: DateTime.parse(json['createdAt'] as String),
  taskCount: (json['taskCount'] as num?)?.toInt(),
);

Map<String, dynamic> _$LabelEntityToJson(_LabelEntity instance) =>
    <String, dynamic>{
      'id': instance.id,
      'externalId': instance.externalId,
      'integrationId': instance.integrationId,
      'name': instance.name,
      'color': instance.color,
      'description': instance.description,
      'sortOrder': instance.sortOrder,
      'isFavorite': instance.isFavorite,
      'providerMetadata': instance.providerMetadata,
      'createdAt': instance.createdAt.toIso8601String(),
      'taskCount': instance.taskCount,
    };
