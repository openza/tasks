import 'package:flutter/material.dart';

import '../../../app/app_theme.dart';

/// Size variants for the provider badge
enum ProviderBadgeSize {
  /// Small dot only (8px)
  small,

  /// Medium with optional label (10px dot)
  medium,
}

/// Provider colors for different integrations
class ProviderColors {
  static const Color todoist = Color(0xFFE44332);
  static const Color msToDo = Color(0xFF00A4EF);
  static const Color obsidian = Color(0xFF7C3AED);
  static const Color local = AppTheme.gray500;

  /// Get color for an integration ID
  static Color forIntegration(String integrationId) {
    switch (integrationId) {
      case 'todoist':
        return todoist;
      case 'msToDo':
        return msToDo;
      case 'obsidian':
        return obsidian;
      case 'openza_tasks':
      default:
        return local;
    }
  }
}

/// A small colored badge indicating the source provider (Todoist, MS To-Do, Local)
class ProviderBadge extends StatelessWidget {
  /// The integration ID to show badge for
  final String integrationId;

  /// Whether to show a text label alongside the dot
  final bool showLabel;

  /// Size of the badge
  final ProviderBadgeSize size;

  const ProviderBadge({
    super.key,
    required this.integrationId,
    this.showLabel = false,
    this.size = ProviderBadgeSize.small,
  });

  String get _label {
    switch (integrationId) {
      case 'todoist':
        return 'Todoist';
      case 'msToDo':
        return 'MS To-Do';
      case 'obsidian':
        return 'Obsidian';
      case 'openza_tasks':
        return 'Local';
      default:
        return integrationId;
    }
  }

  double get _dotSize {
    switch (size) {
      case ProviderBadgeSize.small:
        return 8;
      case ProviderBadgeSize.medium:
        return 10;
    }
  }

  @override
  Widget build(BuildContext context) {
    final color = ProviderColors.forIntegration(integrationId);

    if (!showLabel) {
      return Container(
        width: _dotSize,
        height: _dotSize,
        decoration: BoxDecoration(
          color: color,
          shape: BoxShape.circle,
        ),
      );
    }

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Container(
          width: _dotSize,
          height: _dotSize,
          decoration: BoxDecoration(
            color: color,
            shape: BoxShape.circle,
          ),
        ),
        const SizedBox(width: 6),
        Text(
          _label,
          style: TextStyle(
            fontSize: size == ProviderBadgeSize.small ? 11 : 12,
            fontWeight: FontWeight.w500,
            color: AppTheme.gray600,
          ),
        ),
      ],
    );
  }
}
