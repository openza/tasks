import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/task.dart';
import '../../providers/task_provider.dart';
import '../../providers/repository_provider.dart';
import '../../widgets/tasks/task_list.dart';
import '../../widgets/tasks/task_detail.dart';

class OverdueScreen extends ConsumerStatefulWidget {
  const OverdueScreen({super.key});

  @override
  ConsumerState<OverdueScreen> createState() => _OverdueScreenState();
}

class _OverdueScreenState extends ConsumerState<OverdueScreen> {
  TaskEntity? _selectedTask;

  @override
  Widget build(BuildContext context) {
    final overdueTasksAsync = ref.watch(overdueTasksProvider);
    final unifiedDataAsync = ref.watch(unifiedDataProvider);
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
                          color: AppTheme.errorRed.withValues(alpha: 0.1),
                          borderRadius: BorderRadius.circular(8),
                        ),
                        child: Icon(
                          LucideIcons.alertTriangle,
                          size: 20,
                          color: AppTheme.errorRed,
                        ),
                      ),
                      const SizedBox(width: 12),
                      Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            'Overdue',
                            style: Theme.of(context).textTheme.headlineSmall,
                          ),
                          overdueTasksAsync.when(
                            skipLoadingOnRefresh: true,
                            data:
                                (tasks) => Text(
                                  '${tasks.length} overdue task${tasks.length != 1 ? 's' : ''}',
                                  style: Theme.of(context).textTheme.bodySmall
                                      ?.copyWith(color: AppTheme.errorRed),
                                ),
                            loading:
                                () => Text(
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
                  child: overdueTasksAsync.when(
                    skipLoadingOnRefresh: true,
                    data:
                        (tasks) => unifiedDataAsync.when(
                          skipLoadingOnRefresh: true,
                          data:
                              (data) => TaskListWidget(
                                tasks: tasks,
                                projects: data.projects,
                                filter: TaskFilter.overdue,
                                emptyMessage: 'No overdue tasks - great job!',
                                onTaskTap:
                                    (task) =>
                                        setState(() => _selectedTask = task),
                                onTaskComplete: _completeTask,
                              ),
                          loading:
                              () => const Center(
                                child: CircularProgressIndicator(),
                              ),
                          error: (e, _) => Center(child: Text('Error: $e')),
                        ),
                    loading:
                        () => const Center(child: CircularProgressIndicator()),
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
              data:
                  (data) => TaskDetail(
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
    ref.invalidate(overdueTasksProvider);
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
