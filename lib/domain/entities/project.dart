import 'package:freezed_annotation/freezed_annotation.dart';

import 'task.dart';

part 'project.freezed.dart';
part 'project.g.dart';

/// Project entity representing a task container/folder
@freezed
sealed class ProjectEntity with _$ProjectEntity {
  const ProjectEntity._(); // Enable custom getters

  const factory ProjectEntity({
    required String id,
    required String name,
    String? description,
    @Default('#808080') String color,
    String? icon,
    String? parentId,
    @Default(0) int sortOrder,
    @Default(false) bool isFavorite,
    @Default(false) bool isArchived,
    Map<String, dynamic>? integrations,
    required DateTime createdAt,
    DateTime? updatedAt,
    TaskProvider? provider,
    int? taskCount,
  }) = _ProjectEntity;

  bool get isInbox => id == 'proj_inbox' || name.toLowerCase() == 'inbox';

  factory ProjectEntity.fromJson(Map<String, dynamic> json) =>
      _$ProjectEntityFromJson(json);
}
