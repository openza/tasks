import 'package:flutter/material.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import 'task_list.dart';
import 'task_detail.dart';

/// Sort options for task list
enum TaskSortOption {
  priority('Priority', LucideIcons.arrowUpDown),
  dueDate('Due Date', LucideIcons.calendar),
  createdDate('Created', LucideIcons.clock),
  byLabel('Label', LucideIcons.tag),
  byProject('Project', LucideIcons.folder);

  final String displayName;
  final IconData icon;
  const TaskSortOption(this.displayName, this.icon);
}

/// Priority filter options
enum PriorityFilter {
  all('All', null),
  p1Urgent('Urgent', 1),
  p2High('High', 2),
  p3Normal('Normal', 3),
  p4Low('Low', 4);

  final String label;
  final int? value;
  const PriorityFilter(this.label, this.value);
}

/// Due date filter options
enum DueDateFilter {
  all('All'),
  today('Today'),
  yesterday('Yesterday'),
  last7Days('Last 7 Days'),
  thisWeek('This Week'),
  thisMonth('This Month'),
  older('Older'),
  noDate('No Date');

  final String label;
  const DueDateFilter(this.label);
}

/// Created date filter options
enum CreatedDateFilter {
  all('All'),
  today('Today'),
  yesterday('Yesterday'),
  last7Days('Last 7 Days'),
  thisWeek('This Week'),
  thisMonth('This Month'),
  older('Older');

  final String label;
  const CreatedDateFilter(this.label);
}

/// Widget displaying tasks with filters and detail panel
class TasksWithTabs extends StatefulWidget {
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
  State<TasksWithTabs> createState() => _TasksWithTabsState();
}

class _TasksWithTabsState extends State<TasksWithTabs> {
  TaskEntity? _selectedTask;
  String _searchQuery = '';
  TaskSortOption _sortOption = TaskSortOption.priority;

  // Dynamic filter state - one per sort type
  PriorityFilter _priorityFilter = PriorityFilter.all;
  DueDateFilter _dueDateFilter = DueDateFilter.all;
  CreatedDateFilter _createdDateFilter = CreatedDateFilter.all;
  String? _selectedLabelId; // Label ID for filtering
  String? _selectedProjectId; // Project ID for filtering

  List<TaskEntity> get _filteredTasks {
    var tasks = widget.tasks;

    // Apply search filter
    if (_searchQuery.isNotEmpty) {
      tasks = tasks.where((t) {
        final titleMatch =
            t.title.toLowerCase().contains(_searchQuery.toLowerCase());
        final descMatch = t.description
                ?.toLowerCase()
                .contains(_searchQuery.toLowerCase()) ??
            false;
        return titleMatch || descMatch;
      }).toList();
    }

    // Apply dynamic filter based on current sort option
    tasks = _applyDynamicFilter(tasks);

    // Apply sorting
    return _sortTasks(tasks);
  }

  /// Apply filter based on current sort option
  List<TaskEntity> _applyDynamicFilter(List<TaskEntity> tasks) {
    switch (_sortOption) {
      case TaskSortOption.priority:
        if (_priorityFilter != PriorityFilter.all) {
          tasks = tasks.where((t) => t.priority == _priorityFilter.value).toList();
        }
        break;

      case TaskSortOption.dueDate:
        tasks = _applyDueDateFilter(tasks);
        break;

      case TaskSortOption.createdDate:
        tasks = _applyCreatedDateFilter(tasks);
        break;

      case TaskSortOption.byLabel:
        if (_selectedLabelId != null) {
          tasks = tasks.where((t) =>
            t.labels.any((l) => l.id == _selectedLabelId)
          ).toList();
        }
        break;

      case TaskSortOption.byProject:
        if (_selectedProjectId != null) {
          tasks = tasks.where((t) => t.projectId == _selectedProjectId).toList();
        }
        break;
    }
    return tasks;
  }

  /// Apply due date filter
  List<TaskEntity> _applyDueDateFilter(List<TaskEntity> tasks) {
    if (_dueDateFilter == DueDateFilter.all) return tasks;

    final ranges = _getDateRanges();

    return tasks.where((t) {
      final date = t.dueDate;
      switch (_dueDateFilter) {
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
  List<TaskEntity> _applyCreatedDateFilter(List<TaskEntity> tasks) {
    if (_createdDateFilter == CreatedDateFilter.all) return tasks;

    final ranges = _getDateRanges();

    return tasks.where((t) {
      final date = t.createdAt;
      switch (_createdDateFilter) {
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
      last7Days: today.subtract(const Duration(days: 6)), // 7 days including today
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

  /// Check if any filter is active (non-"All")
  bool get _hasActiveFilter {
    switch (_sortOption) {
      case TaskSortOption.priority:
        return _priorityFilter != PriorityFilter.all;
      case TaskSortOption.dueDate:
        return _dueDateFilter != DueDateFilter.all;
      case TaskSortOption.createdDate:
        return _createdDateFilter != CreatedDateFilter.all;
      case TaskSortOption.byLabel:
        return _selectedLabelId != null;
      case TaskSortOption.byProject:
        return _selectedProjectId != null;
    }
  }

  /// Reset all filters to default
  void _resetFilters() {
    _priorityFilter = PriorityFilter.all;
    _dueDateFilter = DueDateFilter.all;
    _createdDateFilter = CreatedDateFilter.all;
    _selectedLabelId = null;
    _selectedProjectId = null;
  }

  List<TaskEntity> _sortTasks(List<TaskEntity> tasks) {
    final sorted = List<TaskEntity>.from(tasks);

    switch (_sortOption) {
      case TaskSortOption.priority:
        sorted.sort((a, b) {
          // Priority first (lower number = higher priority)
          final priorityCompare = a.priority.compareTo(b.priority);
          if (priorityCompare != 0) return priorityCompare;
          // Then due date (nulls last)
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
          // Due date (nulls last)
          if (a.dueDate != null && b.dueDate != null) {
            return a.dueDate!.compareTo(b.dueDate!);
          }
          if (a.dueDate != null) return -1;
          if (b.dueDate != null) return 1;
          // Then priority
          return a.priority.compareTo(b.priority);
        });
        break;

      case TaskSortOption.createdDate:
        sorted.sort((a, b) => b.createdAt.compareTo(a.createdAt));
        break;

      case TaskSortOption.byLabel:
        sorted.sort((a, b) {
          // Tasks with labels first
          if (a.hasLabels && !b.hasLabels) return -1;
          if (!a.hasLabels && b.hasLabels) return 1;
          // Then by first label name alphabetically
          if (a.hasLabels && b.hasLabels) {
            return a.labels.first.name.compareTo(b.labels.first.name);
          }
          return 0;
        });
        break;

      case TaskSortOption.byProject:
        sorted.sort((a, b) {
          final projectA = _getProjectForTask(a);
          final projectB = _getProjectForTask(b);
          // Tasks with projects first
          if (projectA != null && projectB == null) return -1;
          if (projectA == null && projectB != null) return 1;
          if (projectA == null && projectB == null) return 0;
          // Then by project name alphabetically
          return projectA!.name.compareTo(projectB!.name);
        });
        break;
    }

    return sorted;
  }

  @override
  Widget build(BuildContext context) {
    final activeTasks = _filteredTasks.where((t) => !t.isCompleted).toList();

    return Row(
      children: [
        Expanded(
          child: Column(
            children: [
              _buildHeader(context, activeTasks.length),
              _buildFilters(context),
              Expanded(
                child: TaskListWidget(
                  tasks: activeTasks,
                  projects: widget.projects,
                  emptyMessage: 'No tasks found',
                  preserveOrder: true,
                  onTaskTap: _selectTask,
                  onTaskComplete: widget.onTaskComplete,
                ),
              ),
            ],
          ),
        ),

        // Detail panel
        if (_selectedTask != null)
          TaskDetail(
            task: _selectedTask!,
            project: _getProjectForTask(_selectedTask!),
            projects: widget.projects,
            onClose: () => setState(() => _selectedTask = null),
            onUpdate: (task) {
              widget.onTaskUpdate?.call(task);
              setState(() => _selectedTask = task);
            },
            onDelete: (task) {
              widget.onTaskDelete?.call(task);
              setState(() => _selectedTask = null);
            },
            onComplete: (task) {
              widget.onTaskComplete?.call(task);
              setState(() => _selectedTask = null);
            },
          ),
      ],
    );
  }

  Widget _buildHeader(BuildContext context, int taskCount) {
    // Get selected project name if a project is selected
    final selectedProject = widget.selectedProjectId != null
        ? widget.projects.where((p) => p.id == widget.selectedProjectId).firstOrNull
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

  Widget _buildFilters(BuildContext context) {
    final hasActiveFilters = _searchQuery.isNotEmpty || _hasActiveFilter;
    final isDark = Theme.of(context).brightness == Brightness.dark;

    // Theme-aware colors for filter controls - white with border for active look
    final filterBg = isDark ? AppTheme.gray800 : Colors.white;
    final filterBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;
    final textColor = isDark ? AppTheme.gray200 : AppTheme.gray900;

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
              child: TextField(
                onChanged: (value) => setState(() => _searchQuery = value),
                style: TextStyle(fontSize: 13, color: textColor),
                decoration: InputDecoration(
                  hintText: 'Search...',
                  hintStyle: TextStyle(fontSize: 13, color: isDark ? AppTheme.gray400 : AppTheme.gray500),
                  prefixIcon: Padding(
                    padding: const EdgeInsets.only(left: 10, right: 8),
                    child: Icon(LucideIcons.search, size: 16, color: isDark ? AppTheme.gray400 : AppTheme.gray500),
                  ),
                  prefixIconConstraints: const BoxConstraints(minWidth: 36),
                  isDense: true,
                  border: InputBorder.none,
                  enabledBorder: InputBorder.none,
                  focusedBorder: InputBorder.none,
                  contentPadding: const EdgeInsets.symmetric(vertical: 10),
                ),
              ),
            ),
          ),
          const SizedBox(width: 12),

          // Sort section with label
          Text(
            'Sort',
            style: TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w500,
              color: isDark ? AppTheme.gray400 : AppTheme.gray500,
            ),
          ),
          const SizedBox(width: 6),
          _buildSortDropdown(context),

          const SizedBox(width: 12),

          // Filter section with label
          Text(
            'Filter',
            style: TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w500,
              color: isDark ? AppTheme.gray400 : AppTheme.gray500,
            ),
          ),
          const SizedBox(width: 6),
          _buildDynamicFilter(context),

          // Clear filters button
          if (hasActiveFilters) ...[
            const SizedBox(width: 8),
            _buildClearFiltersButton(isDark),
          ],
        ],
      ),
    );
  }

  /// Build the appropriate filter dropdown based on current sort option
  Widget _buildDynamicFilter(BuildContext context) {
    switch (_sortOption) {
      case TaskSortOption.priority:
        return _buildPriorityFilterDropdown(context);
      case TaskSortOption.dueDate:
        return _buildDueDateFilterDropdown(context);
      case TaskSortOption.createdDate:
        return _buildCreatedDateFilterDropdown(context);
      case TaskSortOption.byLabel:
        return _buildLabelFilterDropdown(context);
      case TaskSortOption.byProject:
        return _buildProjectFilterDropdown(context);
    }
  }

  /// Priority filter dropdown
  Widget _buildPriorityFilterDropdown(BuildContext context) {
    final isActive = _priorityFilter != PriorityFilter.all;
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final priorityColor = _priorityFilter.value != null
        ? AppTheme.priorityColors[_priorityFilter.value] ?? AppTheme.gray500
        : AppTheme.gray500;

    final filterBg = isDark ? AppTheme.gray800 : Colors.white;
    final filterBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;

    return Container(
      height: 36,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: isActive ? priorityColor.withValues(alpha: 0.08) : filterBg,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(
          color: isActive ? priorityColor.withValues(alpha: 0.3) : filterBorder,
        ),
      ),
      child: DropdownButton<PriorityFilter>(
        value: _priorityFilter,
        selectedItemBuilder: (_) => PriorityFilter.values.map((filter) {
          if (filter == PriorityFilter.all) {
            return Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(LucideIcons.flag, size: 14, color: AppTheme.gray500),
                const SizedBox(width: 6),
                const Text('All', style: TextStyle(fontSize: 13)),
              ],
            );
          }
          return Row(
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
                  color: isActive && _priorityFilter == filter
                      ? AppTheme.priorityColors[filter.value]
                      : isDark ? AppTheme.gray300 : AppTheme.gray700,
                ),
              ),
            ],
          );
        }).toList(),
        items: PriorityFilter.values.map((filter) {
          if (filter == PriorityFilter.all) {
            return DropdownMenuItem(
              value: filter,
              child: Row(
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
        onChanged: (value) => setState(() => _priorityFilter = value ?? PriorityFilter.all),
        underline: const SizedBox.shrink(),
        isDense: true,
        icon: Icon(LucideIcons.chevronDown, size: 14, color: isActive ? priorityColor : AppTheme.gray400),
      ),
    );
  }

  /// Due date filter dropdown
  Widget _buildDueDateFilterDropdown(BuildContext context) {
    final isActive = _dueDateFilter != DueDateFilter.all;
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final activeColor = AppTheme.primaryBlue;

    final filterBg = isDark ? AppTheme.gray800 : Colors.white;
    final filterBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;

    return Container(
      height: 36,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: isActive ? activeColor.withValues(alpha: 0.08) : filterBg,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(
          color: isActive ? activeColor.withValues(alpha: 0.3) : filterBorder,
        ),
      ),
      child: DropdownButton<DueDateFilter>(
        value: _dueDateFilter,
        selectedItemBuilder: (_) => DueDateFilter.values.map((filter) {
          return Row(
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
          );
        }).toList(),
        items: DueDateFilter.values.map((filter) {
          return DropdownMenuItem(
            value: filter,
            child: Row(
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
        onChanged: (value) => setState(() => _dueDateFilter = value ?? DueDateFilter.all),
        underline: const SizedBox.shrink(),
        isDense: true,
        icon: Icon(LucideIcons.chevronDown, size: 14, color: isActive ? activeColor : AppTheme.gray400),
      ),
    );
  }

  /// Created date filter dropdown
  Widget _buildCreatedDateFilterDropdown(BuildContext context) {
    final isActive = _createdDateFilter != CreatedDateFilter.all;
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final activeColor = AppTheme.primaryBlue;

    final filterBg = isDark ? AppTheme.gray800 : Colors.white;
    final filterBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;

    return Container(
      height: 36,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: isActive ? activeColor.withValues(alpha: 0.08) : filterBg,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(
          color: isActive ? activeColor.withValues(alpha: 0.3) : filterBorder,
        ),
      ),
      child: DropdownButton<CreatedDateFilter>(
        value: _createdDateFilter,
        selectedItemBuilder: (_) => CreatedDateFilter.values.map((filter) {
          return Row(
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
          );
        }).toList(),
        items: CreatedDateFilter.values.map((filter) {
          return DropdownMenuItem(
            value: filter,
            child: Row(
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
        onChanged: (value) => setState(() => _createdDateFilter = value ?? CreatedDateFilter.all),
        underline: const SizedBox.shrink(),
        isDense: true,
        icon: Icon(LucideIcons.chevronDown, size: 14, color: isActive ? activeColor : AppTheme.gray400),
      ),
    );
  }

  /// Label filter dropdown - dynamically built from task labels
  Widget _buildLabelFilterDropdown(BuildContext context) {
    final isActive = _selectedLabelId != null;
    final isDark = Theme.of(context).brightness == Brightness.dark;

    // Extract unique labels from tasks
    final labelsMap = <String, ({String id, String name, String color})>{};
    for (final task in widget.tasks) {
      for (final label in task.labels) {
        labelsMap[label.id] = (id: label.id, name: label.name, color: label.color);
      }
    }
    final labels = labelsMap.values.toList()..sort((a, b) => a.name.compareTo(b.name));

    // Find selected label for color
    final selectedLabel = labels.where((l) => l.id == _selectedLabelId).firstOrNull;
    final labelColor = selectedLabel != null ? _parseColor(selectedLabel.color) : AppTheme.gray500;

    final filterBg = isDark ? AppTheme.gray800 : Colors.white;
    final filterBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;

    return Container(
      height: 36,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: isActive ? labelColor.withValues(alpha: 0.08) : filterBg,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(
          color: isActive ? labelColor.withValues(alpha: 0.3) : filterBorder,
        ),
      ),
      child: DropdownButton<String?>(
        value: _selectedLabelId,
        selectedItemBuilder: (_) => [
          // All option
          Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(LucideIcons.tag, size: 14, color: AppTheme.gray500),
              const SizedBox(width: 6),
              Text('All', style: TextStyle(fontSize: 13, color: isDark ? AppTheme.gray300 : AppTheme.gray700)),
            ],
          ),
          // Label options
          ...labels.map((label) => Row(
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
                  color: isActive && _selectedLabelId == label.id
                      ? labelColor
                      : isDark ? AppTheme.gray300 : AppTheme.gray700,
                ),
                overflow: TextOverflow.ellipsis,
              ),
            ],
          )),
        ],
        items: [
          DropdownMenuItem(
            value: null,
            child: Row(
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
                Flexible(
                  child: Text(
                    label.name,
                    style: const TextStyle(fontSize: 13),
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              ],
            ),
          )),
        ],
        onChanged: (value) => setState(() => _selectedLabelId = value),
        underline: const SizedBox.shrink(),
        isDense: true,
        icon: Icon(LucideIcons.chevronDown, size: 14, color: isActive ? labelColor : AppTheme.gray400),
      ),
    );
  }

  /// Project filter dropdown - dynamically built from available projects
  Widget _buildProjectFilterDropdown(BuildContext context) {
    final isActive = _selectedProjectId != null;
    final isDark = Theme.of(context).brightness == Brightness.dark;

    // Get projects that have tasks
    final projectsWithTasks = <String, ProjectEntity>{};
    for (final task in widget.tasks) {
      if (task.projectId != null) {
        final project = _getProjectForTask(task);
        if (project != null) {
          projectsWithTasks[project.id] = project;
        }
      }
    }
    final projects = projectsWithTasks.values.toList()..sort((a, b) => a.name.compareTo(b.name));

    // Find selected project for color
    final selectedProject = projects.where((p) => p.id == _selectedProjectId).firstOrNull;
    final projectColor = selectedProject != null ? _parseColor(selectedProject.color) : AppTheme.gray500;

    final filterBg = isDark ? AppTheme.gray800 : Colors.white;
    final filterBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;

    return Container(
      height: 36,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: isActive ? projectColor.withValues(alpha: 0.08) : filterBg,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(
          color: isActive ? projectColor.withValues(alpha: 0.3) : filterBorder,
        ),
      ),
      child: DropdownButton<String?>(
        value: _selectedProjectId,
        selectedItemBuilder: (_) => [
          // All option
          Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(LucideIcons.folder, size: 14, color: AppTheme.gray500),
              const SizedBox(width: 6),
              Text('All', style: TextStyle(fontSize: 13, color: isDark ? AppTheme.gray300 : AppTheme.gray700)),
            ],
          ),
          // Project options
          ...projects.map((project) => Row(
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
                  color: isActive && _selectedProjectId == project.id
                      ? projectColor
                      : isDark ? AppTheme.gray300 : AppTheme.gray700,
                ),
                overflow: TextOverflow.ellipsis,
              ),
            ],
          )),
        ],
        items: [
          DropdownMenuItem(
            value: null,
            child: Row(
              children: [
                Icon(LucideIcons.layers, size: 14, color: AppTheme.gray500),
                const SizedBox(width: 8),
                const Text('All Projects', style: TextStyle(fontSize: 13)),
              ],
            ),
          ),
          ...projects.map((project) => DropdownMenuItem(
            value: project.id,
            child: Row(
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
                Flexible(
                  child: Text(
                    project.name,
                    style: const TextStyle(fontSize: 13),
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              ],
            ),
          )),
        ],
        onChanged: (value) => setState(() => _selectedProjectId = value),
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

  Widget _buildClearFiltersButton(bool isDark) {
    return InkWell(
      onTap: () => setState(() {
        _searchQuery = '';
        _resetFilters();
      }),
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

  Widget _buildSortDropdown(BuildContext context) {
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
        value: _sortOption,
        selectedItemBuilder: (_) => TaskSortOption.values.map((option) {
          return Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(option.icon, size: 14, color: AppTheme.gray500),
              const SizedBox(width: 6),
              Text(option.displayName, style: const TextStyle(fontSize: 13)),
            ],
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
          if (value != null && value != _sortOption) {
            setState(() {
              _sortOption = value;
              _resetFilters(); // Auto-reset filter when sort changes
            });
          }
        },
        underline: const SizedBox.shrink(),
        isDense: true,
        icon: Icon(LucideIcons.chevronDown, size: 14, color: AppTheme.gray400),
      ),
    );
  }

  void _selectTask(TaskEntity task) {
    setState(() => _selectedTask = task);
  }

  ProjectEntity? _getProjectForTask(TaskEntity task) {
    if (task.projectId == null) return null;
    try {
      return widget.projects.firstWhere(
        (p) => p.id == task.projectId || p.id == 'todoist_${task.projectId}',
      );
    } catch (_) {
      return null;
    }
  }
}
