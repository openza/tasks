import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/task.dart';
import '../../providers/task_provider.dart';
import '../../providers/repository_provider.dart';
import '../../widgets/tasks/tasks_with_tabs.dart';

class TasksScreen extends ConsumerWidget {
  final String? projectId;

  const TasksScreen({super.key, this.projectId});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final unifiedDataAsync = ref.watch(unifiedDataProvider);

    return Scaffold(
      backgroundColor: AppTheme.gray50,
      // FAB removed - Add Task button is always visible in NavRail
      body: unifiedDataAsync.when(
        skipLoadingOnRefresh: true,
        data: (data) {
          // Filter tasks by project if projectId is provided
          final tasks = projectId != null
              ? data.tasks.where((t) => t.projectId == projectId).toList()
              : data.tasks;

          return TasksWithTabs(
            tasks: tasks,
            projects: data.projects,
            selectedProjectId: projectId,
            onTaskComplete: (task) => _completeTask(ref, task),
            onTaskUpdate: (task) => _updateTask(ref, task),
            onTaskDelete: (task) => _deleteTask(ref, task),
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
    ref.invalidate(localTasksProvider);
    ref.invalidate(unifiedDataProvider);
  }

  Future<void> _updateTask(WidgetRef ref, TaskEntity task) async {
    final repository = await ref.read(taskRepositoryProvider.future);
    await repository.updateTask(task);
    ref.invalidate(localTasksProvider);
    ref.invalidate(unifiedDataProvider);
  }

  Future<void> _deleteTask(WidgetRef ref, TaskEntity task) async {
    final repository = await ref.read(taskRepositoryProvider.future);
    await repository.deleteTask(task);
    ref.invalidate(localTasksProvider);
    ref.invalidate(unifiedDataProvider);
  }
}
