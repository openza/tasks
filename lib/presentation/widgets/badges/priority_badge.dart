import 'package:flutter/material.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';

/// Badge showing task priority
class PriorityBadge extends StatelessWidget {
  final int priority;
  final String? integrationId;
  final bool showLabel;
  /// If true, shows badge even for Normal/Low priority (useful in detail views)
  final bool showAlways;

  const PriorityBadge({
    super.key,
    required this.priority,
    this.integrationId,
    this.showLabel = false,
    this.showAlways = false,
  });

  @override
  Widget build(BuildContext context) {
    // Only show badge for high/medium priority in lists, unless showAlways
    if (!showAlways && priority > 2) return const SizedBox.shrink();

    final color = AppTheme.priorityColors[priority] ?? AppTheme.gray500;
    final label = _getPriorityLabel();
    final icon = _getPriorityIcon();

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(6),
        border: Border.all(color: color.withValues(alpha: 0.2)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 14, color: color),
          if (showLabel) ...[
            const SizedBox(width: 6),
            Text(
              label,
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w500,
                color: color,
              ),
            ),
          ],
        ],
      ),
    );
  }

  String _getPriorityLabel() {
    switch (priority) {
      case 1:
        return 'High';
      case 2:
        return 'Medium';
      case 3:
        return 'Normal';
      case 4:
        return 'Low';
      default:
        return '';
    }
  }

  IconData _getPriorityIcon() {
    // Use consistent flag icon for all priorities
    return LucideIcons.flag;
  }
}
