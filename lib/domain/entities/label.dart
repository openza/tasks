import 'package:freezed_annotation/freezed_annotation.dart';

import 'task.dart';

part 'label.freezed.dart';
part 'label.g.dart';

/// Label entity for task categorization
@freezed
sealed class LabelEntity with _$LabelEntity {
  const factory LabelEntity({
    required String id,
    required String name,
    @Default('#808080') String color,
    String? description,
    @Default(0) int sortOrder,
    Map<String, dynamic>? integrations,
    required DateTime createdAt,
    TaskProvider? provider,
    int? taskCount,
  }) = _LabelEntity;

  factory LabelEntity.fromJson(Map<String, dynamic> json) =>
      _$LabelEntityFromJson(json);
}
