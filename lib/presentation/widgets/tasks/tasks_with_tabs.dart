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
  final void Function(TaskEntity)? onTaskComplete;
  final void Function(TaskEntity)? onTaskUpdate;
  final void Function(TaskEntity)? onTaskDelete;

  const TasksWithTabs({
    super.key,
    required this.tasks,
    this.projects = const [],
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
  String? _selectedProjectId;
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

    // Apply project filter
    if (_selectedProjectId != null) {
      tasks = tasks.where((t) => t.projectId == _selectedProjectId).toList();
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
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 0),
      child: Row(
        children: [
          Text(
            'Tasks',
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

  Widget _buildFilters(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(16),
      child: Row(
        children: [
          // Search
          Expanded(
            flex: 2,
            child: TextField(
              onChanged: (value) => setState(() => _searchQuery = value),
              decoration: InputDecoration(
                hintText: 'Search tasks...',
                prefixIcon: Icon(LucideIcons.search, size: 18),
                isDense: true,
                border: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(8),
                  borderSide: BorderSide(color: AppTheme.gray300),
                ),
                contentPadding: const EdgeInsets.symmetric(
                  horizontal: 12,
                  vertical: 10,
                ),
              ),
            ),
          ),
          const SizedBox(width: 12),

          // Sort dropdown
          _buildSortDropdown(),
          const SizedBox(width: 12),

          // Project filter
          if (widget.projects.isNotEmpty)
            Expanded(
              child: _buildDropdown<String?>(
                value: _selectedProjectId,
                hint: 'Project',
                items: [
                  const DropdownMenuItem(value: null, child: Text('All Projects')),
                  ...widget.projects.map((p) => DropdownMenuItem(
                        value: p.id,
                        child: Text(p.name, overflow: TextOverflow.ellipsis),
                      )),
                ],
                onChanged: (value) =>
                    setState(() => _selectedProjectId = value),
              ),
            ),
          const SizedBox(width: 12),

          // Priority filter
          Expanded(
            child: _buildDropdown<int?>(
              value: _selectedPriority,
              hint: 'Priority',
              items: const [
                DropdownMenuItem(value: null, child: Text('All Priorities')),
                DropdownMenuItem(value: 1, child: Text('High')),
                DropdownMenuItem(value: 2, child: Text('Medium')),
                DropdownMenuItem(value: 3, child: Text('Normal')),
                DropdownMenuItem(value: 4, child: Text('Low')),
              ],
              onChanged: (value) => setState(() => _selectedPriority = value),
            ),
          ),

          // Clear filters
          if (_searchQuery.isNotEmpty ||
              _selectedProjectId != null ||
              _selectedPriority != null) ...[
            const SizedBox(width: 12),
            IconButton(
              icon: Icon(LucideIcons.x, size: 18, color: AppTheme.gray500),
              onPressed: () => setState(() {
                _searchQuery = '';
                _selectedProjectId = null;
                _selectedPriority = null;
              }),
              tooltip: 'Clear filters',
            ),
          ],
        ],
      ),
    );
  }

  Widget _buildSortDropdown() {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12),
      decoration: BoxDecoration(
        border: Border.all(color: AppTheme.gray300),
        borderRadius: BorderRadius.circular(8),
      ),
      child: DropdownButton<TaskSortOption>(
        value: _sortOption,
        items: TaskSortOption.values.map((option) {
          return DropdownMenuItem<TaskSortOption>(
            value: option,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(option.icon, size: 16, color: AppTheme.gray600),
                const SizedBox(width: 8),
                Text(option.displayName),
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
        icon: Icon(LucideIcons.chevronDown, size: 16, color: AppTheme.gray400),
      ),
    );
  }

  Widget _buildDropdown<T>({
    required T value,
    required String hint,
    required List<DropdownMenuItem<T>> items,
    required ValueChanged<T?> onChanged,
  }) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12),
      decoration: BoxDecoration(
        border: Border.all(color: AppTheme.gray300),
        borderRadius: BorderRadius.circular(8),
      ),
      child: DropdownButton<T>(
        value: value,
        hint: Text(hint, style: TextStyle(color: AppTheme.gray500)),
        items: items,
        onChanged: onChanged,
        isExpanded: true,
        underline: const SizedBox.shrink(),
        icon: Icon(LucideIcons.chevronDown, size: 16, color: AppTheme.gray400),
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
