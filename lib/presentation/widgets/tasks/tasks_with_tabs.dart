import 'package:flutter/material.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import 'task_list.dart';
import 'task_detail.dart';

/// Widget displaying tasks organized in tabs (All, Active, Completed)
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

class _TasksWithTabsState extends State<TasksWithTabs>
    with SingleTickerProviderStateMixin {
  late TabController _tabController;
  TaskEntity? _selectedTask;
  String _searchQuery = '';
  String? _selectedProjectId;
  int? _selectedPriority;

  @override
  void initState() {
    super.initState();
    _tabController = TabController(length: 3, vsync: this);
  }

  @override
  void dispose() {
    _tabController.dispose();
    super.dispose();
  }

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

    return tasks;
  }

  List<TaskEntity> get _activeTasks =>
      _filteredTasks.where((t) => !t.isCompleted).toList();

  List<TaskEntity> get _completedTasks =>
      _filteredTasks.where((t) => t.isCompleted).toList();

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Expanded(
          child: Column(
            children: [
              _buildHeader(context),
              _buildFilters(context),
              _buildTabBar(context),
              Expanded(
                child: TabBarView(
                  controller: _tabController,
                  children: [
                    // All tasks
                    TaskListWidget(
                      tasks: _filteredTasks,
                      projects: widget.projects,
                      emptyMessage: 'No tasks found',
                      onTaskTap: _selectTask,
                      onTaskComplete: widget.onTaskComplete,
                    ),
                    // Active tasks
                    TaskListWidget(
                      tasks: _activeTasks,
                      projects: widget.projects,
                      emptyMessage: 'No active tasks',
                      onTaskTap: _selectTask,
                      onTaskComplete: widget.onTaskComplete,
                    ),
                    // Completed tasks
                    TaskListWidget(
                      tasks: _completedTasks,
                      projects: widget.projects,
                      emptyMessage: 'No completed tasks',
                      onTaskTap: _selectTask,
                      onTaskComplete: widget.onTaskComplete,
                    ),
                  ],
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
              // Update selected task state
              setState(() {
                _selectedTask = task.copyWith(
                  status: task.isCompleted
                      ? TaskStatus.pending
                      : TaskStatus.completed,
                  completedAt: task.isCompleted ? null : DateTime.now(),
                );
              });
            },
          ),
      ],
    );
  }

  Widget _buildHeader(BuildContext context) {
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
          const Spacer(),
          _buildStatsRow(context),
        ],
      ),
    );
  }

  Widget _buildStatsRow(BuildContext context) {
    final active = _activeTasks.length;
    final completed = _completedTasks.length;
    final total = _filteredTasks.length;

    return Row(
      children: [
        _buildStatBadge(context, 'Total', total, AppTheme.gray500),
        const SizedBox(width: 8),
        _buildStatBadge(context, 'Active', active, AppTheme.primaryBlue),
        const SizedBox(width: 8),
        _buildStatBadge(context, 'Done', completed, AppTheme.successGreen),
      ],
    );
  }

  Widget _buildStatBadge(
      BuildContext context, String label, int count, Color color) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            count.toString(),
            style: TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w600,
              color: color,
            ),
          ),
          const SizedBox(width: 4),
          Text(
            label,
            style: TextStyle(
              fontSize: 11,
              color: color,
            ),
          ),
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

  Widget _buildTabBar(BuildContext context) {
    return Container(
      margin: const EdgeInsets.symmetric(horizontal: 16),
      decoration: BoxDecoration(
        color: AppTheme.gray100,
        borderRadius: BorderRadius.circular(8),
      ),
      child: TabBar(
        controller: _tabController,
        indicator: BoxDecoration(
          color: Colors.white,
          borderRadius: BorderRadius.circular(6),
          boxShadow: [
            BoxShadow(
              color: Colors.black.withValues(alpha: 0.05),
              blurRadius: 2,
              offset: const Offset(0, 1),
            ),
          ],
        ),
        indicatorSize: TabBarIndicatorSize.tab,
        labelColor: AppTheme.gray900,
        unselectedLabelColor: AppTheme.gray500,
        labelStyle: const TextStyle(
          fontSize: 13,
          fontWeight: FontWeight.w500,
        ),
        dividerColor: Colors.transparent,
        tabs: [
          Tab(
            child: Row(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                const Text('All'),
                const SizedBox(width: 6),
                _buildTabBadge(_filteredTasks.length),
              ],
            ),
          ),
          Tab(
            child: Row(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                const Text('Active'),
                const SizedBox(width: 6),
                _buildTabBadge(_activeTasks.length),
              ],
            ),
          ),
          Tab(
            child: Row(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                const Text('Completed'),
                const SizedBox(width: 6),
                _buildTabBadge(_completedTasks.length),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildTabBadge(int count) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
      decoration: BoxDecoration(
        color: AppTheme.gray200,
        borderRadius: BorderRadius.circular(10),
      ),
      child: Text(
        count.toString(),
        style: TextStyle(
          fontSize: 10,
          fontWeight: FontWeight.w600,
          color: AppTheme.gray600,
        ),
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
