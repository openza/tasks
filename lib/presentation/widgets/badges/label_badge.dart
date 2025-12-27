import 'package:flutter/material.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/label.dart';

/// Badge showing a label/tag
class LabelBadge extends StatelessWidget {
  final LabelEntity label;
  final bool isSelected;
  final int? count;
  final VoidCallback? onTap;

  const LabelBadge({
    super.key,
    required this.label,
    this.isSelected = false,
    this.count,
    this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;

    // Grayscale colors for minimal aesthetic - bold text like Todoist
    final inactiveBg = isDark ? AppTheme.gray800 : Colors.white;
    final inactiveBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;
    final inactiveText = isDark ? AppTheme.gray200 : AppTheme.gray800;

    // Selected state uses gray instead of label color for minimal aesthetic
    final selectedBg = isDark ? AppTheme.gray100 : AppTheme.gray800;
    final selectedText = isDark ? AppTheme.gray900 : Colors.white;

    return MouseRegion(
      cursor: onTap != null ? SystemMouseCursors.click : SystemMouseCursors.basic,
      child: GestureDetector(
        onTap: onTap,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
          decoration: BoxDecoration(
            color: isSelected ? selectedBg : inactiveBg,
            borderRadius: BorderRadius.circular(16),
            border: isSelected ? null : Border.all(color: inactiveBorder),
          ),
          child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              label.name,
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w500,
                color: isSelected ? selectedText : inactiveText,
              ),
            ),
            if (count != null) ...[
              const SizedBox(width: 6),
              Text(
                count.toString(),
                style: TextStyle(
                  fontSize: 11,
                  fontWeight: FontWeight.w600,
                  color:
                      isSelected
                          ? selectedText.withValues(alpha: 0.8)
                          : inactiveText,
                ),
              ),
            ],
          ],
        ),
      ),
      ),
    );
  }
}

/// Simplified label badge for display in task cards
/// Uses grayscale colors for minimal, professional look
class LabelChip extends StatelessWidget {
  final String name;
  final String color;

  const LabelChip({super.key, required this.name, required this.color});

  @override
  Widget build(BuildContext context) {
    // Use grayscale instead of label color for minimal aesthetic - bold text like Todoist
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final bgColor = isDark ? AppTheme.gray700 : AppTheme.gray100;
    final textColor = isDark ? Colors.white : AppTheme.gray800; // Much darker text
    final borderColor = isDark ? AppTheme.gray600 : AppTheme.gray300;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
      decoration: BoxDecoration(
        color: bgColor,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: borderColor),
      ),
      child: Text(
        name,
        style: TextStyle(
          fontSize: 10,
          fontWeight: FontWeight.w500,
          color: textColor,
        ),
      ),
    );
  }
}
