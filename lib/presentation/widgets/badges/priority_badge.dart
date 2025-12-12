import 'package:flutter/material.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/task.dart';

/// Badge showing task priority
class PriorityBadge extends StatelessWidget {
  final int priority;
  final TaskProvider? provider;
  final bool showLabel;

  const PriorityBadge({
    super.key,
    required this.priority,
    this.provider,
    this.showLabel = false,
  });

  @override
  Widget build(BuildContext context) {
    // Only show badge for high/medium priority
    if (priority > 2) return const SizedBox.shrink();

    final color = AppTheme.priorityColors[priority] ?? AppTheme.gray500;
    final label = _getPriorityLabel();
    final icon = _getPriorityIcon();

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(4),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 12, color: color),
          if (showLabel) ...[
            const SizedBox(width: 4),
            Text(
              label,
              style: TextStyle(
                fontSize: 11,
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
    // Use different icons based on provider
    if (provider == TaskProvider.msToDo) {
      return priority == 1 ? LucideIcons.star : LucideIcons.chevronUp;
    }
    // Todoist / default
    return priority == 1 ? LucideIcons.flame : LucideIcons.chevronUp;
  }
}
