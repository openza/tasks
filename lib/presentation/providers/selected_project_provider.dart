import 'package:flutter_riverpod/flutter_riverpod.dart';
// ignore: deprecated_member_use
import 'package:flutter_riverpod/legacy.dart';

import '../../domain/entities/project.dart';
import '../../domain/entities/task.dart';
import 'task_provider.dart';

/// Currently selected project ID - null means "All Projects" (no filter)
final selectedProjectIdProvider = StateProvider<String?>((ref) => null);

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
