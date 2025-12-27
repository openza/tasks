import 'dart:isolate';

import '../../domain/entities/label.dart';
import '../../domain/entities/project.dart';
import '../../domain/entities/task.dart';

/// Data class for export operation (needed for isolate)
class ExportData {
  final List<TaskEntity> tasks;
  final List<ProjectEntity> projects;
  final List<LabelEntity> labels;

  ExportData({
    required this.tasks,
    required this.projects,
    required this.labels,
  });

  Map<String, dynamic> toJson() => {
        'tasks': tasks.map((t) => t.toJson()).toList(),
        'projects': projects.map((p) => p.toJson()).toList(),
        'labels': labels.map((l) => l.toJson()).toList(),
      };

  factory ExportData.fromJson(Map<String, dynamic> json) {
    return ExportData(
      tasks: (json['tasks'] as List)
          .map((t) => TaskEntity.fromJson(t as Map<String, dynamic>))
          .toList(),
      projects: (json['projects'] as List)
          .map((p) => ProjectEntity.fromJson(p as Map<String, dynamic>))
          .toList(),
      labels: (json['labels'] as List)
          .map((l) => LabelEntity.fromJson(l as Map<String, dynamic>))
          .toList(),
    );
  }
}

/// Service for exporting data to Markdown format
class MarkdownExporter {
  /// Export data to markdown format (runs in isolate for UI responsiveness)
  static Future<String> export({
    required List<TaskEntity> tasks,
    required List<ProjectEntity> projects,
    required List<LabelEntity> labels,
  }) async {
    // Run markdown generation in a separate isolate
    final exportData = ExportData(
      tasks: tasks,
      projects: projects,
      labels: labels,
    );

    return await Isolate.run(() => _generateMarkdown(exportData));
  }

  /// Generate markdown content (runs in isolate)
  static String _generateMarkdown(ExportData data) {
    final buffer = StringBuffer();

    // Header
    _writeHeader(buffer, data);

    // Projects & Tasks section
    _writeProjectsSection(buffer, data);

    // Labels section
    _writeLabelsSection(buffer, data);

    // Footer
    _writeFooter(buffer);

    return buffer.toString();
  }

  static void _writeHeader(StringBuffer buffer, ExportData data) {
    final now = DateTime.now();
    final activeTasks =
        data.tasks.where((t) => t.status != TaskStatus.completed).length;
    final completedTasks =
        data.tasks.where((t) => t.status == TaskStatus.completed).length;

    buffer.writeln('# Openza Tasks Export');
    buffer.writeln();
    buffer.writeln(
        'Export Date: ${now.year}-${now.month.toString().padLeft(2, '0')}-${now.day.toString().padLeft(2, '0')} ${now.hour.toString().padLeft(2, '0')}:${now.minute.toString().padLeft(2, '0')}');
    buffer.writeln(
        'Total Tasks: ${data.tasks.length} (Active: $activeTasks, Completed: $completedTasks)');
    buffer.writeln('Total Projects: ${data.projects.length}');
    buffer.writeln('Total Labels: ${data.labels.length}');
    buffer.writeln();
    buffer.writeln('---');
    buffer.writeln();
  }

  static void _writeProjectsSection(StringBuffer buffer, ExportData data) {
    buffer.writeln('## Projects & Tasks');
    buffer.writeln();

    // Group tasks by project
    final tasksByProject = <String?, List<TaskEntity>>{};

    for (final task in data.tasks) {
      tasksByProject.putIfAbsent(task.projectId, () => []).add(task);
    }

    // Create project lookup map
    final projectMap = <String, ProjectEntity>{};
    for (final project in data.projects) {
      projectMap[project.id] = project;
    }

    // Sort projects: favorites first, then alphabetically
    final sortedProjectIds = tasksByProject.keys.toList()
      ..sort((a, b) {
        final projA = a != null ? projectMap[a] : null;
        final projB = b != null ? projectMap[b] : null;

        // Null projects (no project assigned) go last
        if (projA == null && projB == null) return 0;
        if (projA == null) return 1;
        if (projB == null) return -1;

        // Favorites first
        if (projA.isFavorite && !projB.isFavorite) return -1;
        if (!projA.isFavorite && projB.isFavorite) return 1;

        // Then alphabetically
        return projA.name.compareTo(projB.name);
      });

    // Write each project with its tasks
    for (final projectId in sortedProjectIds) {
      final project = projectId != null ? projectMap[projectId] : null;
      final tasks = tasksByProject[projectId] ?? [];

      if (tasks.isEmpty) continue;

      // Project header
      final projectName = project?.name ?? 'No Project';
      final providerSuffix = _getProviderSuffix(project?.integrationId);

      buffer.writeln('### $projectName$providerSuffix');
      buffer.writeln();

      // Sort tasks: pending first, then by priority, then by due date
      tasks.sort((a, b) {
        // Completed tasks last
        if (a.isCompleted && !b.isCompleted) return 1;
        if (!a.isCompleted && b.isCompleted) return -1;

        // By priority (lower number = higher priority)
        final priorityCompare = a.priority.compareTo(b.priority);
        if (priorityCompare != 0) return priorityCompare;

        // By due date
        if (a.dueDate != null && b.dueDate != null) {
          return a.dueDate!.compareTo(b.dueDate!);
        }
        if (a.dueDate != null) return -1;
        if (b.dueDate != null) return 1;

        return 0;
      });

      // Write tasks
      for (final task in tasks) {
        _writeTask(buffer, task);
      }

      buffer.writeln();
    }
  }

  static void _writeTask(StringBuffer buffer, TaskEntity task) {
    // Checkbox
    final checkbox = task.isCompleted ? '[x]' : '[ ]';

    // Title (escape markdown special chars)
    final title = _escapeMarkdown(task.title);

    // Metadata
    final metadata = <String>[];

    // Priority (only show if not default)
    if (task.priority != 4) {
      final priorityName = switch (task.priority) {
        1 => 'High',
        2 => 'Medium',
        3 => 'Low',
        _ => null,
      };
      if (priorityName != null) {
        metadata.add('Priority: $priorityName');
      }
    }

    // Due date
    if (task.dueDate != null) {
      final date = task.dueDate!;
      final dateStr =
          '${date.year}-${date.month.toString().padLeft(2, '0')}-${date.day.toString().padLeft(2, '0')}';
      metadata.add('Due: $dateStr');
    }

    // Labels as tags
    final labelTags = task.labels
        .map((l) => '@${l.name.replaceAll(' ', '_')}')
        .join(' ');

    // Build task line
    buffer.write('- $checkbox $title');

    if (metadata.isNotEmpty) {
      buffer.write(' (${metadata.join(', ')})');
    }

    if (labelTags.isNotEmpty) {
      buffer.write(' $labelTags');
    }

    buffer.writeln();

    // Description as sub-item if present
    if (task.description != null && task.description!.isNotEmpty) {
      final escapedDesc = _escapeMarkdown(task.description!);
      buffer.writeln('  - _${escapedDesc}_');
    }
  }

  static void _writeLabelsSection(StringBuffer buffer, ExportData data) {
    if (data.labels.isEmpty) return;

    buffer.writeln('---');
    buffer.writeln();
    buffer.writeln('## Labels');
    buffer.writeln();
    buffer.writeln('| Name | Color | Source |');
    buffer.writeln('|------|-------|--------|');

    // Sort labels alphabetically
    final sortedLabels = data.labels.toList()
      ..sort((a, b) => a.name.compareTo(b.name));

    for (final label in sortedLabels) {
      final name = _escapeMarkdown(label.name);
      final color = label.color;
      final source = _getSourceName(label.integrationId);

      buffer.writeln('| $name | $color | $source |');
    }

    buffer.writeln();
  }

  static void _writeFooter(StringBuffer buffer) {
    buffer.writeln('---');
    buffer.writeln();
    buffer.writeln('*Generated by Openza Tasks*');
  }

  /// Escape markdown special characters
  static String _escapeMarkdown(String text) {
    return text
        .replaceAll('\\', '\\\\')
        .replaceAll('*', '\\*')
        .replaceAll('_', '\\_')
        .replaceAll('`', '\\`')
        .replaceAll('[', '\\[')
        .replaceAll(']', '\\]')
        .replaceAll('|', '\\|');
  }

  /// Get provider suffix for project header
  static String _getProviderSuffix(String? integrationId) {
    if (integrationId == null || integrationId == 'openza_tasks') {
      return '';
    }
    return ' (${_getSourceName(integrationId)})';
  }

  /// Get human-readable source name
  static String _getSourceName(String integrationId) {
    return switch (integrationId) {
      'openza_tasks' => 'Openza Tasks',
      'todoist' => 'Todoist',
      'msToDo' => 'MS To-Do',
      _ => integrationId,
    };
  }

  /// Get statistics for preview
  static ExportStats getStats({
    required List<TaskEntity> tasks,
    required List<ProjectEntity> projects,
    required List<LabelEntity> labels,
  }) {
    final activeTasks =
        tasks.where((t) => t.status != TaskStatus.completed).length;
    final completedTasks =
        tasks.where((t) => t.status == TaskStatus.completed).length;

    return ExportStats(
      totalTasks: tasks.length,
      activeTasks: activeTasks,
      completedTasks: completedTasks,
      totalProjects: projects.length,
      totalLabels: labels.length,
    );
  }
}

/// Statistics for export preview
class ExportStats {
  final int totalTasks;
  final int activeTasks;
  final int completedTasks;
  final int totalProjects;
  final int totalLabels;

  ExportStats({
    required this.totalTasks,
    required this.activeTasks,
    required this.completedTasks,
    required this.totalProjects,
    required this.totalLabels,
  });
}
