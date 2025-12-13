import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/task.dart';
import '../../providers/task_provider.dart';
import '../../providers/repository_provider.dart';
import '../../widgets/tasks/task_list.dart';
import '../../widgets/tasks/task_detail.dart';

class TodayScreen extends ConsumerStatefulWidget {
  const TodayScreen({super.key});

  @override
  ConsumerState<TodayScreen> createState() => _TodayScreenState();
}

class _TodayScreenState extends ConsumerState<TodayScreen> {
  TaskEntity? _selectedTask;

  @override
  Widget build(BuildContext context) {
    final todayTasksAsync = ref.watch(todayTasksProvider);
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
                    data: (tasks) => unifiedDataAsync.when(
                      data: (data) => TaskListWidget(
                        tasks: tasks,
                        projects: data.projects,
                        filter: TaskFilter.today,
                        emptyMessage: 'No tasks due today',
                        onTaskTap: (task) =>
                            setState(() => _selectedTask = task),
                        onTaskComplete: _completeTask,
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
          if (_selectedTask != null)
            unifiedDataAsync.when(
              data: (data) => TaskDetail(
                task: _selectedTask!,
                project: _getProjectForTask(_selectedTask!, data.projects),
                onClose: () => setState(() => _selectedTask = null),
                onComplete: _completeTask,
              ),
              loading: () => const SizedBox.shrink(),
              error: (_, __) => const SizedBox.shrink(),
            ),
        ],
      ),
    );
  }

  Future<void> _completeTask(TaskEntity task) async {
    final repository = await ref.read(taskRepositoryProvider.future);
    await repository.completeTask(task);
    ref.invalidate(unifiedDataProvider);
    ref.invalidate(todayTasksProvider);
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
