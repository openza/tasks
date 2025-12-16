import 'package:freezed_annotation/freezed_annotation.dart';

part 'project.freezed.dart';
part 'project.g.dart';

/// Project entity representing a task container/folder
@freezed
sealed class ProjectEntity with _$ProjectEntity {
  const ProjectEntity._(); // Enable custom getters

  const factory ProjectEntity({
    required String id,
    String? externalId,           // Provider's original ID
    required String integrationId, // FK to integrations table
    required String name,
    String? description,
    @Default('#808080') String color,
    String? icon,
    String? parentId,
    @Default(0) int sortOrder,
    @Default(false) bool isFavorite,
    @Default(false) bool isArchived,
    Map<String, dynamic>? providerMetadata, // Provider-specific unmapped data
    required DateTime createdAt,
    DateTime? updatedAt,
    int? taskCount,
  }) = _ProjectEntity;

  bool get isInbox => id == 'proj_inbox' || name.toLowerCase() == 'inbox';

  /// Check if this is a native (Openza Tasks) project
  bool get isNative => integrationId == 'openza_tasks';

  /// Check if this is an external provider project
  bool get isExternal => !isNative;

  factory ProjectEntity.fromJson(Map<String, dynamic> json) =>
      _$ProjectEntityFromJson(json);
}
