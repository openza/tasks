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
  int? _selectedPriority;
  TaskSortOption _sortOption = TaskSortOption.priority;

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

    // Apply priority filter
    if (_selectedPriority != null) {
      tasks = tasks.where((t) => t.priority == _selectedPriority).toList();
    }

    // Apply sorting
    return _sortTasks(tasks);
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
                color: _parseProjectColor(selectedProject.color),
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
              '$taskCount active',
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

  Color _parseProjectColor(String colorStr) {
    if (colorStr.startsWith('#')) {
      try {
        return Color(int.parse(colorStr.substring(1), radix: 16) + 0xFF000000);
      } catch (_) {
        return AppTheme.gray500;
      }
    }
    return AppTheme.gray500;
  }

  Widget _buildFilters(BuildContext context) {
    final hasActiveFilters = _searchQuery.isNotEmpty || _selectedPriority != null;
    final isDark = Theme.of(context).brightness == Brightness.dark;

    // Theme-aware colors for filter controls - use gray100 for more contrast
    final filterBg = isDark ? AppTheme.gray800 : AppTheme.gray100;
    final filterBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;
    final textColor = isDark ? AppTheme.gray200 : AppTheme.gray900;

    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
      child: Row(
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
                  contentPadding: const EdgeInsets.symmetric(vertical: 10),
                ),
              ),
            ),
          ),
          const SizedBox(width: 8),

          // Sort dropdown
          _buildSortDropdown(context),
          const SizedBox(width: 8),

          // Priority filter
          _buildPriorityFilter(context),

          // Clear filters
          if (hasActiveFilters) ...[
            const SizedBox(width: 6),
            _buildClearFiltersButton(),
          ],
        ],
      ),
    );
  }

  Widget _buildPriorityFilter(BuildContext context) {
    final isActive = _selectedPriority != null;
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final priorityColor = _selectedPriority != null
        ? AppTheme.priorityColors[_selectedPriority] ?? AppTheme.gray500
        : AppTheme.gray500;

    // Theme-aware colors - gray100 for better contrast
    final filterBg = isDark ? AppTheme.gray800 : AppTheme.gray100;
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
      child: DropdownButton<int?>(
        value: _selectedPriority,
        hint: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(LucideIcons.flag, size: 14, color: isDark ? AppTheme.gray400 : AppTheme.gray500),
            const SizedBox(width: 6),
            Text('Priority', style: TextStyle(fontSize: 13, color: isDark ? AppTheme.gray400 : AppTheme.gray600)),
          ],
        ),
        selectedItemBuilder: (_) => [
          // "All" option
          Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(LucideIcons.flag, size: 14, color: AppTheme.gray500),
              const SizedBox(width: 6),
              const Text('All', style: TextStyle(fontSize: 13)),
            ],
          ),
          // Priority options
          ...[1, 2, 3, 4].map((p) => Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Container(
                width: 8,
                height: 8,
                decoration: BoxDecoration(
                  color: AppTheme.priorityColors[p],
                  shape: BoxShape.circle,
                ),
              ),
              const SizedBox(width: 6),
              Text(
                _getPriorityLabel(p),
                style: TextStyle(
                  fontSize: 13,
                  color: isActive && _selectedPriority == p
                      ? AppTheme.priorityColors[p]
                      : isDark ? AppTheme.gray300 : AppTheme.gray700,
                ),
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
                const Text('All Priorities', style: TextStyle(fontSize: 13)),
              ],
            ),
          ),
          ...[1, 2, 3, 4].map((p) => DropdownMenuItem(
            value: p,
            child: Row(
              children: [
                Container(
                  width: 10,
                  height: 10,
                  decoration: BoxDecoration(
                    color: AppTheme.priorityColors[p],
                    shape: BoxShape.circle,
                  ),
                ),
                const SizedBox(width: 8),
                Text(_getPriorityLabel(p), style: const TextStyle(fontSize: 13)),
              ],
            ),
          )),
        ],
        onChanged: (value) => setState(() => _selectedPriority = value),
        underline: const SizedBox.shrink(),
        isDense: true,
        icon: Icon(LucideIcons.chevronDown, size: 14, color: isActive ? priorityColor : AppTheme.gray400),
      ),
    );
  }

  String _getPriorityLabel(int priority) {
    switch (priority) {
      case 1: return 'Urgent';
      case 2: return 'High';
      case 3: return 'Normal';
      case 4: return 'Low';
      default: return '';
    }
  }

  Widget _buildClearFiltersButton() {
    return Tooltip(
      message: 'Clear all filters',
      child: InkWell(
        onTap: () => setState(() {
          _searchQuery = '';
          _selectedPriority = null;
        }),
        borderRadius: BorderRadius.circular(6),
        child: Container(
          height: 36,
          width: 36,
          decoration: BoxDecoration(
            color: AppTheme.errorRed.withValues(alpha: 0.08),
            borderRadius: BorderRadius.circular(6),
            border: Border.all(color: AppTheme.errorRed.withValues(alpha: 0.2)),
          ),
          child: Icon(LucideIcons.x, size: 16, color: AppTheme.errorRed),
        ),
      ),
    );
  }

  Widget _buildSortDropdown(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final filterBg = isDark ? AppTheme.gray800 : AppTheme.gray100;
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
          if (value != null) {
            setState(() => _sortOption = value);
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
