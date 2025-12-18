import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/task.dart';
import '../../providers/task_provider.dart';
import '../../providers/repository_provider.dart';
import '../../widgets/tasks/task_list.dart';
import '../../widgets/tasks/task_detail.dart';

class CompletedScreen extends ConsumerStatefulWidget {
  const CompletedScreen({super.key});

  @override
  ConsumerState<CompletedScreen> createState() => _CompletedScreenState();
}

class _CompletedScreenState extends ConsumerState<CompletedScreen> {
  TaskEntity? _selectedTask;

  @override
  Widget build(BuildContext context) {
    final completedTasksAsync = ref.watch(completedTasksProvider);
    final unifiedDataAsync = ref.watch(unifiedDataProvider);

    return Container(
      color: AppTheme.gray50,
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // Header
                Container(
                  padding: const EdgeInsets.all(24),
                  decoration: BoxDecoration(
                    color: Theme.of(context).colorScheme.surface,
                    border: Border(
                      bottom: BorderSide(color: Theme.of(context).dividerColor),
                    ),
                  ),
                  child: Row(
                    children: [
                      Container(
                        padding: const EdgeInsets.all(8),
                        decoration: BoxDecoration(
                          color: AppTheme.successGreen.withValues(alpha: 0.1),
                          borderRadius: BorderRadius.circular(8),
                        ),
                        child: Icon(
                          LucideIcons.checkCircle2,
                          size: 20,
                          color: AppTheme.successGreen,
                        ),
                      ),
                      const SizedBox(width: 12),
                      Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            'Completed',
                            style: Theme.of(context).textTheme.headlineSmall,
                          ),
                          completedTasksAsync.when(
                            skipLoadingOnRefresh: true,
                            data: (tasks) => Text(
                              '${tasks.length} completed task${tasks.length != 1 ? 's' : ''}',
                              style: Theme.of(context).textTheme.bodySmall
                                  ?.copyWith(color: AppTheme.successGreen),
                            ),
                            loading: () => Text(
                              'Loading...',
                              style: Theme.of(context).textTheme.bodySmall
                                  ?.copyWith(color: AppTheme.gray500),
                            ),
                            error: (_, __) => const SizedBox.shrink(),
                          ),
                        ],
                      ),
                    ],
                  ),
                ),

                // Task List
                Expanded(
                  child: completedTasksAsync.when(
                    skipLoadingOnRefresh: true,
                    data: (tasks) => unifiedDataAsync.when(
                      skipLoadingOnRefresh: true,
                      data: (data) => TaskListWidget(
                        tasks: tasks,
                        projects: data.projects,
                        filter: TaskFilter.project, // Use project filter to show all passed tasks
                        emptyMessage: 'No completed tasks yet',
                        onTaskTap: (task) => setState(() => _selectedTask = task),
                        onTaskComplete: _reopenTask,
                      ),
                      loading: () => const Center(
                        child: CircularProgressIndicator(),
                      ),
                      error: (e, _) => Center(child: Text('Error: $e')),
                    ),
                    loading: () => const Center(child: CircularProgressIndicator()),
                    error: (e, _) => Center(child: Text('Error: $e')),
                  ),
                ),
              ],
            ),
          ),

          // Detail panel
          if (_selectedTask != null)
            unifiedDataAsync.when(
              skipLoadingOnRefresh: true,
              data: (data) => TaskDetail(
                task: _selectedTask!,
                project: _getProjectForTask(_selectedTask!, data.projects),
                onClose: () => setState(() => _selectedTask = null),
                onComplete: _reopenTask,
              ),
              loading: () => const SizedBox.shrink(),
              error: (_, __) => const SizedBox.shrink(),
            ),
        ],
      ),
    );
  }

  Future<void> _reopenTask(TaskEntity task) async {
    final repository = await ref.read(taskRepositoryProvider.future);
    await repository.reopenTask(task);
    ref.invalidate(unifiedDataProvider);
    ref.invalidate(completedTasksProvider);
    if (_selectedTask?.id == task.id) {
      setState(() => _selectedTask = null);
    }
  }

  dynamic _getProjectForTask(TaskEntity task, List projects) {
    if (task.projectId == null) return null;
    try {
      return projects.firstWhere(
        (p) => p.id == task.projectId || p.id == 'todoist_${task.projectId}',
      );
    } catch (_) {
      return null;
    }
  }
}
