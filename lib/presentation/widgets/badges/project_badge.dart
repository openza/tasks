import 'package:flutter/material.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/project.dart';

enum ProjectBadgeVariant { badge, text, full }

/// Badge showing project information
class ProjectBadge extends StatelessWidget {
  final ProjectEntity project;
  final ProjectBadgeVariant variant;
  final VoidCallback? onTap;

  const ProjectBadge({
    super.key,
    required this.project,
    this.variant = ProjectBadgeVariant.badge,
    this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final color = _parseColor(project.color);

    switch (variant) {
      case ProjectBadgeVariant.text:
        return _buildTextVariant(color);
      case ProjectBadgeVariant.full:
        return _buildFullVariant(context, color);
      case ProjectBadgeVariant.badge:
      default:
        return _buildBadgeVariant(color);
    }
  }

  Widget _buildBadgeVariant(Color color) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(4),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Container(
            width: 8,
            height: 8,
            decoration: BoxDecoration(
              color: color,
              borderRadius: BorderRadius.circular(2),
            ),
          ),
          const SizedBox(width: 4),
          Text(
            project.name,
            style: TextStyle(
              fontSize: 11,
              fontWeight: FontWeight.w500,
              color: color,
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildTextVariant(Color color) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Container(
          width: 8,
          height: 8,
          decoration: BoxDecoration(
            color: color,
            borderRadius: BorderRadius.circular(2),
          ),
        ),
        const SizedBox(width: 6),
        Text(
          project.name,
          style: TextStyle(
            fontSize: 12,
            color: AppTheme.gray600,
          ),
        ),
      ],
    );
  }

  Widget _buildFullVariant(BuildContext context, Color color) {
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(8),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.1),
          borderRadius: BorderRadius.circular(8),
          border: Border.all(color: color.withValues(alpha: 0.3)),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(_getProjectIcon(), size: 16, color: color),
            const SizedBox(width: 8),
            Text(
              project.name,
              style: TextStyle(
                fontSize: 13,
                fontWeight: FontWeight.w500,
                color: color,
              ),
            ),
            if (project.isFavorite) ...[
              const SizedBox(width: 4),
              Icon(LucideIcons.star, size: 12, color: color),
            ],
          ],
        ),
      ),
    );
  }

  IconData _getProjectIcon() {
    if (project.isInbox) return LucideIcons.inbox;
    if (project.icon != null) {
      switch (project.icon) {
        case 'inbox':
          return LucideIcons.inbox;
        case 'briefcase':
          return LucideIcons.briefcase;
        case 'user':
          return LucideIcons.user;
        case 'home':
          return LucideIcons.home;
        case 'star':
          return LucideIcons.star;
        case 'heart':
          return LucideIcons.heart;
        case 'book':
          return LucideIcons.book;
        case 'code':
          return LucideIcons.code;
        case 'music':
          return LucideIcons.music;
        case 'camera':
          return LucideIcons.camera;
      }
    }
    return LucideIcons.folder;
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
