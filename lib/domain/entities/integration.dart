import 'package:flutter/material.dart';

/// Integration entity representing a task provider configuration
class IntegrationEntity {
  final String id;
  final String name;
  final String displayName;
  final String color;
  final String? icon;
  final String? logoPath;
  final bool isActive;
  final bool isConfigured;
  final DateTime? lastSyncAt;
  final String? syncToken;
  final DateTime createdAt;

  const IntegrationEntity({
    required this.id,
    required this.name,
    required this.displayName,
    required this.color,
    this.icon,
    this.logoPath,
    this.isActive = false,
    this.isConfigured = false,
    this.lastSyncAt,
    this.syncToken,
    required this.createdAt,
  });

  /// Parse hex color string to Color object
  Color get colorValue => _hexToColor(color);

  /// Check if this is the native Openza Tasks integration
  bool get isNative => id == 'openza_tasks';

  /// Check if this is an external provider
  bool get isExternal => !isNative;

  /// Convert hex string to Color
  static Color _hexToColor(String hex) {
    final buffer = StringBuffer();
    if (hex.length == 6 || hex.length == 7) buffer.write('ff');
    buffer.write(hex.replaceFirst('#', ''));
    return Color(int.parse(buffer.toString(), radix: 16));
  }

  /// Create from database row
  factory IntegrationEntity.fromDb({
    required String id,
    required String name,
    required String displayName,
    required String color,
    String? icon,
    String? logoPath,
    required bool isActive,
    required bool isConfigured,
    DateTime? lastSyncAt,
    String? syncToken,
    required DateTime createdAt,
  }) {
    return IntegrationEntity(
      id: id,
      name: name,
      displayName: displayName,
      color: color,
      icon: icon,
      logoPath: logoPath,
      isActive: isActive,
      isConfigured: isConfigured,
      lastSyncAt: lastSyncAt,
      syncToken: syncToken,
      createdAt: createdAt,
    );
  }

  @override
  String toString() => 'IntegrationEntity(id: $id, displayName: $displayName)';

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is IntegrationEntity &&
          runtimeType == other.runtimeType &&
          id == other.id;

  @override
  int get hashCode => id.hashCode;
}
