import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/label.dart';
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
  TaskEntity? _selectedTask;
  String? _selectedLabelFilter;

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

  /// Filter tasks by selected label
  List<TaskEntity> _filterByLabel(List<TaskEntity> tasks) {
    if (_selectedLabelFilter == null) return tasks;
    return tasks.where((task) {
      return task.labels.any((label) => label.name == _selectedLabelFilter);
    }).toList();
  }

  @override
  Widget build(BuildContext context) {
    final labeledTasksAsync = ref.watch(labeledTasksProvider);
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
                                      ?.copyWith(color: AppTheme.gray500),
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
                                  _selectedLabelFilter != null
                                      ? 'No tasks with "$_selectedLabelFilter" label'
                                      : 'No labeled tasks found',
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
                      );
                    },
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

  Widget _buildLabelFilterBar(BuildContext context, List<TaskEntity> tasks) {
    final availableLabels = _getAvailableLabels(tasks);
    if (availableLabels.isEmpty) return const SizedBox.shrink();

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
            // "All Labels" button
            _FilterButton(
              label: 'All Labels',
              count: tasks.length,
              isSelected: _selectedLabelFilter == null,
              onTap: () => setState(() => _selectedLabelFilter = null),
            ),
            const SizedBox(width: 8),
            // Individual label filters
            ...availableLabels.values.map(
              (item) => Padding(
                padding: const EdgeInsets.only(right: 8),
                child: LabelBadge(
                  label: item.label,
                  count: item.count,
                  isSelected: _selectedLabelFilter == item.label.name,
                  onTap:
                      () => setState(
                        () => _selectedLabelFilter = item.label.name,
                      ),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  void _completeTask(TaskEntity task) {
    // TODO: Implement task completion logic
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

/// "All Labels" filter button
class _FilterButton extends StatelessWidget {
  final String label;
  final int count;
  final bool isSelected;
  final VoidCallback onTap;

  const _FilterButton({
    required this.label,
    required this.count,
    required this.isSelected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Material(
      color: isSelected ? AppTheme.gray900 : Colors.white,
      borderRadius: BorderRadius.circular(20),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(20),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(20),
            border: isSelected ? null : Border.all(color: AppTheme.gray200),
          ),
          child: Text(
            '$label ($count)',
            style: TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w500,
              color: isSelected ? Colors.white : AppTheme.gray700,
            ),
          ),
        ),
      ),
    );
  }
}
