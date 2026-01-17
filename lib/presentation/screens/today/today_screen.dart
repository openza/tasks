import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/task.dart';
import '../../providers/task_provider.dart';
import '../../providers/repository_provider.dart';
import '../../providers/selected_project_provider.dart';
import '../../widgets/tasks/task_list.dart';
import '../../widgets/tasks/task_detail.dart';

class TodayScreen extends ConsumerWidget {
  const TodayScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final todayTasksAsync = ref.watch(todayTasksProvider);
    final unifiedDataAsync = ref.watch(unifiedDataProvider);
    final selectedTask = ref.watch(selectedTaskProvider);
    final isDark = Theme.of(context).brightness == Brightness.dark;

    return Container(
      color: isDark ? AppTheme.gray900 : Colors.white,
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
                          color: AppTheme.warningOrange.withValues(alpha: 0.1),
                          borderRadius: BorderRadius.circular(8),
                        ),
                        child: Icon(
                          LucideIcons.calendarDays,
                          size: 20,
                          color: AppTheme.warningOrange,
                        ),
                      ),
                      const SizedBox(width: 12),
                      Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            'Today',
                            style: Theme.of(context).textTheme.headlineSmall,
                          ),
                          todayTasksAsync.when(
                            skipLoadingOnRefresh: true,
                            data: (tasks) => Text(
                              '${tasks.length} task${tasks.length != 1 ? 's' : ''} due today',
                              style: Theme.of(context)
                                  .textTheme
                                  .bodySmall
                                  ?.copyWith(color: AppTheme.gray500),
                            ),
                            loading: () => Text(
                              'Loading...',
                              style: Theme.of(context)
                                  .textTheme
                                  .bodySmall
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
                  child: todayTasksAsync.when(
                    skipLoadingOnRefresh: true,
                    data: (tasks) => unifiedDataAsync.when(
                      skipLoadingOnRefresh: true,
                      data: (data) => TaskListWidget(
                        tasks: tasks,
                        projects: data.projects,
                        filter: TaskFilter.today,
                        emptyMessage: 'No tasks due today',
                        selectedTaskId: selectedTask?.id,
                        onTaskTap: (task) =>
                            ref.read(selectedTaskIdProvider.notifier).state = task.id,
                        onTaskComplete: (task) => _completeTask(ref, task),
                      ),
                      loading: () =>
                          const Center(child: CircularProgressIndicator()),
                      error: (e, _) => Center(child: Text('Error: $e')),
                    ),
                    loading: () =>
                        const Center(child: CircularProgressIndicator()),
                    error: (e, _) => Center(child: Text('Error: $e')),
                  ),
                ),
              ],
            ),
          ),

          // Detail panel
          if (selectedTask != null)
            unifiedDataAsync.when(
              skipLoadingOnRefresh: true,
              data: (data) => TaskDetail(
                task: selectedTask,
                project: _getProjectForTask(selectedTask, data.projects),
                onClose: () => ref.read(selectedTaskIdProvider.notifier).state = null,
                onComplete: (task) => _completeTask(ref, task),
              ),
              loading: () => const SizedBox.shrink(),
              error: (_, __) => const SizedBox.shrink(),
            ),
        ],
      ),
    );
  }

  Future<void> _completeTask(WidgetRef ref, TaskEntity task) async {
    final repository = await ref.read(taskRepositoryProvider.future);
    await repository.completeTask(task);
    ref.refresh(unifiedDataProvider);
    ref.refresh(todayTasksProvider);
    final selectedTaskId = ref.read(selectedTaskIdProvider);
    if (selectedTaskId == task.id) {
      ref.read(selectedTaskIdProvider.notifier).state = null;
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
