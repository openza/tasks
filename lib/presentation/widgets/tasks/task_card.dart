import 'package:flutter/material.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../core/utils/date_utils.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import '../badges/priority_badge.dart';
import '../badges/label_badge.dart';

/// Card widget for displaying a single task
class TaskCard extends StatelessWidget {
  final TaskEntity task;
  final ProjectEntity? project;
  final VoidCallback? onTap;
  final VoidCallback? onComplete;
  final bool showProject;

  const TaskCard({
    super.key,
    required this.task,
    this.project,
    this.onTap,
    this.onComplete,
    this.showProject = true,
  });

  @override
  Widget build(BuildContext context) {
    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              // Checkbox
              _buildCheckbox(),
              const SizedBox(width: 12),

              // Content
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    // Title
                    Text(
                      task.title,
                      style: TextStyle(
                        fontSize: 14,
                        fontWeight: FontWeight.w500,
                        color: task.isCompleted
                            ? AppTheme.gray400
                            : AppTheme.gray900,
                        decoration: task.isCompleted
                            ? TextDecoration.lineThrough
                            : null,
                      ),
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                    ),

                    // Description
                    if (task.description != null &&
                        task.description!.isNotEmpty) ...[
                      const SizedBox(height: 4),
                      Text(
                        task.description!,
                        style: TextStyle(
                          fontSize: 12,
                          color: AppTheme.gray500,
                        ),
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ],

                    // Metadata row
                    const SizedBox(height: 8),
                    _buildMetadataRow(context),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildCheckbox() {
    return GestureDetector(
      onTap: onComplete,
      child: Container(
        width: 20,
        height: 20,
        decoration: BoxDecoration(
          shape: BoxShape.circle,
          border: Border.all(
            color: task.isCompleted ? AppTheme.accentPink : AppTheme.gray400,
            width: 2,
          ),
          color: task.isCompleted ? AppTheme.accentPink : Colors.transparent,
        ),
        child: task.isCompleted
            ? const Icon(Icons.check, size: 14, color: Colors.white)
            : null,
      ),
    );
  }

  Widget _buildMetadataRow(BuildContext context) {
    return Wrap(
      spacing: 8,
      runSpacing: 4,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        // Labels (max 2)
        ...task.labels.take(2).map((label) => LabelChip(
              name: label.name,
              color: label.color,
            )),
        if (task.labels.length > 2)
          Text(
            '+${task.labels.length - 2}',
            style: TextStyle(
              fontSize: 10,
              color: AppTheme.gray500,
            ),
          ),

        // Priority
        PriorityBadge(priority: task.priority, provider: task.provider),

        // Project
        if (showProject && project != null)
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
            decoration: BoxDecoration(
              color: _parseColor(project!.color).withValues(alpha: 0.1),
              borderRadius: BorderRadius.circular(4),
            ),
            child: Text(
              project!.name,
              style: TextStyle(
                fontSize: 10,
                color: _parseColor(project!.color),
                fontWeight: FontWeight.w500,
              ),
            ),
          ),

        // Due date
        if (task.dueDate != null) _buildDueDate(),

        // Energy level indicator
        if (task.energyLevel >= 3) _buildEnergyIndicator(),

        // Focus time indicator
        if (task.focusTime)
          const Text('🧠', style: TextStyle(fontSize: 12)),

        // Estimated duration
        if (task.estimatedDuration != null)
          Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(LucideIcons.clock, size: 10, color: AppTheme.gray400),
              const SizedBox(width: 2),
              Text(
                '${task.estimatedDuration}m',
                style: TextStyle(fontSize: 10, color: AppTheme.gray500),
              ),
            ],
          ),

        // Provider indicator
        _buildProviderIndicator(),
      ],
    );
  }

  Widget _buildDueDate() {
    Color color;
    String text;

    if (task.isOverdue) {
      color = AppTheme.errorRed;
      final days = AppDateUtils.getDaysOverdue(task.dueDate);
      text = days == 1 ? '1 day ago' : '$days days ago';
    } else if (task.isDueToday) {
      color = AppTheme.warningOrange;
      text = 'Today';
    } else if (AppDateUtils.isTomorrow(task.dueDate)) {
      color = AppTheme.primaryBlue;
      text = 'Tomorrow';
    } else {
      color = AppTheme.gray500;
      text = AppDateUtils.formatForDisplay(task.dueDate);
    }

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(4),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(LucideIcons.calendar, size: 10, color: color),
          const SizedBox(width: 3),
          Text(
            text,
            style: TextStyle(
              fontSize: 10,
              color: color,
              fontWeight: FontWeight.w500,
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildEnergyIndicator() {
    String emoji;
    switch (task.energyLevel) {
      case 3:
        emoji = '🟠';
        break;
      case 4:
        emoji = '🔴';
        break;
      case 5:
        emoji = '⚡';
        break;
      default:
        return const SizedBox.shrink();
    }
    return Text(emoji, style: const TextStyle(fontSize: 12));
  }

  Widget _buildProviderIndicator() {
    if (task.provider == null || task.provider == TaskProvider.local) {
      return const SizedBox.shrink();
    }

    Color color;
    String label;

    switch (task.provider) {
      case TaskProvider.todoist:
        color = const Color(0xFFE44332);
        label = 'Todoist';
        break;
      case TaskProvider.msToDo:
        color = const Color(0xFF00A4EF);
        label = 'MS To-Do';
        break;
      default:
        return const SizedBox.shrink();
    }

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Container(
          width: 6,
          height: 6,
          decoration: BoxDecoration(
            color: color,
            shape: BoxShape.circle,
          ),
        ),
        const SizedBox(width: 3),
        Text(
          label,
          style: TextStyle(
            fontSize: 9,
            color: AppTheme.gray400,
          ),
        ),
      ],
    );
  }

  Color _parseColor(String colorStr) {
    if (colorStr.startsWith('#')) {
      try {
        return Color(int.parse(colorStr.substring(1), radix: 16) + 0xFF000000);
      } catch (_) {
        return AppTheme.gray500;
      }
    }
    return AppTheme.gray500;
  }
}
