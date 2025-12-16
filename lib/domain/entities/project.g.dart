// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'project.dart';

// **************************************************************************
// JsonSerializableGenerator
// **************************************************************************

_ProjectEntity _$ProjectEntityFromJson(Map<String, dynamic> json) =>
    _ProjectEntity(
      id: json['id'] as String,
      externalId: json['externalId'] as String?,
      integrationId: json['integrationId'] as String,
      name: json['name'] as String,
      description: json['description'] as String?,
      color: json['color'] as String? ?? '#808080',
      icon: json['icon'] as String?,
      parentId: json['parentId'] as String?,
      sortOrder: (json['sortOrder'] as num?)?.toInt() ?? 0,
      isFavorite: json['isFavorite'] as bool? ?? false,
      isArchived: json['isArchived'] as bool? ?? false,
      providerMetadata: json['providerMetadata'] as Map<String, dynamic>?,
      createdAt: DateTime.parse(json['createdAt'] as String),
      updatedAt:
          json['updatedAt'] == null
              ? null
              : DateTime.parse(json['updatedAt'] as String),
      taskCount: (json['taskCount'] as num?)?.toInt(),
    );

Map<String, dynamic> _$ProjectEntityToJson(_ProjectEntity instance) =>
    <String, dynamic>{
      'id': instance.id,
      'externalId': instance.externalId,
      'integrationId': instance.integrationId,
      'name': instance.name,
      'description': instance.description,
      'color': instance.color,
      'icon': instance.icon,
      'parentId': instance.parentId,
      'sortOrder': instance.sortOrder,
      'isFavorite': instance.isFavorite,
      'isArchived': instance.isArchived,
      'providerMetadata': instance.providerMetadata,
      'createdAt': instance.createdAt.toIso8601String(),
      'updatedAt': instance.updatedAt?.toIso8601String(),
      'taskCount': instance.taskCount,
    };
