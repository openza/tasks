// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'backup.dart';

// **************************************************************************
// JsonSerializableGenerator
// **************************************************************************

_BackupInfo _$BackupInfoFromJson(Map<String, dynamic> json) => _BackupInfo(
  fileName: json['fileName'] as String,
  filePath: json['filePath'] as String,
  createdAt: DateTime.parse(json['createdAt'] as String),
  sizeInBytes: (json['sizeInBytes'] as num).toInt(),
);

Map<String, dynamic> _$BackupInfoToJson(_BackupInfo instance) =>
    <String, dynamic>{
      'fileName': instance.fileName,
      'filePath': instance.filePath,
      'createdAt': instance.createdAt.toIso8601String(),
      'sizeInBytes': instance.sizeInBytes,
    };
