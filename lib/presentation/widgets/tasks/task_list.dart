import 'package:flutter/material.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import 'task_card.dart';

enum TaskFilter { all, today, overdue, labeled, project }

/// Widget for displaying a list of tasks
class TaskListWidget extends StatelessWidget {
  final List<TaskEntity> tasks;
  final List<ProjectEntity> projects;
  final TaskFilter filter;
  final String? projectId;
  final String? title;
  final String? emptyMessage;
  final bool sortByLabels;
  final bool sortByProject;
  final bool preserveOrder;
  final bool showGradientBackground;
  final void Function(TaskEntity)? onTaskTap;
  final void Function(TaskEntity)? onTaskComplete;
  final String? selectedTaskId;

  const TaskListWidget({
    super.key,
    required this.tasks,
    this.projects = const [],
    this.filter = TaskFilter.all,
    this.projectId,
    this.title,
    this.emptyMessage,
    this.sortByLabels = false,
    this.sortByProject = false,
    this.preserveOrder = false,
    this.showGradientBackground = false,
    this.onTaskTap,
    this.onTaskComplete,
    this.selectedTaskId,
  });

  @override
  Widget build(BuildContext context) {
    final filteredTasks = _filterTasks();
    final sortedTasks = _sortTasks(filteredTasks);

    if (showGradientBackground) {
      return Container(
        decoration: const BoxDecoration(
          gradient: LinearGradient(
            begin: Alignment.topLeft,
            end: Alignment.bottomRight,
            colors: [
              Color(0xFFEFF6FF), // blue-50
              Color(0xFFFDF2F8), // pink-50
              Color(0xFFF3E8FF), // purple-100
            ],
          ),
        ),
        child: _buildContent(context, sortedTasks),
      );
    }

    return _buildContent(context, sortedTasks);
  }

  Widget _buildContent(BuildContext context, List<TaskEntity> sortedTasks) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // Title
        if (title != null)
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
            child: Text(
              title!,
              style: Theme.of(context).textTheme.headlineSmall,
            ),
          ),

        // Task list or empty state
        Expanded(
          child: sortedTasks.isEmpty
              ? _buildEmptyState(context)
              : ListView.builder(
                  padding: const EdgeInsets.all(16),
                  itemCount: sortedTasks.length,
                  itemBuilder: (context, index) {
                    final task = sortedTasks[index];
                    final project = _getProjectForTask(task);

                    return TaskCard(
                      task: task,
                      project: project,
                      onTap: onTaskTap != null ? () => onTaskTap!(task) : null,
                      onComplete: onTaskComplete != null
                          ? () => onTaskComplete!(task)
                          : null,
                      showProject: filter != TaskFilter.project,
                      isSelected: selectedTaskId == task.id,
                    );
                  },
                ),
        ),
      ],
    );
  }

  Widget _buildEmptyState(BuildContext context) {
    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(
            Icons.check_circle_outline,
            size: 64,
            color: AppTheme.gray300,
          ),
          const SizedBox(height: 16),
          Text(
            emptyMessage ?? 'No tasks found',
            style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                  color: AppTheme.gray500,
                ),
          ),
        ],
      ),
    );
  }

  List<TaskEntity> _filterTasks() {
    switch (filter) {
      case TaskFilter.today:
        return tasks.where((t) => t.isDueToday && !t.isCompleted).toList();
      case TaskFilter.overdue:
        return tasks.where((t) => t.isOverdue && !t.isCompleted).toList();
      case TaskFilter.labeled:
        return tasks.where((t) => t.hasLabels && !t.isCompleted).toList();
      case TaskFilter.project:
        if (projectId == null) return tasks;
        return tasks.where((t) => t.projectId == projectId).toList();
      case TaskFilter.all:
      default:
        return tasks.where((t) => !t.isCompleted).toList();
    }
  }

  List<TaskEntity> _sortTasks(List<TaskEntity> tasksToSort) {
    // If preserveOrder is true, return tasks as-is (parent already sorted)
    if (preserveOrder) {
      return tasksToSort;
    }

    final sorted = List<TaskEntity>.from(tasksToSort);

    if (sortByLabels) {
      sorted.sort((a, b) {
        // Tasks with labels first
        if (a.hasLabels && !b.hasLabels) return -1;
        if (!a.hasLabels && b.hasLabels) return 1;
        // Then by first label name
        if (a.hasLabels && b.hasLabels) {
          return a.labels.first.name.compareTo(b.labels.first.name);
        }
        return 0;
      });
    } else if (sortByProject) {
      sorted.sort((a, b) {
        final projectA = _getProjectForTask(a);
        final projectB = _getProjectForTask(b);
        if (projectA == null && projectB == null) return 0;
        if (projectA == null) return 1;
        if (projectB == null) return -1;
        return projectA.name.compareTo(projectB.name);
      });
    } else {
      // Default sort: priority > due date > created
      sorted.sort((a, b) {
        // Priority first
        final priorityCompare = a.priority.compareTo(b.priority);
        if (priorityCompare != 0) return priorityCompare;

        // Due date (nulls last)
        if (a.dueDate != null && b.dueDate != null) {
          return a.dueDate!.compareTo(b.dueDate!);
        }
        if (a.dueDate != null) return -1;
        if (b.dueDate != null) return 1;

        // Created date
        return b.createdAt.compareTo(a.createdAt);
      });
    }

    return sorted;
  }

  ProjectEntity? _getProjectForTask(TaskEntity task) {
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
