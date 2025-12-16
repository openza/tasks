import 'package:freezed_annotation/freezed_annotation.dart';

part 'label.freezed.dart';
part 'label.g.dart';

/// Label entity for task categorization
@freezed
sealed class LabelEntity with _$LabelEntity {
  const LabelEntity._(); // Enable custom getters

  const factory LabelEntity({
    required String id,
    String? externalId,           // Provider's original ID
    required String integrationId, // FK to integrations table
    required String name,
    @Default('#808080') String color,
    String? description,
    @Default(0) int sortOrder,
    @Default(false) bool isFavorite,
    Map<String, dynamic>? providerMetadata, // Provider-specific unmapped data
    required DateTime createdAt,
    int? taskCount,
  }) = _LabelEntity;

  /// Check if this is a native (Openza Tasks) label
  bool get isNative => integrationId == 'openza_tasks';

  /// Check if this is an external provider label
  bool get isExternal => !isNative;

  factory LabelEntity.fromJson(Map<String, dynamic> json) =>
      _$LabelEntityFromJson(json);
}
