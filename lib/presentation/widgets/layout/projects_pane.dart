import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_router.dart';
import '../../../app/app_theme.dart';
import '../../../domain/entities/project.dart';
import '../../providers/selected_project_provider.dart';
import '../../providers/task_provider.dart';
import '../common/provider_badge.dart';

/// Projects pane (200px) showing all projects grouped by provider
/// Part of the 4-pane layout: NavRail | ProjectsPane | TasksList | TaskDetails
class ProjectsPane extends ConsumerStatefulWidget {
  const ProjectsPane({super.key});

  @override
  ConsumerState<ProjectsPane> createState() => _ProjectsPaneState();
}

class _ProjectsPaneState extends ConsumerState<ProjectsPane> {
  final _searchController = TextEditingController();
  String _searchQuery = '';

  /// Tracks which provider groups are expanded
  final Map<String, bool> _expandedGroups = {
    'openza_tasks': true,
    'todoist': true,
    'msToDo': true,
  };

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  Color _hexToColor(String? hex) {
    if (hex == null) return AppTheme.gray500;
    final hexCode = hex.replaceAll('#', '');
    if (hexCode.length != 6) return AppTheme.gray500;
    return Color(int.parse('FF$hexCode', radix: 16));
  }

  @override
  Widget build(BuildContext context) {
    final projectsByProvider = ref.watch(projectsByProviderProvider);
    final selectedProjectId = ref.watch(selectedProjectIdProvider);

    return Container(
      width: 260,
      decoration: BoxDecoration(
        color: Theme.of(context).colorScheme.surface,
        border: Border(
          right: BorderSide(color: Theme.of(context).dividerColor),
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Search bar
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 12, 12, 8),
            child: TextField(
              controller: _searchController,
              onChanged: (value) {
                setState(() {
                  _searchQuery = value.toLowerCase();
                });
              },
              decoration: InputDecoration(
                hintText: 'Search projects...',
                hintStyle: const TextStyle(
                  fontSize: 13,
                  color: AppTheme.gray400,
                ),
                prefixIcon: const Icon(
                  LucideIcons.search,
                  size: 16,
                  color: AppTheme.gray400,
                ),
                suffixIcon: _searchQuery.isNotEmpty
                    ? IconButton(
                        icon: const Icon(LucideIcons.x, size: 14, color: AppTheme.gray400),
                        onPressed: () {
                          _searchController.clear();
                          setState(() {
                            _searchQuery = '';
                          });
                        },
                      )
                    : null,
                isDense: true,
                contentPadding: const EdgeInsets.symmetric(vertical: 10),
                border: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(6),
                  borderSide: const BorderSide(color: AppTheme.gray200),
                ),
                enabledBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(6),
                  borderSide: const BorderSide(color: AppTheme.gray200),
                ),
                focusedBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(6),
                  borderSide: const BorderSide(color: AppTheme.primaryBlue, width: 1.5),
                ),
                filled: true,
                fillColor: AppTheme.gray50,
              ),
              style: const TextStyle(fontSize: 13),
            ),
          ),

          const SizedBox(height: 4),

          // Projects list grouped by provider
          Expanded(
            child: ref.watch(unifiedDataProvider).when(
                  skipLoadingOnRefresh: true,
                  data: (data) {
                    if (projectsByProvider.isEmpty) {
                      return _buildEmptyState();
                    }

                    return ListView(
                      padding: const EdgeInsets.symmetric(horizontal: 8),
                      children: _buildProviderGroups(
                        projectsByProvider,
                        selectedProjectId,
                      ),
                    );
                  },
                  loading: () => const Center(
                    child: Padding(
                      padding: EdgeInsets.all(24),
                      child: SizedBox(
                        width: 20,
                        height: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      ),
                    ),
                  ),
                  error: (error, stack) => _buildEmptyState(),
                ),
          ),
        ],
      ),
    );
  }

  Widget _buildEmptyState() {
    if (_searchQuery.isNotEmpty) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(LucideIcons.searchX, size: 32, color: AppTheme.gray300),
              const SizedBox(height: 8),
              Text(
                'No projects found',
                style: TextStyle(
                  fontSize: 12,
                  color: AppTheme.gray500,
                ),
              ),
            ],
          ),
        ),
      );
    }

    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(LucideIcons.folderOpen, size: 32, color: AppTheme.gray300),
            const SizedBox(height: 8),
            Text(
              'No projects yet',
              style: TextStyle(
                fontSize: 12,
                color: AppTheme.gray500,
              ),
            ),
          ],
        ),
      ),
    );
  }

  List<Widget> _buildProviderGroups(
    Map<String, List<ProjectEntity>> projectsByProvider,
    String? selectedProjectId,
  ) {
    final widgets = <Widget>[];

    // Define the order of providers to display
    const providerOrder = ['openza_tasks', 'todoist', 'msToDo'];

    for (final providerId in providerOrder) {
      final projects = projectsByProvider[providerId];
      if (projects == null || projects.isEmpty) continue;

      // Filter projects by search query
      final filteredProjects = _searchQuery.isEmpty
          ? projects
          : projects
              .where((p) => p.name.toLowerCase().contains(_searchQuery))
              .toList();

      if (filteredProjects.isEmpty && _searchQuery.isNotEmpty) continue;

      widgets.add(
        _ProviderGroup(
          providerId: providerId,
          projects: filteredProjects,
          selectedProjectId: selectedProjectId,
          isExpanded: _expandedGroups[providerId] ?? true,
          onToggleExpanded: () {
            setState(() {
              _expandedGroups[providerId] = !(_expandedGroups[providerId] ?? true);
            });
          },
          onProjectSelected: (projectId) => _selectProject(projectId),
          hexToColor: _hexToColor,
        ),
      );
    }

    // Handle any other providers not in the predefined order
    for (final entry in projectsByProvider.entries) {
      if (providerOrder.contains(entry.key)) continue;

      final filteredProjects = _searchQuery.isEmpty
          ? entry.value
          : entry.value
              .where((p) => p.name.toLowerCase().contains(_searchQuery))
              .toList();

      if (filteredProjects.isEmpty) continue;

      widgets.add(
        _ProviderGroup(
          providerId: entry.key,
          projects: filteredProjects,
          selectedProjectId: selectedProjectId,
          isExpanded: _expandedGroups[entry.key] ?? true,
          onToggleExpanded: () {
            setState(() {
              _expandedGroups[entry.key] = !(_expandedGroups[entry.key] ?? true);
            });
          },
          onProjectSelected: (projectId) => _selectProject(projectId),
          hexToColor: _hexToColor,
        ),
      );
    }

    return widgets;
  }

  void _selectProject(String projectId) {
    ref.read(selectedProjectIdProvider.notifier).state = projectId;
    // Navigate to tasks with projectId
    context.go('${AppRoutes.tasks}?projectId=$projectId');
  }
}

/// A collapsible group of projects for a single provider
class _ProviderGroup extends StatelessWidget {
  final String providerId;
  final List<ProjectEntity> projects;
  final String? selectedProjectId;
  final bool isExpanded;
  final VoidCallback onToggleExpanded;
  final void Function(String) onProjectSelected;
  final Color Function(String?) hexToColor;

  const _ProviderGroup({
    required this.providerId,
    required this.projects,
    required this.selectedProjectId,
    required this.isExpanded,
    required this.onToggleExpanded,
    required this.onProjectSelected,
    required this.hexToColor,
  });

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // Provider header
        InkWell(
          onTap: onToggleExpanded,
          borderRadius: BorderRadius.circular(6),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 8),
            child: Row(
              children: [
                Icon(
                  isExpanded ? LucideIcons.chevronDown : LucideIcons.chevronRight,
                  size: 14,
                  color: AppTheme.gray500,
                ),
                const SizedBox(width: 6),
                ProviderBadge(
                  integrationId: providerId,
                  showLabel: false,
                  size: ProviderBadgeSize.small,
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    getProviderDisplayName(providerId),
                    style: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w500,
                      color: AppTheme.gray600,
                    ),
                  ),
                ),
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                  decoration: BoxDecoration(
                    color: AppTheme.gray100,
                    borderRadius: BorderRadius.circular(4),
                  ),
                  child: Text(
                    '${projects.length}',
                    style: const TextStyle(
                      fontSize: 11,
                      fontWeight: FontWeight.w500,
                      color: AppTheme.gray500,
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),

        // Projects list
        if (isExpanded)
          ...projects.map(
            (project) => _ProjectItem(
              project: project,
              isSelected: project.id == selectedProjectId,
              onTap: () => onProjectSelected(project.id),
              hexToColor: hexToColor,
            ),
          ),

        const SizedBox(height: 8),
      ],
    );
  }
}

/// Individual project item in the list
class _ProjectItem extends StatelessWidget {
  final ProjectEntity project;
  final bool isSelected;
  final VoidCallback onTap;
  final Color Function(String?) hexToColor;

  const _ProjectItem({
    required this.project,
    required this.isSelected,
    required this.onTap,
    required this.hexToColor,
  });

  @override
  Widget build(BuildContext context) {
    final isInbox = project.isInbox;
    final color = hexToColor(project.color);

    return Padding(
      padding: const EdgeInsets.only(left: 20, top: 1, bottom: 1, right: 4),
      child: Material(
        color: isSelected
            ? AppTheme.primaryBlue.withValues(alpha: 0.1)
            : Colors.transparent,
        borderRadius: BorderRadius.circular(6),
        child: InkWell(
          onTap: onTap,
          borderRadius: BorderRadius.circular(6),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 7),
            child: Row(
              children: [
                // Project color indicator or inbox icon
                if (isInbox)
                  Icon(LucideIcons.inbox, size: 16, color: color)
                else
                  Container(
                    width: 12,
                    height: 12,
                    decoration: BoxDecoration(
                      color: color,
                      borderRadius: BorderRadius.circular(3),
                    ),
                  ),
                const SizedBox(width: 10),
                // Project name
                Expanded(
                  child: Text(
                    project.name,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: isSelected ? FontWeight.w500 : FontWeight.w400,
                      color: isSelected ? AppTheme.primaryBlue : AppTheme.gray700,
                    ),
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                // Favorite indicator
                if (project.isFavorite)
                  const Icon(
                    LucideIcons.star,
                    size: 14,
                    color: AppTheme.amber500,
                  ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
