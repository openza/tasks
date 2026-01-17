import 'package:flutter_riverpod/flutter_riverpod.dart';
// ignore: deprecated_member_use
import 'package:flutter_riverpod/legacy.dart';

import '../../domain/entities/project.dart';
import '../../domain/entities/task.dart';
import '../models/task_ui_state.dart';
import 'task_provider.dart';

// =============================================================================
// SELECTION STATE PROVIDERS
// =============================================================================

/// Currently selected project ID - null means "All Projects" (no filter)
final selectedProjectIdProvider = StateProvider<String?>((ref) => null);

/// Currently selected task ID for detail view - persists across data refreshes
final selectedTaskIdProvider = StateProvider<String?>((ref) => null);

/// Get the currently selected task entity by looking up the ID in unified data
/// This derived provider automatically updates when either the task ID or data changes
final selectedTaskProvider = Provider<TaskEntity?>((ref) {
  final taskId = ref.watch(selectedTaskIdProvider);
  if (taskId == null) return null;

  final data = ref.watch(unifiedDataProvider).value;
  if (data == null) return null;

  return data.tasks.where((t) => t.id == taskId).firstOrNull;
});

// =============================================================================
// TASKS VIEW UI STATE PROVIDERS
// These persist across data refreshes (sync, new task, etc.)
// =============================================================================

/// Search query for task filtering
final tasksSearchQueryProvider = StateProvider<String>((ref) => '');

/// Current sort option for tasks
final tasksSortOptionProvider = StateProvider<TaskSortOption>(
  (ref) => TaskSortOption.priority,
);

/// Priority filter for tasks
final tasksPriorityFilterProvider = StateProvider<PriorityFilter>(
  (ref) => PriorityFilter.all,
);

/// Due date filter for tasks
final tasksDueDateFilterProvider = StateProvider<DueDateFilter>(
  (ref) => DueDateFilter.all,
);

/// Created date filter for tasks
final tasksCreatedDateFilterProvider = StateProvider<CreatedDateFilter>(
  (ref) => CreatedDateFilter.all,
);

/// Selected label ID for filtering (when sorted by label)
final tasksSelectedLabelIdProvider = StateProvider<String?>((ref) => null);

/// Selected project ID for filtering within tasks view (when sorted by project)
final tasksSelectedProjectIdForFilterProvider = StateProvider<String?>((ref) => null);

/// Projects grouped by integration provider (integrationId)
/// Returns a map like: {'openza_tasks': [...], 'todoist': [...], 'msToDo': [...]}
final projectsByProviderProvider =
    Provider<Map<String, List<ProjectEntity>>>((ref) {
  final data = ref.watch(unifiedDataProvider).value;
  if (data == null) return {};

  final grouped = <String, List<ProjectEntity>>{};

  for (final project in data.projects) {
    grouped.putIfAbsent(project.integrationId, () => []).add(project);
  }

  // Sort each group: favorites first, then by sortOrder
  for (final projects in grouped.values) {
    projects.sort((a, b) {
      if (a.isFavorite != b.isFavorite) {
        return a.isFavorite ? -1 : 1;
      }
      return a.sortOrder.compareTo(b.sortOrder);
    });
  }

  return grouped;
});

/// Data filtered by selected project
class FilteredProjectData {
  final List<TaskEntity> tasks;
  final ProjectEntity? project;

  const FilteredProjectData({
    this.tasks = const [],
    this.project,
  });
}

/// Provider that filters tasks by selected project
final filteredByProjectProvider = Provider<FilteredProjectData>((ref) {
  final selectedProjectId = ref.watch(selectedProjectIdProvider);
  final data = ref.watch(unifiedDataProvider).value;

  if (data == null) {
    return const FilteredProjectData();
  }

  // No project selected - return all tasks
  if (selectedProjectId == null) {
    return FilteredProjectData(
      tasks: data.tasks,
      project: null,
    );
  }

  // Find the selected project
  final project = data.projects.where((p) => p.id == selectedProjectId).firstOrNull;

  // Filter tasks by project
  final filteredTasks =
      data.tasks.where((t) => t.projectId == selectedProjectId).toList();

  return FilteredProjectData(
    tasks: filteredTasks,
    project: project,
  );
});

/// Virtual project extracted from task metadata (for provider tasks)
class VirtualProject {
  final String id;           // e.g., "todoist_source_12345"
  final String sourceId;     // Provider's project ID from metadata
  final String integrationId;
  final String name;
  final int taskCount;

  const VirtualProject({
    required this.id,
    required this.sourceId,
    required this.integrationId,
    required this.name,
    required this.taskCount,
  });
}

/// Provider that extracts "virtual projects" from provider tasks' metadata
/// These are projects shown in the sidebar for Todoist/MS To-Do but not stored in DB
final providerVirtualProjectsProvider = Provider<Map<String, List<VirtualProject>>>((ref) {
  final data = ref.watch(unifiedDataProvider).value;
  if (data == null) return {};

  final virtualProjects = <String, Map<String, VirtualProject>>{};

  for (final task in data.tasks) {
    // Skip native tasks
    if (task.isNative) continue;

    final sourceProjectId = task.sourceProjectId;
    if (sourceProjectId == null) continue;

    final integrationId = task.integrationId;
    final virtualId = '${integrationId}_source_$sourceProjectId';

    virtualProjects.putIfAbsent(integrationId, () => {});

    // Get project name from metadata (or fall back to ID)
    final projectName = task.sourceProjectName ?? sourceProjectId;

    if (virtualProjects[integrationId]!.containsKey(virtualId)) {
      // Increment task count
      final existing = virtualProjects[integrationId]![virtualId]!;
      virtualProjects[integrationId]![virtualId] = VirtualProject(
        id: existing.id,
        sourceId: existing.sourceId,
        integrationId: existing.integrationId,
        name: existing.name,
        taskCount: existing.taskCount + 1,
      );
    } else {
      // Create new virtual project
      virtualProjects[integrationId]![virtualId] = VirtualProject(
        id: virtualId,
        sourceId: sourceProjectId,
        integrationId: integrationId,
        name: projectName,
        taskCount: 1,
      );
    }
  }

  // Convert to list format
  final result = <String, List<VirtualProject>>{};
  for (final entry in virtualProjects.entries) {
    result[entry.key] = entry.value.values.toList()
      ..sort((a, b) => b.taskCount.compareTo(a.taskCount)); // Sort by task count
  }

  return result;
});

/// Provider display names for integrations
const providerDisplayNames = {
  'openza_tasks': 'Local',
  'todoist': 'Todoist',
  'msToDo': 'Microsoft To-Do',
};

/// Get display name for a provider
String getProviderDisplayName(String integrationId) {
  return providerDisplayNames[integrationId] ?? integrationId;
}
