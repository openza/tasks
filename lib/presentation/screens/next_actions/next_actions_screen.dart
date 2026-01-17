import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/label.dart';
import '../../providers/repository_provider.dart';
import '../../providers/selected_project_provider.dart';
import '../../providers/task_provider.dart';
import '../../widgets/badges/label_badge.dart';
import '../../widgets/tasks/task_list.dart';
import '../../widgets/tasks/task_detail.dart';
import '../../../domain/entities/task.dart';

class NextActionsScreen extends ConsumerStatefulWidget {
  const NextActionsScreen({super.key});

  @override
  ConsumerState<NextActionsScreen> createState() => _NextActionsScreenState();
}

class _NextActionsScreenState extends ConsumerState<NextActionsScreen> {
  // Label filtering is screen-specific, so keep as local state
  final Set<String> _selectedLabels = {};

  /// Get unique labels from tasks with counts
  Map<String, _LabelWithCount> _getAvailableLabels(List<TaskEntity> tasks) {
    final labelCounts = <String, _LabelWithCount>{};

    for (final task in tasks) {
      for (final label in task.labels) {
        if (labelCounts.containsKey(label.name)) {
          labelCounts[label.name]!.count++;
        } else {
          labelCounts[label.name] = _LabelWithCount(label: label, count: 1);
        }
      }
    }

    return labelCounts;
  }

  /// Filter tasks by selected labels (multi-select: show tasks with ANY selected label)
  List<TaskEntity> _filterByLabel(List<TaskEntity> tasks) {
    if (_selectedLabels.isEmpty) return tasks;
    return tasks.where((task) {
      return task.labels.any((label) => _selectedLabels.contains(label.name));
    }).toList();
  }

  /// Toggle label selection
  void _toggleLabel(String labelName) {
    setState(() {
      if (_selectedLabels.contains(labelName)) {
        _selectedLabels.remove(labelName);
      } else {
        _selectedLabels.add(labelName);
      }
    });
  }

  /// Clear all selected labels
  void _clearFilters() {
    setState(() => _selectedLabels.clear());
  }

  @override
  Widget build(BuildContext context) {
    final labeledTasksAsync = ref.watch(labeledTasksProvider);
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
                          color: AppTheme.accentPink.withValues(alpha: 0.1),
                          borderRadius: BorderRadius.circular(8),
                        ),
                        child: Icon(
                          LucideIcons.zap,
                          size: 20,
                          color: AppTheme.accentPink,
                        ),
                      ),
                      const SizedBox(width: 12),
                      Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            'Next Actions',
                            style: Theme.of(context).textTheme.headlineSmall,
                          ),
                          labeledTasksAsync.when(
                            skipLoadingOnRefresh: true,
                            data:
                                (tasks) => Text(
                                  '${tasks.length} labeled task${tasks.length != 1 ? 's' : ''}',
                                  style: Theme.of(context).textTheme.bodySmall
                                      ?.copyWith(color: isDark ? AppTheme.gray400 : AppTheme.gray600),
                                ),
                            loading:
                                () => Text(
                                  'Loading...',
                                  style: Theme.of(context).textTheme.bodySmall
                                      ?.copyWith(color: isDark ? AppTheme.gray400 : AppTheme.gray600),
                                ),
                            error: (_, __) => const SizedBox.shrink(),
                          ),
                        ],
                      ),
                    ],
                  ),
                ),

                // Label Filter Bar
                labeledTasksAsync.when(
                  skipLoadingOnRefresh: true,
                  data: (tasks) => _buildLabelFilterBar(context, tasks),
                  loading: () => const SizedBox.shrink(),
                  error: (_, __) => const SizedBox.shrink(),
                ),

                // Task List
                Expanded(
                  child: labeledTasksAsync.when(
                    skipLoadingOnRefresh: true,
                    data: (tasks) {
                      final filteredTasks = _filterByLabel(tasks);
                      return unifiedDataAsync.when(
                        skipLoadingOnRefresh: true,
                        data:
                            (data) => TaskListWidget(
                              tasks: filteredTasks,
                              projects: data.projects,
                              filter: TaskFilter.labeled,
                              sortByLabels: true,
                              emptyMessage:
                                  _selectedLabels.isNotEmpty
                                      ? 'No tasks with selected label${_selectedLabels.length > 1 ? 's' : ''}'
                                      : 'No labeled tasks found',
                              onTaskTap:
                                  (task) =>
                                      ref.read(selectedTaskIdProvider.notifier).state = task.id,
                              onTaskComplete: _completeTask,
                              selectedTaskId: selectedTask?.id,
                            ),
                        loading:
                            () => const Center(
                              child: CircularProgressIndicator(),
                            ),
                        error: (e, _) => _buildErrorCard(context, 'Failed to load task data'),
                      );
                    },
                    loading:
                        () => const Center(child: CircularProgressIndicator()),
                    error: (e, _) => _buildErrorCard(context, 'Failed to load tasks'),
                  ),
                ),
              ],
            ),
          ),

          // Detail panel
          if (selectedTask != null)
            unifiedDataAsync.when(
              skipLoadingOnRefresh: true,
              data:
                  (data) => TaskDetail(
                    task: selectedTask,
                    project: _getProjectForTask(selectedTask, data.projects),
                    onClose: () => ref.read(selectedTaskIdProvider.notifier).state = null,
                    onComplete: _completeTask,
                  ),
              loading: () => const SizedBox.shrink(),
              error: (_, __) => const SizedBox.shrink(),
            ),
        ],
      ),
    );
  }

  Widget _buildLabelFilterBar(BuildContext context, List<TaskEntity> tasks) {
    final availableLabels = _getAvailableLabels(tasks);
    if (availableLabels.isEmpty) return const SizedBox.shrink();

    final isDark = Theme.of(context).brightness == Brightness.dark;
    final hasActiveFilters = _selectedLabels.isNotEmpty;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 12),
      decoration: BoxDecoration(
        color: Theme.of(context).colorScheme.surface,
        border: Border(
          bottom: BorderSide(color: Theme.of(context).dividerColor),
        ),
      ),
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: Row(
          children: [
            // Clear filters button (only shown when filters active)
            if (hasActiveFilters) ...[
              Material(
                color: Colors.transparent,
                child: InkWell(
                  onTap: _clearFilters,
                  borderRadius: BorderRadius.circular(16),
                  child: Container(
                    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
                    decoration: BoxDecoration(
                      borderRadius: BorderRadius.circular(16),
                      border: Border.all(
                        color: isDark ? AppTheme.gray600 : AppTheme.gray300,
                      ),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(
                          LucideIcons.x,
                          size: 12,
                          color: isDark ? AppTheme.gray400 : AppTheme.gray500,
                        ),
                        const SizedBox(width: 4),
                        Text(
                          'Clear',
                          style: TextStyle(
                            fontSize: 12,
                            fontWeight: FontWeight.w500,
                            color: isDark ? AppTheme.gray400 : AppTheme.gray600,
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
              const SizedBox(width: 8),
            ],
            // Label filters (multi-select toggle)
            ...availableLabels.values.map(
              (item) => Padding(
                padding: const EdgeInsets.only(right: 8),
                child: LabelBadge(
                  label: item.label,
                  count: item.count,
                  isSelected: _selectedLabels.contains(item.label.name),
                  onTap: () => _toggleLabel(item.label.name),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _completeTask(TaskEntity task) async {
    final repository = await ref.read(taskRepositoryProvider.future);
    await repository.completeTask(task);
    ref.refresh(localTasksProvider);
    ref.refresh(unifiedDataProvider);
    ref.refresh(labeledTasksProvider);
    final selectedTaskId = ref.read(selectedTaskIdProvider);
    if (selectedTaskId == task.id) {
      ref.read(selectedTaskIdProvider.notifier).state = null;
    }
  }

  Widget _buildErrorCard(BuildContext context, String message) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              LucideIcons.alertCircle,
              size: 48,
              color: AppTheme.errorRed,
            ),
            const SizedBox(height: 16),
            Text(
              message,
              style: Theme.of(context).textTheme.titleMedium?.copyWith(
                    color: AppTheme.gray700,
                  ),
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 8),
            Text(
              'Please try again later',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AppTheme.gray500,
                  ),
            ),
          ],
        ),
      ),
    );
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

/// Helper class to hold label with count
class _LabelWithCount {
  final LabelEntity label;
  int count;

  _LabelWithCount({required this.label, required this.count});
}

