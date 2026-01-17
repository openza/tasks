import 'package:flutter/material.dart';
import 'package:lucide_icons/lucide_icons.dart';

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
