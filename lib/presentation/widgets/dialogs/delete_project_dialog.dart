import 'package:flutter/material.dart';
import 'package:lucide_icons/lucide_icons.dart';
import 'package:toastification/toastification.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/project.dart';

/// Result of the delete project dialog
enum DeleteProjectAction {
  /// User cancelled the dialog
  cancel,

  /// Move tasks to Inbox, then delete project
  moveTasksToInbox,

  /// Delete tasks along with the project
  deleteTasks,
}

/// Dialog for confirming project deletion
class DeleteProjectDialog extends StatelessWidget {
  final ProjectEntity project;
  final int taskCount;

  const DeleteProjectDialog({
    super.key,
    required this.project,
    required this.taskCount,
  });

  /// Show delete confirmation dialog
  /// Returns the action to take, or null if cancelled
  static Future<DeleteProjectAction?> show(
    BuildContext context, {
    required ProjectEntity project,
    required int taskCount,
  }) async {
    // Prevent deleting Inbox
    if (project.isInbox) {
      toastification.show(
        context: context,
        type: ToastificationType.warning,
        title: const Text('Cannot Delete Inbox'),
        description: const Text('The Inbox project cannot be deleted'),
        autoCloseDuration: const Duration(seconds: 3),
      );
      return null;
    }

    return showDialog<DeleteProjectAction>(
      context: context,
      builder: (context) => DeleteProjectDialog(
        project: project,
        taskCount: taskCount,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final hasTasks = taskCount > 0;

    return Dialog(
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      child: Container(
        width: 400,
        constraints: const BoxConstraints(maxHeight: 450),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            _buildHeader(context),
            Padding(
              padding: const EdgeInsets.all(20),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  _buildProjectInfo(context),
                  if (hasTasks) ...[
                    const SizedBox(height: 16),
                    _buildTaskWarning(context),
                  ],
                ],
              ),
            ),
            _buildFooter(context, hasTasks),
          ],
        ),
      ),
    );
  }

  Widget _buildHeader(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        border: Border(bottom: BorderSide(color: AppTheme.gray200)),
      ),
      child: Row(
        children: [
          Container(
            padding: const EdgeInsets.all(8),
            decoration: BoxDecoration(
              color: Colors.red.withValues(alpha: 0.1),
              borderRadius: BorderRadius.circular(8),
            ),
            child: const Icon(
              LucideIcons.trash2,
              size: 20,
              color: Colors.red,
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Delete Project',
                  style: Theme.of(context).textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                ),
                Text(
                  'This action cannot be undone',
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: AppTheme.gray500,
                      ),
                ),
              ],
            ),
          ),
          IconButton(
            icon: Icon(LucideIcons.x, color: AppTheme.gray400),
            onPressed: () => Navigator.of(context).pop(DeleteProjectAction.cancel),
          ),
        ],
      ),
    );
  }

  Widget _buildProjectInfo(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: AppTheme.gray50,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: AppTheme.gray200),
      ),
      child: Row(
        children: [
          Container(
            width: 16,
            height: 16,
            decoration: BoxDecoration(
              color: _parseColor(project.color),
              borderRadius: BorderRadius.circular(4),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              project.name,
              style: const TextStyle(
                fontSize: 14,
                fontWeight: FontWeight.w500,
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildTaskWarning(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: Colors.amber.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: Colors.amber.withValues(alpha: 0.3)),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Icon(
            LucideIcons.alertTriangle,
            size: 18,
            color: Colors.amber,
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'This project contains $taskCount task${taskCount == 1 ? '' : 's'}',
                  style: const TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w500,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  'Choose what to do with these tasks:',
                  style: TextStyle(
                    fontSize: 12,
                    color: AppTheme.gray600,
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildFooter(BuildContext context, bool hasTasks) {
    if (!hasTasks) {
      // Simple delete confirmation
      return Container(
        padding: const EdgeInsets.all(20),
        decoration: BoxDecoration(
          border: Border(top: BorderSide(color: AppTheme.gray200)),
        ),
        child: Row(
          children: [
            Expanded(
              child: OutlinedButton(
                onPressed: () => Navigator.of(context).pop(DeleteProjectAction.cancel),
                child: const Text('Cancel'),
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              flex: 2,
              child: FilledButton.icon(
                onPressed: () => Navigator.of(context).pop(DeleteProjectAction.deleteTasks),
                style: FilledButton.styleFrom(
                  backgroundColor: Colors.red,
                  foregroundColor: Colors.white,
                ),
                icon: const Icon(LucideIcons.trash2, size: 18),
                label: const Text('Delete Project'),
              ),
            ),
          ],
        ),
      );
    }

    // Delete with task options
    return Container(
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        border: Border(top: BorderSide(color: AppTheme.gray200)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          OutlinedButton.icon(
            onPressed: () => Navigator.of(context).pop(DeleteProjectAction.moveTasksToInbox),
            icon: const Icon(LucideIcons.inbox, size: 18),
            label: const Text('Move Tasks to Inbox & Delete'),
          ),
          const SizedBox(height: 8),
          FilledButton.icon(
            onPressed: () => Navigator.of(context).pop(DeleteProjectAction.deleteTasks),
            style: FilledButton.styleFrom(
              backgroundColor: Colors.red,
              foregroundColor: Colors.white,
            ),
            icon: const Icon(LucideIcons.trash2, size: 18),
            label: const Text('Delete Project & Tasks'),
          ),
          const SizedBox(height: 8),
          TextButton(
            onPressed: () => Navigator.of(context).pop(DeleteProjectAction.cancel),
            child: const Text('Cancel'),
          ),
        ],
      ),
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
