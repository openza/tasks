import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';

class TasksScreen extends ConsumerStatefulWidget {
  final String? projectId;

  const TasksScreen({super.key, this.projectId});

  @override
  ConsumerState<TasksScreen> createState() => _TasksScreenState();
}

class _TasksScreenState extends ConsumerState<TasksScreen>
    with SingleTickerProviderStateMixin {
  late TabController _tabController;
  final List<String> _openTabs = ['Tasks']; // Main tab always open
  int _currentTabIndex = 0;
  String _sortBy = 'priority';

  @override
  void initState() {
    super.initState();
    _tabController = TabController(length: _openTabs.length, vsync: this);
  }

  @override
  void dispose() {
    _tabController.dispose();
    super.dispose();
  }

  void _openTaskTab(String taskId, String taskTitle) {
    if (!_openTabs.contains(taskTitle)) {
      setState(() {
        _openTabs.add(taskTitle);
        _tabController = TabController(length: _openTabs.length, vsync: this);
        _currentTabIndex = _openTabs.length - 1;
        _tabController.index = _currentTabIndex;
      });
    } else {
      final index = _openTabs.indexOf(taskTitle);
      setState(() {
        _currentTabIndex = index;
        _tabController.index = index;
      });
    }
  }

  void _closeTab(int index) {
    if (index == 0) return; // Can't close main Tasks tab
    setState(() {
      _openTabs.removeAt(index);
      _tabController = TabController(length: _openTabs.length, vsync: this);
      if (_currentTabIndex >= _openTabs.length) {
        _currentTabIndex = _openTabs.length - 1;
      }
      _tabController.index = _currentTabIndex;
    });
  }

  String get _title {
    if (widget.projectId != null) {
      // TODO: Get project name from provider
      return widget.projectId!.replaceFirst(widget.projectId![0], widget.projectId![0].toUpperCase());
    }
    return 'All Tasks';
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      color: AppTheme.gray50,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Header with tabs
          Container(
            decoration: BoxDecoration(
              color: Theme.of(context).colorScheme.surface,
              border: Border(
                bottom: BorderSide(color: Theme.of(context).dividerColor),
              ),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // Title and sort
                Padding(
                  padding: const EdgeInsets.fromLTRB(24, 24, 24, 0),
                  child: Row(
                    children: [
                      const Icon(LucideIcons.listTodo, size: 24),
                      const SizedBox(width: 12),
                      Text(
                        _title,
                        style: Theme.of(context).textTheme.headlineSmall,
                      ),
                      const Spacer(),
                      // Sort dropdown
                      if (_currentTabIndex == 0)
                        PopupMenuButton<String>(
                          initialValue: _sortBy,
                          onSelected: (value) => setState(() => _sortBy = value),
                          itemBuilder: (context) => [
                            const PopupMenuItem(
                              value: 'priority',
                              child: Text('Sort by Priority'),
                            ),
                            const PopupMenuItem(
                              value: 'project',
                              child: Text('Sort by Project'),
                            ),
                            const PopupMenuItem(
                              value: 'labels',
                              child: Text('Sort by Labels'),
                            ),
                          ],
                          child: Container(
                            padding: const EdgeInsets.symmetric(
                                horizontal: 12, vertical: 8),
                            decoration: BoxDecoration(
                              border: Border.all(color: AppTheme.gray300),
                              borderRadius: BorderRadius.circular(8),
                            ),
                            child: Row(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                const Icon(LucideIcons.arrowUpDown, size: 16),
                                const SizedBox(width: 8),
                                Text(
                                  'Sort: ${_sortBy[0].toUpperCase()}${_sortBy.substring(1)}',
                                  style: Theme.of(context).textTheme.bodySmall,
                                ),
                              ],
                            ),
                          ),
                        ),
                    ],
                  ),
                ),

                // Tabs
                if (_openTabs.length > 1)
                  Padding(
                    padding: const EdgeInsets.fromLTRB(16, 16, 16, 0),
                    child: SizedBox(
                      height: 40,
                      child: ListView.builder(
                        scrollDirection: Axis.horizontal,
                        itemCount: _openTabs.length,
                        itemBuilder: (context, index) {
                          final isActive = _currentTabIndex == index;
                          return Padding(
                            padding: const EdgeInsets.only(right: 4),
                            child: Material(
                              color: isActive
                                  ? AppTheme.primaryBlue.withValues(alpha: 0.1)
                                  : Colors.transparent,
                              borderRadius: const BorderRadius.vertical(
                                top: Radius.circular(8),
                              ),
                              child: InkWell(
                                onTap: () => setState(() {
                                  _currentTabIndex = index;
                                  _tabController.index = index;
                                }),
                                borderRadius: const BorderRadius.vertical(
                                  top: Radius.circular(8),
                                ),
                                child: Container(
                                  padding: const EdgeInsets.symmetric(
                                      horizontal: 12, vertical: 8),
                                  constraints:
                                      const BoxConstraints(maxWidth: 150),
                                  child: Row(
                                    mainAxisSize: MainAxisSize.min,
                                    children: [
                                      Flexible(
                                        child: Text(
                                          _openTabs[index],
                                          overflow: TextOverflow.ellipsis,
                                          style: TextStyle(
                                            fontSize: 13,
                                            fontWeight: isActive
                                                ? FontWeight.w600
                                                : FontWeight.w400,
                                            color: isActive
                                                ? AppTheme.primaryBlue
                                                : AppTheme.gray600,
                                          ),
                                        ),
                                      ),
                                      if (index > 0) ...[
                                        const SizedBox(width: 4),
                                        InkWell(
                                          onTap: () => _closeTab(index),
                                          child: const Icon(
                                            LucideIcons.x,
                                            size: 14,
                                            color: AppTheme.gray400,
                                          ),
                                        ),
                                      ],
                                    ],
                                  ),
                                ),
                              ),
                            ),
                          );
                        },
                      ),
                    ),
                  )
                else
                  const SizedBox(height: 16),
              ],
            ),
          ),

          // Content
          Expanded(
            child: IndexedStack(
              index: _currentTabIndex,
              children: _openTabs.asMap().entries.map((entry) {
                if (entry.key == 0) {
                  return _buildTaskList(context);
                }
                return _buildTaskDetail(context, entry.value);
              }).toList(),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildTaskList(BuildContext context) {
    return ListView.builder(
      padding: const EdgeInsets.all(16),
      itemCount: 10,
      itemBuilder: (context, index) {
        final taskTitle = 'Task ${index + 1}';
        return Card(
          margin: const EdgeInsets.only(bottom: 8),
          child: ListTile(
            leading: Checkbox(
              value: false,
              onChanged: (value) {},
            ),
            title: Text(taskTitle),
            subtitle: Text(
              'This is a placeholder task description',
              style: Theme.of(context).textTheme.bodySmall,
            ),
            onTap: () => _openTaskTab('task_$index', taskTitle),
            trailing: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                  decoration: BoxDecoration(
                    color: AppTheme.primaryBlue.withValues(alpha: 0.1),
                    borderRadius: BorderRadius.circular(4),
                  ),
                  child: Text(
                    'Work',
                    style: TextStyle(
                      fontSize: 11,
                      color: AppTheme.primaryBlue,
                      fontWeight: FontWeight.w500,
                    ),
                  ),
                ),
              ],
            ),
          ),
        );
      },
    );
  }

  Widget _buildTaskDetail(BuildContext context, String taskTitle) {
    return SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: Card(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                taskTitle,
                style: Theme.of(context).textTheme.headlineSmall,
              ),
              const SizedBox(height: 16),
              Text(
                'This is a detailed description of the task. It can contain multiple lines and provide more context about what needs to be done.',
                style: Theme.of(context).textTheme.bodyMedium,
              ),
              const SizedBox(height: 24),
              const Divider(),
              const SizedBox(height: 16),
              _DetailRow(label: 'Due Date', value: 'Tomorrow'),
              _DetailRow(label: 'Project', value: 'Work'),
              _DetailRow(label: 'Priority', value: 'High'),
              _DetailRow(label: 'Status', value: 'Active'),
              _DetailRow(label: 'Created', value: 'Dec 10, 2025'),
            ],
          ),
        ),
      ),
    );
  }
}

class _DetailRow extends StatelessWidget {
  final String label;
  final String value;

  const _DetailRow({required this.label, required this.value});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        children: [
          SizedBox(
            width: 100,
            child: Text(
              label,
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ),
          Expanded(
            child: Text(
              value,
              style: Theme.of(context).textTheme.bodyMedium,
            ),
          ),
        ],
      ),
    );
  }
}
