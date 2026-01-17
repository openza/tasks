import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import '../../models/task_ui_state.dart';
import '../../providers/selected_project_provider.dart';
import 'task_list.dart';
import 'task_detail.dart';

// Re-export enums for backward compatibility
export '../../models/task_ui_state.dart';

/// Widget displaying tasks with filters and detail panel
/// Uses Riverpod providers for all UI state to persist across data refreshes
class TasksWithTabs extends ConsumerWidget {
  final List<TaskEntity> tasks;
  final List<ProjectEntity> projects;
  final String? selectedProjectId;
  final void Function(TaskEntity)? onTaskComplete;
  final void Function(TaskEntity)? onTaskUpdate;
  final void Function(TaskEntity)? onTaskDelete;

  const TasksWithTabs({
    super.key,
    required this.tasks,
    this.projects = const [],
    this.selectedProjectId,
    this.onTaskComplete,
    this.onTaskUpdate,
    this.onTaskDelete,
  });

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // Watch all UI state from providers (persists across data refreshes)
    final searchQuery = ref.watch(tasksSearchQueryProvider);
    final sortOption = ref.watch(tasksSortOptionProvider);
    final priorityFilter = ref.watch(tasksPriorityFilterProvider);
    final dueDateFilter = ref.watch(tasksDueDateFilterProvider);
    final createdDateFilter = ref.watch(tasksCreatedDateFilterProvider);
    final selectedLabelId = ref.watch(tasksSelectedLabelIdProvider);
    final selectedProjectIdForFilter = ref.watch(tasksSelectedProjectIdForFilterProvider);
    final selectedTask = ref.watch(selectedTaskProvider);

    // Filter and sort tasks
    final filteredTasks = _getFilteredTasks(
      tasks: tasks,
      searchQuery: searchQuery,
      sortOption: sortOption,
      priorityFilter: priorityFilter,
      dueDateFilter: dueDateFilter,
      createdDateFilter: createdDateFilter,
      selectedLabelId: selectedLabelId,
      selectedProjectIdForFilter: selectedProjectIdForFilter,
      projects: projects,
    );
    final activeTasks = filteredTasks.where((t) => !t.isCompleted).toList();

    return Row(
      children: [
        Expanded(
          child: Column(
            children: [
              _buildHeader(context, ref, activeTasks.length),
              _buildFilters(context, ref),
              Expanded(
                child: TaskListWidget(
                  tasks: activeTasks,
                  projects: projects,
                  emptyMessage: 'No tasks found',
                  preserveOrder: true,
                  onTaskTap: (task) => _selectTask(ref, task),
                  onTaskComplete: onTaskComplete,
                  selectedTaskId: selectedTask?.id,
                ),
              ),
            ],
          ),
        ),

        // Detail panel - uses provider-based selection
        if (selectedTask != null)
          TaskDetail(
            task: selectedTask,
            project: _getProjectForTask(selectedTask, projects),
            projects: projects,
            onClose: () => _clearSelection(ref),
            onUpdate: (task) {
              onTaskUpdate?.call(task);
            },
            onDelete: (task) {
              onTaskDelete?.call(task);
              _clearSelection(ref);
            },
            onComplete: (task) {
              onTaskComplete?.call(task);
              _clearSelection(ref);
            },
          ),
      ],
    );
  }

  /// Select a task by updating the provider
  void _selectTask(WidgetRef ref, TaskEntity task) {
    ref.read(selectedTaskIdProvider.notifier).state = task.id;
  }

  /// Clear task selection
  void _clearSelection(WidgetRef ref) {
    ref.read(selectedTaskIdProvider.notifier).state = null;
  }

  /// Get filtered and sorted tasks
  List<TaskEntity> _getFilteredTasks({
    required List<TaskEntity> tasks,
    required String searchQuery,
    required TaskSortOption sortOption,
    required PriorityFilter priorityFilter,
    required DueDateFilter dueDateFilter,
    required CreatedDateFilter createdDateFilter,
    required String? selectedLabelId,
    required String? selectedProjectIdForFilter,
    required List<ProjectEntity> projects,
  }) {
    var result = tasks;

    // Apply search filter
    if (searchQuery.isNotEmpty) {
      result = result.where((t) {
        final titleMatch = t.title.toLowerCase().contains(searchQuery.toLowerCase());
        final descMatch = t.description?.toLowerCase().contains(searchQuery.toLowerCase()) ?? false;
        return titleMatch || descMatch;
      }).toList();
    }

    // Apply dynamic filter based on sort option
    result = _applyDynamicFilter(
      result,
      sortOption: sortOption,
      priorityFilter: priorityFilter,
      dueDateFilter: dueDateFilter,
      createdDateFilter: createdDateFilter,
      selectedLabelId: selectedLabelId,
      selectedProjectIdForFilter: selectedProjectIdForFilter,
    );

    // Apply sorting
    return _sortTasks(result, sortOption, projects);
  }

  /// Apply filter based on current sort option
  List<TaskEntity> _applyDynamicFilter(
    List<TaskEntity> tasks, {
    required TaskSortOption sortOption,
    required PriorityFilter priorityFilter,
    required DueDateFilter dueDateFilter,
    required CreatedDateFilter createdDateFilter,
    required String? selectedLabelId,
    required String? selectedProjectIdForFilter,
  }) {
    switch (sortOption) {
      case TaskSortOption.priority:
        if (priorityFilter != PriorityFilter.all) {
          tasks = tasks.where((t) => t.priority == priorityFilter.value).toList();
        }
        break;

      case TaskSortOption.dueDate:
        tasks = _applyDueDateFilter(tasks, dueDateFilter);
        break;

      case TaskSortOption.createdDate:
        tasks = _applyCreatedDateFilter(tasks, createdDateFilter);
        break;

      case TaskSortOption.byLabel:
        if (selectedLabelId != null) {
          tasks = tasks.where((t) => t.labels.any((l) => l.id == selectedLabelId)).toList();
        }
        break;

      case TaskSortOption.byProject:
        if (selectedProjectIdForFilter != null) {
          tasks = tasks.where((t) => t.projectId == selectedProjectIdForFilter).toList();
        }
        break;
    }
    return tasks;
  }

  /// Apply due date filter
  List<TaskEntity> _applyDueDateFilter(List<TaskEntity> tasks, DueDateFilter filter) {
    if (filter == DueDateFilter.all) return tasks;

    final ranges = _getDateRanges();

    return tasks.where((t) {
      final date = t.dueDate;
      switch (filter) {
        case DueDateFilter.all:
          return true;
        case DueDateFilter.today:
          return date != null && _isSameDay(date, ranges.today);
        case DueDateFilter.yesterday:
          return date != null && _isSameDay(date, ranges.yesterday);
        case DueDateFilter.last7Days:
          return date != null && !date.isBefore(ranges.last7Days) && _isOnOrBefore(date, ranges.today);
        case DueDateFilter.thisWeek:
          return date != null && !date.isBefore(ranges.thisWeekStart) && _isOnOrBefore(date, ranges.thisWeekEnd);
        case DueDateFilter.thisMonth:
          return date != null && !date.isBefore(ranges.thisMonthStart) && _isOnOrBefore(date, ranges.thisMonthEnd);
        case DueDateFilter.older:
          return date != null && date.isBefore(ranges.thisMonthStart);
        case DueDateFilter.noDate:
          return date == null;
      }
    }).toList();
  }

  /// Apply created date filter
  List<TaskEntity> _applyCreatedDateFilter(List<TaskEntity> tasks, CreatedDateFilter filter) {
    if (filter == CreatedDateFilter.all) return tasks;

    final ranges = _getDateRanges();

    return tasks.where((t) {
      final date = t.createdAt;
      switch (filter) {
        case CreatedDateFilter.all:
          return true;
        case CreatedDateFilter.today:
          return _isSameDay(date, ranges.today);
        case CreatedDateFilter.yesterday:
          return _isSameDay(date, ranges.yesterday);
        case CreatedDateFilter.last7Days:
          return !date.isBefore(ranges.last7Days) && _isOnOrBefore(date, ranges.today);
        case CreatedDateFilter.thisWeek:
          return !date.isBefore(ranges.thisWeekStart) && _isOnOrBefore(date, ranges.today);
        case CreatedDateFilter.thisMonth:
          return !date.isBefore(ranges.thisMonthStart) && _isOnOrBefore(date, ranges.today);
        case CreatedDateFilter.older:
          return date.isBefore(ranges.thisMonthStart);
      }
    }).toList();
  }

  /// Get date ranges for filtering (DRY helper)
  ({
    DateTime today,
    DateTime yesterday,
    DateTime last7Days,
    DateTime thisWeekStart,
    DateTime thisWeekEnd,
    DateTime thisMonthStart,
    DateTime thisMonthEnd,
  }) _getDateRanges() {
    final now = DateTime.now();
    final today = DateTime(now.year, now.month, now.day);
    final weekday = now.weekday;
    final thisWeekStart = today.subtract(Duration(days: weekday - 1));
    final thisWeekEnd = thisWeekStart.add(const Duration(days: 6));
    final thisMonthStart = DateTime(now.year, now.month, 1);
    final nextMonth = now.month == 12 ? DateTime(now.year + 1, 1, 1) : DateTime(now.year, now.month + 1, 1);
    final thisMonthEnd = nextMonth.subtract(const Duration(days: 1));

    return (
      today: today,
      yesterday: today.subtract(const Duration(days: 1)),
      last7Days: today.subtract(const Duration(days: 6)),
      thisWeekStart: thisWeekStart,
      thisWeekEnd: thisWeekEnd,
      thisMonthStart: thisMonthStart,
      thisMonthEnd: thisMonthEnd,
    );
  }

  /// Check if date is on or before target date (inclusive upper bound)
  bool _isOnOrBefore(DateTime date, DateTime target) {
    final dateOnly = DateTime(date.year, date.month, date.day);
    final targetOnly = DateTime(target.year, target.month, target.day);
    return !dateOnly.isAfter(targetOnly);
  }

  /// Check if two dates are the same day
  bool _isSameDay(DateTime a, DateTime b) =>
      a.year == b.year && a.month == b.month && a.day == b.day;

  List<TaskEntity> _sortTasks(List<TaskEntity> tasks, TaskSortOption sortOption, List<ProjectEntity> projects) {
    final sorted = List<TaskEntity>.from(tasks);

    switch (sortOption) {
      case TaskSortOption.priority:
        sorted.sort((a, b) {
          final priorityCompare = a.priority.compareTo(b.priority);
          if (priorityCompare != 0) return priorityCompare;
          if (a.dueDate != null && b.dueDate != null) {
            return a.dueDate!.compareTo(b.dueDate!);
          }
          if (a.dueDate != null) return -1;
          if (b.dueDate != null) return 1;
          return 0;
        });
        break;

      case TaskSortOption.dueDate:
        sorted.sort((a, b) {
          if (a.dueDate != null && b.dueDate != null) {
            return a.dueDate!.compareTo(b.dueDate!);
          }
          if (a.dueDate != null) return -1;
          if (b.dueDate != null) return 1;
          return a.priority.compareTo(b.priority);
        });
        break;

      case TaskSortOption.createdDate:
        sorted.sort((a, b) => b.createdAt.compareTo(a.createdAt));
        break;

      case TaskSortOption.byLabel:
        sorted.sort((a, b) {
          if (a.hasLabels && !b.hasLabels) return -1;
          if (!a.hasLabels && b.hasLabels) return 1;
          if (a.hasLabels && b.hasLabels) {
            return a.labels.first.name.compareTo(b.labels.first.name);
          }
          return 0;
        });
        break;

      case TaskSortOption.byProject:
        sorted.sort((a, b) {
          final projectA = _getProjectForTask(a, projects);
          final projectB = _getProjectForTask(b, projects);
          if (projectA != null && projectB == null) return -1;
          if (projectA == null && projectB != null) return 1;
          if (projectA == null && projectB == null) return 0;
          return projectA!.name.compareTo(projectB!.name);
        });
        break;
    }

    return sorted;
  }

  Widget _buildHeader(BuildContext context, WidgetRef ref, int taskCount) {
    final selectedProject = selectedProjectId != null
        ? projects.where((p) => p.id == selectedProjectId).firstOrNull
        : null;
    final headerTitle = selectedProject?.name ?? 'Tasks';

    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 0),
      child: Row(
        children: [
          if (selectedProject != null) ...[
            Container(
              width: 12,
              height: 12,
              decoration: BoxDecoration(
                color: _parseColor(selectedProject.color),
                borderRadius: BorderRadius.circular(3),
              ),
            ),
            const SizedBox(width: 10),
          ],
          Text(
            headerTitle,
            style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                  fontWeight: FontWeight.bold,
                ),
          ),
          const SizedBox(width: 12),
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
            decoration: BoxDecoration(
              color: AppTheme.primaryBlue.withValues(alpha: 0.1),
              borderRadius: BorderRadius.circular(12),
            ),
            child: Text(
              '$taskCount ${taskCount == 1 ? 'task' : 'tasks'}',
              style: TextStyle(
                fontSize: 13,
                fontWeight: FontWeight.w500,
                color: AppTheme.primaryBlue,
              ),
            ),
          ),
          const Spacer(),
        ],
      ),
    );
  }

  Widget _buildFilters(BuildContext context, WidgetRef ref) {
    final searchQuery = ref.watch(tasksSearchQueryProvider);
    final sortOption = ref.watch(tasksSortOptionProvider);
    final hasActiveFilters = searchQuery.isNotEmpty || _hasActiveFilter(ref, sortOption);
    final isDark = Theme.of(context).brightness == Brightness.dark;

    final filterBg = isDark ? AppTheme.gray800 : Colors.white;
    final filterBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;
    final textColor = isDark ? AppTheme.gray200 : AppTheme.gray900;
    final labelColor = isDark ? AppTheme.gray400 : AppTheme.gray500;

    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          // Search - flexible with max width
          Flexible(
            child: Container(
              height: 36,
              constraints: const BoxConstraints(maxWidth: 280),
              decoration: BoxDecoration(
                color: filterBg,
                borderRadius: BorderRadius.circular(8),
                border: Border.all(color: filterBorder),
              ),
              child: _SearchField(
                initialValue: searchQuery,
                onChanged: (value) => ref.read(tasksSearchQueryProvider.notifier).state = value,
                textColor: textColor,
                isDark: isDark,
              ),
            ),
          ),
          const SizedBox(width: 16),

          // Sort section with label
          Text(
            'Sort',
            style: TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w500,
              color: labelColor,
            ),
          ),
          const SizedBox(width: 8),
          _buildSortDropdown(context, ref),

          const SizedBox(width: 16),

          // Filter section with label
          Text(
            'Filter',
            style: TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w500,
              color: labelColor,
            ),
          ),
          const SizedBox(width: 8),
          _buildDynamicFilter(context, ref),

          // Clear filters button
          if (hasActiveFilters) ...[
            const SizedBox(width: 12),
            _buildClearFiltersButton(context, ref, isDark),
          ],
        ],
      ),
    );
  }

  /// Check if any filter is active (non-"All")
  bool _hasActiveFilter(WidgetRef ref, TaskSortOption sortOption) {
    switch (sortOption) {
      case TaskSortOption.priority:
        return ref.read(tasksPriorityFilterProvider) != PriorityFilter.all;
      case TaskSortOption.dueDate:
        return ref.read(tasksDueDateFilterProvider) != DueDateFilter.all;
      case TaskSortOption.createdDate:
        return ref.read(tasksCreatedDateFilterProvider) != CreatedDateFilter.all;
      case TaskSortOption.byLabel:
        return ref.read(tasksSelectedLabelIdProvider) != null;
      case TaskSortOption.byProject:
        return ref.read(tasksSelectedProjectIdForFilterProvider) != null;
    }
  }

  /// Reset all filters to default
  void _resetFilters(WidgetRef ref) {
    ref.read(tasksPriorityFilterProvider.notifier).state = PriorityFilter.all;
    ref.read(tasksDueDateFilterProvider.notifier).state = DueDateFilter.all;
    ref.read(tasksCreatedDateFilterProvider.notifier).state = CreatedDateFilter.all;
    ref.read(tasksSelectedLabelIdProvider.notifier).state = null;
    ref.read(tasksSelectedProjectIdForFilterProvider.notifier).state = null;
  }

  /// Build the appropriate filter dropdown based on current sort option
  Widget _buildDynamicFilter(BuildContext context, WidgetRef ref) {
    final sortOption = ref.watch(tasksSortOptionProvider);
    switch (sortOption) {
      case TaskSortOption.priority:
        return _buildPriorityFilterDropdown(context, ref);
      case TaskSortOption.dueDate:
        return _buildDueDateFilterDropdown(context, ref);
      case TaskSortOption.createdDate:
        return _buildCreatedDateFilterDropdown(context, ref);
      case TaskSortOption.byLabel:
        return _buildLabelFilterDropdown(context, ref);
      case TaskSortOption.byProject:
        return _buildProjectFilterDropdown(context, ref);
    }
  }

  /// Priority filter dropdown
  Widget _buildPriorityFilterDropdown(BuildContext context, WidgetRef ref) {
    final priorityFilter = ref.watch(tasksPriorityFilterProvider);
    final isActive = priorityFilter != PriorityFilter.all;
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final priorityColor = priorityFilter.value != null
        ? AppTheme.priorityColors[priorityFilter.value] ?? AppTheme.gray500
        : AppTheme.gray500;

    final filterBg = isDark ? AppTheme.gray800 : Colors.white;
    final filterBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;

    return Container(
      height: 36,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: filterBg,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(
          color: isActive ? priorityColor : filterBorder,
        ),
      ),
      child: DropdownButton<PriorityFilter>(
        value: priorityFilter,
        selectedItemBuilder: (_) => PriorityFilter.values.map((filter) {
          if (filter == PriorityFilter.all) {
            return Center(
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(LucideIcons.flag, size: 14, color: AppTheme.gray500),
                  const SizedBox(width: 6),
                  const Text('All', style: TextStyle(fontSize: 13)),
                ],
              ),
            );
          }
          return Center(
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  width: 8,
                  height: 8,
                  decoration: BoxDecoration(
                    color: AppTheme.priorityColors[filter.value],
                    shape: BoxShape.circle,
                  ),
                ),
                const SizedBox(width: 6),
                Text(
                  filter.label,
                  style: TextStyle(
                    fontSize: 13,
                    color: isActive && priorityFilter == filter
                        ? AppTheme.priorityColors[filter.value]
                        : isDark ? AppTheme.gray300 : AppTheme.gray700,
                  ),
                ),
              ],
            ),
          );
        }).toList(),
        items: PriorityFilter.values.map((filter) {
          if (filter == PriorityFilter.all) {
            return DropdownMenuItem(
              value: filter,
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(LucideIcons.layers, size: 14, color: AppTheme.gray500),
                  const SizedBox(width: 8),
                  const Text('All Priorities', style: TextStyle(fontSize: 13)),
                ],
              ),
            );
          }
          return DropdownMenuItem(
            value: filter,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  width: 10,
                  height: 10,
                  decoration: BoxDecoration(
                    color: AppTheme.priorityColors[filter.value],
                    shape: BoxShape.circle,
                  ),
                ),
                const SizedBox(width: 8),
                Text(filter.label, style: const TextStyle(fontSize: 13)),
              ],
            ),
          );
        }).toList(),
        onChanged: (value) => ref.read(tasksPriorityFilterProvider.notifier).state = value ?? PriorityFilter.all,
        underline: const SizedBox.shrink(),
        isDense: true,
        icon: Icon(LucideIcons.chevronDown, size: 14, color: isActive ? priorityColor : AppTheme.gray400),
      ),
    );
  }

  /// Due date filter dropdown
  Widget _buildDueDateFilterDropdown(BuildContext context, WidgetRef ref) {
    final dueDateFilter = ref.watch(tasksDueDateFilterProvider);
    final isActive = dueDateFilter != DueDateFilter.all;
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final activeColor = AppTheme.primaryBlue;

    final filterBg = isDark ? AppTheme.gray800 : Colors.white;
    final filterBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;

    return Container(
      height: 36,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: filterBg,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(
          color: isActive ? activeColor : filterBorder,
        ),
      ),
      child: DropdownButton<DueDateFilter>(
        value: dueDateFilter,
        selectedItemBuilder: (_) => DueDateFilter.values.map((filter) {
          return Center(
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(LucideIcons.calendar, size: 14, color: isActive ? activeColor : AppTheme.gray500),
                const SizedBox(width: 6),
                Text(
                  filter.label,
                  style: TextStyle(
                    fontSize: 13,
                    color: isActive ? activeColor : (isDark ? AppTheme.gray300 : AppTheme.gray700),
                  ),
                ),
              ],
            ),
          );
        }).toList(),
        items: DueDateFilter.values.map((filter) {
          return DropdownMenuItem(
            value: filter,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  filter == DueDateFilter.all ? LucideIcons.layers : LucideIcons.calendar,
                  size: 14,
                  color: AppTheme.gray500,
                ),
                const SizedBox(width: 8),
                Text(filter.label, style: const TextStyle(fontSize: 13)),
              ],
            ),
          );
        }).toList(),
        onChanged: (value) => ref.read(tasksDueDateFilterProvider.notifier).state = value ?? DueDateFilter.all,
        underline: const SizedBox.shrink(),
        isDense: true,
        icon: Icon(LucideIcons.chevronDown, size: 14, color: isActive ? activeColor : AppTheme.gray400),
      ),
    );
  }

  /// Created date filter dropdown
  Widget _buildCreatedDateFilterDropdown(BuildContext context, WidgetRef ref) {
    final createdDateFilter = ref.watch(tasksCreatedDateFilterProvider);
    final isActive = createdDateFilter != CreatedDateFilter.all;
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final activeColor = AppTheme.primaryBlue;

    final filterBg = isDark ? AppTheme.gray800 : Colors.white;
    final filterBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;

    return Container(
      height: 36,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: filterBg,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(
          color: isActive ? activeColor : filterBorder,
        ),
      ),
      child: DropdownButton<CreatedDateFilter>(
        value: createdDateFilter,
        selectedItemBuilder: (_) => CreatedDateFilter.values.map((filter) {
          return Center(
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(LucideIcons.clock, size: 14, color: isActive ? activeColor : AppTheme.gray500),
                const SizedBox(width: 6),
                Text(
                  filter.label,
                  style: TextStyle(
                    fontSize: 13,
                    color: isActive ? activeColor : (isDark ? AppTheme.gray300 : AppTheme.gray700),
                  ),
                ),
              ],
            ),
          );
        }).toList(),
        items: CreatedDateFilter.values.map((filter) {
          return DropdownMenuItem(
            value: filter,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  filter == CreatedDateFilter.all ? LucideIcons.layers : LucideIcons.clock,
                  size: 14,
                  color: AppTheme.gray500,
                ),
                const SizedBox(width: 8),
                Text(filter.label, style: const TextStyle(fontSize: 13)),
              ],
            ),
          );
        }).toList(),
        onChanged: (value) => ref.read(tasksCreatedDateFilterProvider.notifier).state = value ?? CreatedDateFilter.all,
        underline: const SizedBox.shrink(),
        isDense: true,
        icon: Icon(LucideIcons.chevronDown, size: 14, color: isActive ? activeColor : AppTheme.gray400),
      ),
    );
  }

  /// Label filter dropdown - dynamically built from task labels
  Widget _buildLabelFilterDropdown(BuildContext context, WidgetRef ref) {
    final selectedLabelId = ref.watch(tasksSelectedLabelIdProvider);
    final isActive = selectedLabelId != null;
    final isDark = Theme.of(context).brightness == Brightness.dark;

    // Extract unique labels from tasks
    final labelsMap = <String, ({String id, String name, String color})>{};
    for (final task in tasks) {
      for (final label in task.labels) {
        labelsMap[label.id] = (id: label.id, name: label.name, color: label.color);
      }
    }
    final labels = labelsMap.values.toList()..sort((a, b) => a.name.compareTo(b.name));

    // Find selected label for color
    final selectedLabel = labels.where((l) => l.id == selectedLabelId).firstOrNull;
    final labelColor = selectedLabel != null ? _parseColor(selectedLabel.color) : AppTheme.gray500;

    final filterBg = isDark ? AppTheme.gray800 : Colors.white;
    final filterBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;

    return Container(
      height: 36,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: filterBg,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(
          color: isActive ? labelColor : filterBorder,
        ),
      ),
      child: DropdownButton<String?>(
        value: selectedLabelId,
        selectedItemBuilder: (_) => [
          // All option
          Center(
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(LucideIcons.tag, size: 14, color: AppTheme.gray500),
                const SizedBox(width: 6),
                Text('All', style: TextStyle(fontSize: 13, color: isDark ? AppTheme.gray300 : AppTheme.gray700)),
              ],
            ),
          ),
          // Label options
          ...labels.map((label) => Center(
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  width: 8,
                  height: 8,
                  decoration: BoxDecoration(
                    color: _parseColor(label.color),
                    shape: BoxShape.circle,
                  ),
                ),
                const SizedBox(width: 6),
                Text(
                  label.name,
                  style: TextStyle(
                    fontSize: 13,
                    color: isActive && selectedLabelId == label.id
                        ? labelColor
                        : isDark ? AppTheme.gray300 : AppTheme.gray700,
                  ),
                  overflow: TextOverflow.ellipsis,
                ),
              ],
            ),
          )),
        ],
        items: [
          DropdownMenuItem(
            value: null,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(LucideIcons.layers, size: 14, color: AppTheme.gray500),
                const SizedBox(width: 8),
                const Text('All Labels', style: TextStyle(fontSize: 13)),
              ],
            ),
          ),
          ...labels.map((label) => DropdownMenuItem(
            value: label.id,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  width: 10,
                  height: 10,
                  decoration: BoxDecoration(
                    color: _parseColor(label.color),
                    shape: BoxShape.circle,
                  ),
                ),
                const SizedBox(width: 8),
                Text(
                  label.name,
                  style: const TextStyle(fontSize: 13),
                  overflow: TextOverflow.ellipsis,
                ),
              ],
            ),
          )),
        ],
        onChanged: (value) => ref.read(tasksSelectedLabelIdProvider.notifier).state = value,
        underline: const SizedBox.shrink(),
        isDense: true,
        icon: Icon(LucideIcons.chevronDown, size: 14, color: isActive ? labelColor : AppTheme.gray400),
      ),
    );
  }

  /// Project filter dropdown - dynamically built from available projects
  Widget _buildProjectFilterDropdown(BuildContext context, WidgetRef ref) {
    final selectedProjectIdForFilter = ref.watch(tasksSelectedProjectIdForFilterProvider);
    final isActive = selectedProjectIdForFilter != null;
    final isDark = Theme.of(context).brightness == Brightness.dark;

    // Get projects that have tasks
    final projectsWithTasks = <String, ProjectEntity>{};
    for (final task in tasks) {
      if (task.projectId != null) {
        final project = _getProjectForTask(task, projects);
        if (project != null) {
          projectsWithTasks[project.id] = project;
        }
      }
    }
    final availableProjects = projectsWithTasks.values.toList()..sort((a, b) => a.name.compareTo(b.name));

    // Find selected project for color
    final selectedProject = availableProjects.where((p) => p.id == selectedProjectIdForFilter).firstOrNull;
    final projectColor = selectedProject != null ? _parseColor(selectedProject.color) : AppTheme.gray500;

    final filterBg = isDark ? AppTheme.gray800 : Colors.white;
    final filterBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;

    return Container(
      height: 36,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: filterBg,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(
          color: isActive ? projectColor : filterBorder,
        ),
      ),
      child: DropdownButton<String?>(
        value: selectedProjectIdForFilter,
        selectedItemBuilder: (_) => [
          // All option
          Center(
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(LucideIcons.folder, size: 14, color: AppTheme.gray500),
                const SizedBox(width: 6),
                Text('All', style: TextStyle(fontSize: 13, color: isDark ? AppTheme.gray300 : AppTheme.gray700)),
              ],
            ),
          ),
          // Project options
          ...availableProjects.map((project) => Center(
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  width: 8,
                  height: 8,
                  decoration: BoxDecoration(
                    color: _parseColor(project.color),
                    borderRadius: BorderRadius.circular(2),
                  ),
                ),
                const SizedBox(width: 6),
                Text(
                  project.name,
                  style: TextStyle(
                    fontSize: 13,
                    color: isActive && selectedProjectIdForFilter == project.id
                        ? projectColor
                        : isDark ? AppTheme.gray300 : AppTheme.gray700,
                  ),
                  overflow: TextOverflow.ellipsis,
                ),
              ],
            ),
          )),
        ],
        items: [
          DropdownMenuItem(
            value: null,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(LucideIcons.layers, size: 14, color: AppTheme.gray500),
                const SizedBox(width: 8),
                const Text('All Projects', style: TextStyle(fontSize: 13)),
              ],
            ),
          ),
          ...availableProjects.map((project) => DropdownMenuItem(
            value: project.id,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  width: 10,
                  height: 10,
                  decoration: BoxDecoration(
                    color: _parseColor(project.color),
                    borderRadius: BorderRadius.circular(2),
                  ),
                ),
                const SizedBox(width: 8),
                Text(
                  project.name,
                  style: const TextStyle(fontSize: 13),
                  overflow: TextOverflow.ellipsis,
                ),
              ],
            ),
          )),
        ],
        onChanged: (value) => ref.read(tasksSelectedProjectIdForFilterProvider.notifier).state = value,
        underline: const SizedBox.shrink(),
        isDense: true,
        icon: Icon(LucideIcons.chevronDown, size: 14, color: isActive ? projectColor : AppTheme.gray400),
      ),
    );
  }

  /// Parse color string (hex format) to Color
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

  Widget _buildClearFiltersButton(BuildContext context, WidgetRef ref, bool isDark) {
    return InkWell(
      onTap: () {
        ref.read(tasksSearchQueryProvider.notifier).state = '';
        _resetFilters(ref);
      },
      borderRadius: BorderRadius.circular(6),
      child: Container(
        height: 36,
        padding: const EdgeInsets.symmetric(horizontal: 10),
        decoration: BoxDecoration(
          color: isDark ? AppTheme.gray800 : Colors.white,
          borderRadius: BorderRadius.circular(8),
          border: Border.all(color: isDark ? AppTheme.gray600 : AppTheme.gray300),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(LucideIcons.x, size: 14, color: AppTheme.gray500),
            const SizedBox(width: 4),
            Text(
              'Clear',
              style: TextStyle(
                fontSize: 13,
                color: isDark ? AppTheme.gray300 : AppTheme.gray600,
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildSortDropdown(BuildContext context, WidgetRef ref) {
    final sortOption = ref.watch(tasksSortOptionProvider);
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final filterBg = isDark ? AppTheme.gray800 : Colors.white;
    final filterBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;

    return Container(
      height: 36,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: filterBg,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: filterBorder),
      ),
      child: DropdownButton<TaskSortOption>(
        value: sortOption,
        selectedItemBuilder: (_) => TaskSortOption.values.map((option) {
          return Center(
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(option.icon, size: 14, color: AppTheme.gray500),
                const SizedBox(width: 6),
                Text(option.displayName, style: const TextStyle(fontSize: 13)),
              ],
            ),
          );
        }).toList(),
        items: TaskSortOption.values.map((option) {
          return DropdownMenuItem<TaskSortOption>(
            value: option,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(option.icon, size: 14, color: AppTheme.gray500),
                const SizedBox(width: 8),
                Text(option.displayName, style: const TextStyle(fontSize: 13)),
              ],
            ),
          );
        }).toList(),
        onChanged: (value) {
          if (value != null && value != sortOption) {
            ref.read(tasksSortOptionProvider.notifier).state = value;
            _resetFilters(ref); // Auto-reset filter when sort changes
          }
        },
        underline: const SizedBox.shrink(),
        isDense: true,
        icon: Icon(LucideIcons.chevronDown, size: 14, color: AppTheme.gray400),
      ),
    );
  }

  static ProjectEntity? _getProjectForTask(TaskEntity task, List<ProjectEntity> projects) {
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

/// Stateful search field to preserve text input across widget rebuilds
class _SearchField extends StatefulWidget {
  final String initialValue;
  final ValueChanged<String> onChanged;
  final Color textColor;
  final bool isDark;

  const _SearchField({
    required this.initialValue,
    required this.onChanged,
    required this.textColor,
    required this.isDark,
  });

  @override
  State<_SearchField> createState() => _SearchFieldState();
}

class _SearchFieldState extends State<_SearchField> {
  late final TextEditingController _controller;

  @override
  void initState() {
    super.initState();
    _controller = TextEditingController(text: widget.initialValue);
  }

  @override
  void didUpdateWidget(covariant _SearchField oldWidget) {
    super.didUpdateWidget(oldWidget);
    // Sync controller with provider state if cleared externally
    if (widget.initialValue.isEmpty && _controller.text.isNotEmpty) {
      _controller.clear();
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return TextField(
      controller: _controller,
      onChanged: widget.onChanged,
      style: TextStyle(fontSize: 13, color: widget.textColor),
      decoration: InputDecoration(
        hintText: 'Search...',
        hintStyle: TextStyle(fontSize: 13, color: widget.isDark ? AppTheme.gray400 : AppTheme.gray500),
        prefixIcon: Padding(
          padding: const EdgeInsets.only(left: 10, right: 8),
          child: Icon(LucideIcons.search, size: 16, color: widget.isDark ? AppTheme.gray400 : AppTheme.gray500),
        ),
        prefixIconConstraints: const BoxConstraints(minWidth: 36),
        isDense: true,
        border: InputBorder.none,
        enabledBorder: InputBorder.none,
        focusedBorder: InputBorder.none,
        contentPadding: const EdgeInsets.symmetric(vertical: 10),
      ),
    );
  }
}
