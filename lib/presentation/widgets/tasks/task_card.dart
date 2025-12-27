import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../core/utils/date_utils.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import '../../providers/integration_provider.dart';
import '../badges/priority_badge.dart';
import '../badges/label_badge.dart';

/// Card widget for displaying a single task
class TaskCard extends ConsumerStatefulWidget {
  final TaskEntity task;
  final ProjectEntity? project;
  final VoidCallback? onTap;
  final VoidCallback? onComplete;
  final bool showProject;
  final bool isSelected;

  const TaskCard({
    super.key,
    required this.task,
    this.project,
    this.onTap,
    this.onComplete,
    this.showProject = true,
    this.isSelected = false,
  });

  @override
  ConsumerState<TaskCard> createState() => _TaskCardState();
}

class _TaskCardState extends ConsumerState<TaskCard> {
  bool _isHovered = false;

  @override
  Widget build(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final baseColor = isDark ? AppTheme.gray800 : Colors.white;
    final hoverColor = isDark ? AppTheme.gray700 : AppTheme.gray100;
    final selectedColor =
        isDark
            ? AppTheme.primaryBlue.withValues(alpha: 0.15)
            : AppTheme.primaryBlue.withValues(alpha: 0.08);

    return MouseRegion(
      onEnter: (_) => setState(() => _isHovered = true),
      onExit: (_) => setState(() => _isHovered = false),
      child: AnimatedContainer(
        duration: AppTheme.animationFast,
        margin: const EdgeInsets.only(bottom: 8),
        decoration: BoxDecoration(
          color:
              widget.isSelected
                  ? selectedColor
                  : _isHovered
                  ? hoverColor
                  : baseColor,
          borderRadius: BorderRadius.circular(12),
          border: Border(
            left: BorderSide(
              color:
                  widget.isSelected ? AppTheme.primaryBlue : Colors.transparent,
              width: 4,
            ),
          ),
          boxShadow: [
            BoxShadow(
              color: Colors.black.withValues(alpha: isDark ? 0.2 : 0.04),
              blurRadius: 4,
              offset: const Offset(0, 1),
            ),
          ],
        ),
        child: GestureDetector(
          onTap: widget.onTap,
          behavior: HitTestBehavior.opaque,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
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
                      // Title - use pure black in light mode for maximum readability
                        Text(
                          widget.task.title,
                          style: TextStyle(
                            fontSize: 14,
                            fontWeight: FontWeight.w500,
                            color:
                                widget.task.isCompleted
                                    ? AppTheme.gray400
                                    : isDark
                                    ? Colors.white
                                    : Colors.black,
                            decoration:
                                widget.task.isCompleted
                                    ? TextDecoration.lineThrough
                                    : null,
                          ),
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                        ),

                        // Description - darker text for better readability
                        if (widget.task.description != null &&
                            widget.task.description!.isNotEmpty) ...[
                          const SizedBox(height: 4),
                          Text(
                            widget.task.description!,
                            style: TextStyle(
                              fontSize: 12,
                              color: isDark ? AppTheme.gray200 : AppTheme.gray700,
                            ),
                            maxLines: 2,
                            overflow: TextOverflow.ellipsis,
                          ),
                        ],

                      // Metadata row
                      const SizedBox(height: 10),
                      _buildMetadataRow(context),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildCheckbox() {
    return GestureDetector(
      onTap: widget.onComplete,
      child: Container(
        width: 20,
        height: 20,
        decoration: BoxDecoration(
          shape: BoxShape.circle,
          border: Border.all(
            color:
                widget.task.isCompleted
                    ? AppTheme.accentPink
                    : AppTheme.gray400,
            width: 2,
          ),
          color:
              widget.task.isCompleted
                  ? AppTheme.accentPink
                  : Colors.transparent,
        ),
        child:
            widget.task.isCompleted
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
        ...widget.task.labels
            .take(2)
            .map((label) => LabelChip(name: label.name, color: label.color)),
        if (widget.task.labels.length > 2)
          Builder(builder: (context) {
            final isDark = Theme.of(context).brightness == Brightness.dark;
            return Text(
              '+${widget.task.labels.length - 2}',
              style: TextStyle(fontSize: 10, color: isDark ? AppTheme.gray300 : AppTheme.gray600),
            );
          }),

        // Priority
        PriorityBadge(priority: widget.task.priority),

        // Project
        if (widget.showProject && widget.project != null)
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
            decoration: BoxDecoration(
              color: _parseColor(widget.project!.color).withValues(alpha: 0.1),
              borderRadius: BorderRadius.circular(4),
            ),
            child: Text(
              widget.project!.name,
              style: TextStyle(
                fontSize: 10,
                color: _parseColor(widget.project!.color),
                fontWeight: FontWeight.w500,
              ),
            ),
          ),

        // Due date
        if (widget.task.dueDate != null) _buildDueDate(),

        // Integration indicator
        _buildIntegrationIndicator(),
      ],
    );
  }

  Widget _buildDueDate() {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    String text;

    // Only use muted red for overdue tasks, gray for everything else (minimal colors)
    final bool isOverdue = widget.task.isOverdue && !widget.task.isCompleted;
    // Softer, less alarming muted red instead of bright errorRed
    const overdueColor = Color(0xFFB85C5C);
    final normalColor = isDark ? AppTheme.gray200 : AppTheme.gray700;
    final Color color = isOverdue ? overdueColor : normalColor;

    if (isOverdue) {
      final days = AppDateUtils.getDaysOverdue(widget.task.dueDate);
      text = days == 1 ? '1 day ago' : '$days days ago';
    } else if (widget.task.isDueToday) {
      text = 'Today';
    } else if (AppDateUtils.isTomorrow(widget.task.dueDate)) {
      text = 'Tomorrow';
    } else {
      text = AppDateUtils.formatForDisplay(widget.task.dueDate);
    }

    // Simpler display - just text with optional dot indicator for overdue
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        if (isOverdue) ...[
          Container(
            width: 6,
            height: 6,
            decoration: BoxDecoration(
              color: overdueColor,
              shape: BoxShape.circle,
            ),
          ),
          const SizedBox(width: 4),
        ],
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
    );
  }

  Widget _buildIntegrationIndicator() {
    // Don't show indicator for native tasks
    if (widget.task.isNative) {
      return const SizedBox.shrink();
    }

    final integration = ref.watch(
      integrationByIdProvider(widget.task.integrationId),
    );
    if (integration == null) {
      return const SizedBox.shrink();
    }

    final isDark = Theme.of(context).brightness == Brightness.dark;
    final color = integration.colorValue;
    final label = integration.displayName;
    final textColor = isDark ? AppTheme.gray200 : AppTheme.gray700;

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Container(
          width: 6,
          height: 6,
          decoration: BoxDecoration(color: color, shape: BoxShape.circle),
        ),
        const SizedBox(width: 3),
        Text(label, style: TextStyle(fontSize: 9, color: textColor)),
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
