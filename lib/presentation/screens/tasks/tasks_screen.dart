import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';
import 'package:toastification/toastification.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import '../../providers/task_provider.dart';
import '../../providers/repository_provider.dart';
import '../../widgets/tasks/tasks_with_tabs.dart';

class TasksScreen extends ConsumerWidget {
  final String? projectId;

  const TasksScreen({super.key, this.projectId});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final unifiedDataAsync = ref.watch(unifiedDataProvider);
    final isDark = Theme.of(context).brightness == Brightness.dark;

    return Scaffold(
      backgroundColor: isDark ? AppTheme.gray900 : Colors.white,
      // FAB removed - Add Task button is always visible in NavRail
      body: unifiedDataAsync.when(
        skipLoadingOnRefresh: true,
        data: (data) {
          // Filter tasks by project if projectId is provided
          List<TaskEntity> tasks;
          if (projectId == null) {
            // No project selected - show all tasks
            tasks = data.tasks;
          } else if (_isVirtualProjectId(projectId!)) {
            // Virtual project (e.g., "todoist_source_12345") - filter by sourceProjectId
            final sourceProjectId = _extractSourceProjectId(projectId!);
            tasks = data.tasks.where((t) => t.sourceProjectId == sourceProjectId).toList();
          } else {
            // Check if viewing Inbox - also include tasks with null projectId
            final selectedProject = data.projects.where((p) => p.id == projectId).firstOrNull;
            if (selectedProject?.isInbox == true) {
              // Inbox shows NATIVE tasks assigned to Inbox OR unassigned native tasks
              // External tasks (Todoist, MS To-Do) are shown in their own virtual project sections
              tasks = data.tasks.where((t) =>
                t.isNative && (t.projectId == projectId || t.projectId == null)
              ).toList();
            } else {
              // Other projects - only show tasks with matching projectId
              tasks = data.tasks.where((t) => t.projectId == projectId).toList();
            }
          }

          return TasksWithTabs(
            tasks: tasks,
            projects: data.projects,
            selectedProjectId: projectId,
            onTaskComplete: (task) => _completeTask(ref, task),
            onTaskUpdate: (task) => _updateTask(ref, task),
            onTaskDelete: (task) => _deleteTask(ref, task),
            onTaskMoveToProject: (task, project) =>
                _moveTaskToProject(context, ref, task, project),
          );
        },
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (error, _) => Center(
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Icon(LucideIcons.alertCircle, size: 48, color: AppTheme.errorRed),
              const SizedBox(height: 16),
              Text(
                'Error loading tasks',
                style: Theme.of(context).textTheme.titleMedium,
              ),
              const SizedBox(height: 8),
              Text(
                error.toString(),
                style: Theme.of(context)
                    .textTheme
                    .bodySmall
                    ?.copyWith(color: AppTheme.gray500),
              ),
              const SizedBox(height: 16),
              FilledButton.icon(
                onPressed: () => ref.invalidate(unifiedDataProvider),
                icon: const Icon(LucideIcons.refreshCw),
                label: const Text('Retry'),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Future<void> _completeTask(WidgetRef ref, TaskEntity task) async {
    final repository = await ref.read(taskRepositoryProvider.future);
    await repository.completeTask(task);
    // Use refresh instead of invalidate for smooth background updates
    ref.refresh(localTasksProvider);
    ref.refresh(unifiedDataProvider);
  }

  Future<void> _updateTask(WidgetRef ref, TaskEntity task) async {
    final repository = await ref.read(taskRepositoryProvider.future);
    await repository.updateTask(task);
    // Use refresh instead of invalidate for smooth background updates
    ref.refresh(localTasksProvider);
    ref.refresh(localLabelsProvider);
    ref.refresh(unifiedDataProvider);
  }

  Future<void> _deleteTask(WidgetRef ref, TaskEntity task) async {
    final repository = await ref.read(taskRepositoryProvider.future);
    await repository.deleteTask(task);
    // Use refresh instead of invalidate for smooth background updates
    ref.refresh(localTasksProvider);
    ref.refresh(unifiedDataProvider);
  }

  Future<void> _moveTaskToProject(
    BuildContext context,
    WidgetRef ref,
    TaskEntity task,
    ProjectEntity? targetProject,
  ) async {
    try {
      final repository = await ref.read(taskRepositoryProvider.future);

      // Update task with new project ID
      final updatedTask = task.copyWith(
        projectId: targetProject?.id,
      );

      await repository.updateTask(updatedTask);

      // Refresh providers
      ref.invalidate(localTasksProvider);
      ref.invalidate(unifiedDataProvider);

      // Show success toast
      if (context.mounted) {
        final projectName = targetProject?.name ?? 'Inbox';
        toastification.show(
          context: context,
          type: ToastificationType.success,
          title: const Text('Task Moved'),
          description: Text('Moved to $projectName'),
          autoCloseDuration: const Duration(seconds: 2),
        );
      }
    } catch (e) {
      if (context.mounted) {
        toastification.show(
          context: context,
          type: ToastificationType.error,
          title: const Text('Error'),
          description: const Text('Failed to move task'),
          autoCloseDuration: const Duration(seconds: 3),
        );
      }
    }
  }

  /// Check if projectId is a virtual project ID (e.g., "todoist_source_12345")
  bool _isVirtualProjectId(String projectId) {
    return projectId.contains('_source_');
  }

  /// Extract the source project ID from a virtual project ID
  /// e.g., "todoist_source_12345" -> "12345"
  String _extractSourceProjectId(String virtualProjectId) {
    final parts = virtualProjectId.split('_source_');
    return parts.length > 1 ? parts[1] : virtualProjectId;
  }
}
