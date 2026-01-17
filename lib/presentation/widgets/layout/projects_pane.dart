import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:lucide_icons/lucide_icons.dart';
import 'package:drift/drift.dart' show Value;
import 'package:toastification/toastification.dart';

import '../../../app/app_router.dart';
import '../../../app/app_theme.dart';
import '../../../data/datasources/local/database/database.dart';
import '../../../domain/entities/project.dart';
import '../../providers/database_provider.dart';
import '../../providers/selected_project_provider.dart';
import '../../providers/task_provider.dart';
import '../common/provider_badge.dart';
import '../dialogs/delete_project_dialog.dart';
import '../dialogs/project_dialog.dart';

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
    final virtualProjects = ref.watch(providerVirtualProjectsProvider);
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
          // Header with search and add button
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 12, 12, 8),
            child: Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _searchController,
                    onChanged: (value) {
                      setState(() {
                        _searchQuery = value.toLowerCase();
                      });
                    },
                    decoration: InputDecoration(
                      hintText: 'Search projects...',
                      hintStyle: TextStyle(
                        fontSize: 13,
                        color: Theme.of(context).brightness == Brightness.dark
                            ? AppTheme.gray400
                            : AppTheme.gray500,
                      ),
                      prefixIcon: Icon(
                        LucideIcons.search,
                        size: 16,
                        color: Theme.of(context).brightness == Brightness.dark
                            ? AppTheme.gray400
                            : AppTheme.gray500,
                      ),
                      suffixIcon:
                          _searchQuery.isNotEmpty
                              ? IconButton(
                                icon: const Icon(
                                  LucideIcons.x,
                                  size: 14,
                                  color: AppTheme.gray400,
                                ),
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
                        borderSide: const BorderSide(
                          color: AppTheme.primaryBlue,
                          width: 1.5,
                        ),
                      ),
                      filled: true,
                      fillColor: Theme.of(context).brightness == Brightness.dark
                          ? AppTheme.gray800
                          : AppTheme.gray100,
                    ),
                    style: const TextStyle(fontSize: 13),
                  ),
                ),
              ],
            ),
          ),

          const SizedBox(height: 4),

          // Projects list grouped by provider
          Expanded(
            child: ref
                .watch(unifiedDataProvider)
                .when(
                  skipLoadingOnRefresh: true,
                  data: (data) {
                    if (projectsByProvider.isEmpty) {
                      return _buildEmptyState();
                    }

                    return ListView(
                      padding: const EdgeInsets.symmetric(horizontal: 8),
                      children: _buildProviderGroups(
                        projectsByProvider,
                        virtualProjects,
                        selectedProjectId,
                      ),
                    );
                  },
                  loading:
                      () => const Center(
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
                style: TextStyle(fontSize: 12, color: AppTheme.gray500),
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
              style: TextStyle(fontSize: 12, color: AppTheme.gray500),
            ),
          ],
        ),
      ),
    );
  }

  List<Widget> _buildProviderGroups(
    Map<String, List<ProjectEntity>> projectsByProvider,
    Map<String, List<VirtualProject>> virtualProjects,
    String? selectedProjectId,
  ) {
    final widgets = <Widget>[];

    // Define the order of providers to display
    const providerOrder = ['openza_tasks', 'todoist', 'msToDo'];

    for (final providerId in providerOrder) {
      // For local projects (openza_tasks), show from database
      if (providerId == 'openza_tasks') {
        final projects = projectsByProvider[providerId];
        if (projects == null || projects.isEmpty) continue;

        // Filter projects by search query
        final filteredProjects =
            _searchQuery.isEmpty
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
                _expandedGroups[providerId] =
                    !(_expandedGroups[providerId] ?? true);
              });
            },
            onProjectSelected: (projectId) => _selectProject(projectId),
            onEditProject: (project) => _editProject(context, project),
            onDeleteProject: (project) => _deleteProject(context, project),
            hexToColor: _hexToColor,
          ),
        );
      } else {
        // For external providers (todoist, msToDo), show virtual projects from metadata
        final vProjects = virtualProjects[providerId];
        if (vProjects == null || vProjects.isEmpty) continue;

        // Filter virtual projects by search query
        final filteredProjects =
            _searchQuery.isEmpty
                ? vProjects
                : vProjects
                    .where((p) => p.name.toLowerCase().contains(_searchQuery))
                    .toList();

        if (filteredProjects.isEmpty && _searchQuery.isNotEmpty) continue;

        widgets.add(
          _VirtualProviderGroup(
            providerId: providerId,
            projects: filteredProjects,
            selectedProjectId: selectedProjectId,
            isExpanded: _expandedGroups[providerId] ?? true,
            onToggleExpanded: () {
              setState(() {
                _expandedGroups[providerId] =
                    !(_expandedGroups[providerId] ?? true);
              });
            },
            onProjectSelected: (projectId) => _selectVirtualProject(projectId),
          ),
        );
      }
    }

    return widgets;
  }

  void _selectVirtualProject(String virtualProjectId) {
    ref.read(selectedProjectIdProvider.notifier).state = virtualProjectId;
    // Navigate to tasks with the virtual project ID
    context.go('${AppRoutes.tasks}?projectId=$virtualProjectId');
  }

  void _selectProject(String projectId) {
    ref.read(selectedProjectIdProvider.notifier).state = projectId;
    // Navigate to tasks with projectId
    context.go('${AppRoutes.tasks}?projectId=$projectId');
  }

  /// Edit an existing local project
  Future<void> _editProject(BuildContext context, ProjectEntity project) async {
    final updatedProject = await ProjectDialog.showEdit(context, project);
    if (updatedProject == null || !mounted) return;

    try {
      final db = ref.read(databaseProvider);
      await db.updateProject(
        project.id,
        ProjectsCompanion(
          name: Value(updatedProject.name),
          color: Value(updatedProject.color),
          updatedAt: Value(DateTime.now()),
        ),
      );

      // Refresh the data for smooth background update
      ref.refresh(localProjectsProvider);
      ref.refresh(unifiedDataProvider);

      if (mounted) {
        toastification.show(
          // ignore: use_build_context_synchronously
          context: context,
          type: ToastificationType.success,
          title: const Text('Project Updated'),
          description: Text('${updatedProject.name} has been updated'),
          autoCloseDuration: const Duration(seconds: 3),
        );
      }
    } catch (e) {
      if (mounted) {
        toastification.show(
          // ignore: use_build_context_synchronously
          context: context,
          type: ToastificationType.error,
          title: const Text('Error'),
          description: const Text('Failed to update project'),
          autoCloseDuration: const Duration(seconds: 3),
        );
      }
    }
  }

  /// Delete a local project
  Future<void> _deleteProject(BuildContext ctx, ProjectEntity project) async {
    final db = ref.read(databaseProvider);

    // Get task count for the project
    int taskCount;
    try {
      taskCount = await db.getTaskCountForProject(project.id);
    } catch (e) {
      if (mounted) {
        toastification.show(
          // ignore: use_build_context_synchronously
          context: ctx,
          type: ToastificationType.error,
          title: const Text('Error'),
          description: const Text('Failed to check project tasks'),
          autoCloseDuration: const Duration(seconds: 3),
        );
      }
      return;
    }

    if (!mounted) return;

    final action = await DeleteProjectDialog.show(
      // ignore: use_build_context_synchronously
      ctx,
      project: project,
      taskCount: taskCount,
    );

    if (action == null || action == DeleteProjectAction.cancel || !mounted) {
      return;
    }

    try {
      // Use transactional delete to ensure atomicity
      await db.deleteProjectWithTasks(
        project.id,
        moveTasksToInbox: action == DeleteProjectAction.moveTasksToInbox,
      );

      // Clear selection if this project was selected
      if (ref.read(selectedProjectIdProvider) == project.id) {
        ref.read(selectedProjectIdProvider.notifier).state = null;
      }

      // Refresh the data for smooth background update
      ref.refresh(localProjectsProvider);
      ref.refresh(localTasksProvider); // Tasks may have been moved/deleted
      ref.refresh(unifiedDataProvider);

      if (mounted) {
        toastification.show(
          // ignore: use_build_context_synchronously
          context: ctx,
          type: ToastificationType.success,
          title: const Text('Project Deleted'),
          description: Text('${project.name} has been deleted'),
          autoCloseDuration: const Duration(seconds: 3),
        );
      }
    } catch (e) {
      if (mounted) {
        toastification.show(
          // ignore: use_build_context_synchronously
          context: ctx,
          type: ToastificationType.error,
          title: const Text('Error'),
          description: const Text('Failed to delete project'),
          autoCloseDuration: const Duration(seconds: 3),
        );
      }
    }
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
  final void Function(ProjectEntity) onEditProject;
  final void Function(ProjectEntity) onDeleteProject;
  final Color Function(String?) hexToColor;

  const _ProviderGroup({
    required this.providerId,
    required this.projects,
    required this.selectedProjectId,
    required this.isExpanded,
    required this.onToggleExpanded,
    required this.onProjectSelected,
    required this.onEditProject,
    required this.onDeleteProject,
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
            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 10),
            child: Row(
              children: [
                Icon(
                  isExpanded
                      ? LucideIcons.chevronDown
                      : LucideIcons.chevronRight,
                  size: 12,
                  color: AppTheme.gray400,
                ),
                const SizedBox(width: 6),
                ProviderBadge(
                  integrationId: providerId,
                  showLabel: false,
                  size: ProviderBadgeSize.small,
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: Builder(
                    builder: (context) {
                      final isDark = Theme.of(context).brightness == Brightness.dark;
                      return Text(
                        getProviderDisplayName(providerId).toUpperCase(),
                        style: TextStyle(
                          fontSize: 11,
                          fontWeight: FontWeight.w600,
                          color: isDark ? AppTheme.gray300 : AppTheme.gray600,
                          letterSpacing: 0.5,
                        ),
                      );
                    },
                  ),
                ),
                Builder(
                  builder: (context) {
                    final isDark = Theme.of(context).brightness == Brightness.dark;
                    return Container(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 6,
                        vertical: 2,
                      ),
                      decoration: BoxDecoration(
                        color: isDark ? AppTheme.gray700 : AppTheme.gray100,
                        borderRadius: BorderRadius.circular(4),
                      ),
                      child: Text(
                        '${projects.length}',
                        style: TextStyle(
                          fontSize: 10,
                          fontWeight: FontWeight.w500,
                          color: isDark ? AppTheme.gray300 : AppTheme.gray600,
                        ),
                      ),
                    );
                  },
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
              onEdit:
                  project.isNative && !project.isInbox
                      ? () => onEditProject(project)
                      : null,
              onDelete:
                  project.isNative && !project.isInbox
                      ? () => onDeleteProject(project)
                      : null,
              hexToColor: hexToColor,
            ),
          ),

        const SizedBox(height: 8),
      ],
    );
  }
}

/// Individual project item in the list
class _ProjectItem extends StatefulWidget {
  final ProjectEntity project;
  final bool isSelected;
  final VoidCallback onTap;
  final VoidCallback? onEdit;
  final VoidCallback? onDelete;
  final Color Function(String?) hexToColor;

  const _ProjectItem({
    required this.project,
    required this.isSelected,
    required this.onTap,
    this.onEdit,
    this.onDelete,
    required this.hexToColor,
  });

  @override
  State<_ProjectItem> createState() => _ProjectItemState();
}

class _ProjectItemState extends State<_ProjectItem> {
  bool _isHovered = false;

  @override
  Widget build(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final isInbox = widget.project.isInbox;
    final color = widget.hexToColor(widget.project.color);
    final canShowMenu = widget.onEdit != null || widget.onDelete != null;

    // Theme-aware colors
    final selectedBg =
        isDark
            ? AppTheme.gray700.withValues(alpha: 0.5)
            : AppTheme.gray200.withValues(alpha: 0.6);
    final hoverBg =
        isDark ? AppTheme.gray700.withValues(alpha: 0.3) : AppTheme.gray100;
    final selectedTextColor = isDark ? Colors.white : Colors.black;
    final normalTextColor = isDark ? AppTheme.gray200 : AppTheme.gray900;

    Widget itemContent = Padding(
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
              widget.project.name,
              style: TextStyle(
                fontSize: 13,
                fontWeight:
                    widget.isSelected ? FontWeight.w500 : FontWeight.w400,
                color: widget.isSelected ? selectedTextColor : normalTextColor,
              ),
              overflow: TextOverflow.ellipsis,
            ),
          ),
          // Favorite indicator
          if (widget.project.isFavorite)
            const Icon(LucideIcons.star, size: 14, color: AppTheme.amber500),
        ],
      ),
    );

    return Padding(
      padding: const EdgeInsets.only(left: 20, top: 1, bottom: 1, right: 4),
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        onEnter: (_) => setState(() => _isHovered = true),
        onExit: (_) => setState(() => _isHovered = false),
        child: Container(
          decoration: BoxDecoration(
            color:
                widget.isSelected
                    ? selectedBg
                    : _isHovered
                    ? hoverBg
                    : Colors.transparent,
            borderRadius: BorderRadius.circular(6),
          ),
          child: Material(
            color: Colors.transparent,
            child:
                canShowMenu
                    ? _buildWithContextMenu(context, itemContent)
                    : InkWell(
                      onTap: widget.onTap,
                      borderRadius: BorderRadius.circular(6),
                      child: itemContent,
                    ),
          ),
        ),
      ),
    );
  }

  Widget _buildWithContextMenu(BuildContext context, Widget child) {
    return GestureDetector(
      onSecondaryTapUp: (details) {
        _showContextMenu(context, details.globalPosition);
      },
      child: InkWell(
        onTap: widget.onTap,
        borderRadius: BorderRadius.circular(6),
        child: child,
      ),
    );
  }

  void _showContextMenu(BuildContext context, Offset position) {
    final items = <PopupMenuEntry<String>>[];

    if (widget.onEdit != null) {
      items.add(
        PopupMenuItem<String>(
          value: 'edit',
          height: 36,
          child: Row(
            children: [
              Icon(LucideIcons.pencil, size: 16, color: AppTheme.gray600),
              const SizedBox(width: 8),
              const Text('Edit', style: TextStyle(fontSize: 13)),
            ],
          ),
        ),
      );
    }

    if (widget.onDelete != null) {
      if (items.isNotEmpty) {
        items.add(const PopupMenuDivider(height: 8));
      }
      items.add(
        PopupMenuItem<String>(
          value: 'delete',
          height: 36,
          child: Row(
            children: [
              const Icon(LucideIcons.trash2, size: 16, color: Colors.red),
              const SizedBox(width: 8),
              const Text(
                'Delete',
                style: TextStyle(fontSize: 13, color: Colors.red),
              ),
            ],
          ),
        ),
      );
    }

    if (items.isEmpty) return;

    showMenu<String>(
      context: context,
      position: RelativeRect.fromLTRB(
        position.dx,
        position.dy,
        position.dx + 1,
        position.dy + 1,
      ),
      items: items,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
    ).then((value) {
      if (value == 'edit') {
        widget.onEdit?.call();
      } else if (value == 'delete') {
        widget.onDelete?.call();
      }
    });
  }
}

/// A collapsible group of virtual projects for external providers (Todoist, MS To-Do)
class _VirtualProviderGroup extends StatelessWidget {
  final String providerId;
  final List<VirtualProject> projects;
  final String? selectedProjectId;
  final bool isExpanded;
  final VoidCallback onToggleExpanded;
  final void Function(String) onProjectSelected;

  const _VirtualProviderGroup({
    required this.providerId,
    required this.projects,
    required this.selectedProjectId,
    required this.isExpanded,
    required this.onToggleExpanded,
    required this.onProjectSelected,
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
            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 10),
            child: Row(
              children: [
                Icon(
                  isExpanded
                      ? LucideIcons.chevronDown
                      : LucideIcons.chevronRight,
                  size: 12,
                  color: AppTheme.gray400,
                ),
                const SizedBox(width: 6),
                ProviderBadge(
                  integrationId: providerId,
                  showLabel: false,
                  size: ProviderBadgeSize.small,
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: Builder(
                    builder: (context) {
                      final isDark = Theme.of(context).brightness == Brightness.dark;
                      return Text(
                        getProviderDisplayName(providerId).toUpperCase(),
                        style: TextStyle(
                          fontSize: 11,
                          fontWeight: FontWeight.w600,
                          color: isDark ? AppTheme.gray300 : AppTheme.gray600,
                          letterSpacing: 0.5,
                        ),
                      );
                    },
                  ),
                ),
                Builder(
                  builder: (context) {
                    final isDark = Theme.of(context).brightness == Brightness.dark;
                    return Container(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 6,
                        vertical: 2,
                      ),
                      decoration: BoxDecoration(
                        color: isDark ? AppTheme.gray700 : AppTheme.gray100,
                        borderRadius: BorderRadius.circular(4),
                      ),
                      child: Text(
                        '${projects.length}',
                        style: TextStyle(
                          fontSize: 10,
                          fontWeight: FontWeight.w500,
                          color: isDark ? AppTheme.gray300 : AppTheme.gray600,
                        ),
                      ),
                    );
                  },
                ),
              ],
            ),
          ),
        ),

        // Virtual projects list
        if (isExpanded)
          ...projects.map(
            (project) => _VirtualProjectItem(
              project: project,
              isSelected: project.id == selectedProjectId,
              onTap: () => onProjectSelected(project.id),
            ),
          ),

        const SizedBox(height: 8),
      ],
    );
  }
}

/// Individual virtual project item in the list
class _VirtualProjectItem extends StatefulWidget {
  final VirtualProject project;
  final bool isSelected;
  final VoidCallback onTap;

  const _VirtualProjectItem({
    required this.project,
    required this.isSelected,
    required this.onTap,
  });

  @override
  State<_VirtualProjectItem> createState() => _VirtualProjectItemState();
}

class _VirtualProjectItemState extends State<_VirtualProjectItem> {
  bool _isHovered = false;

  @override
  Widget build(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;

    // Theme-aware colors
    final selectedBg =
        isDark
            ? AppTheme.gray700.withValues(alpha: 0.5)
            : AppTheme.gray200.withValues(alpha: 0.6);
    final hoverBg =
        isDark ? AppTheme.gray700.withValues(alpha: 0.3) : AppTheme.gray100;
    final selectedTextColor = isDark ? Colors.white : Colors.black;
    final normalTextColor = isDark ? AppTheme.gray200 : AppTheme.gray900;

    return Padding(
      padding: const EdgeInsets.only(left: 20, top: 1, bottom: 1, right: 4),
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        onEnter: (_) => setState(() => _isHovered = true),
        onExit: (_) => setState(() => _isHovered = false),
        child: Container(
          decoration: BoxDecoration(
            color:
                widget.isSelected
                    ? selectedBg
                    : _isHovered
                    ? hoverBg
                    : Colors.transparent,
            borderRadius: BorderRadius.circular(6),
          ),
          child: Material(
            color: Colors.transparent,
            child: InkWell(
              onTap: widget.onTap,
              borderRadius: BorderRadius.circular(6),
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 7),
                child: Row(
                  children: [
                    // Folder icon for virtual projects
                    Icon(
                      LucideIcons.folder,
                      size: 16,
                      color: _getProviderColor(widget.project.integrationId),
                    ),
                    const SizedBox(width: 10),
                    // Project name
                    Expanded(
                      child: Text(
                        widget.project.name,
                        style: TextStyle(
                          fontSize: 13,
                          fontWeight:
                              widget.isSelected ? FontWeight.w500 : FontWeight.w400,
                          color: widget.isSelected ? selectedTextColor : normalTextColor,
                        ),
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                    // Task count badge
                    Container(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 6,
                        vertical: 2,
                      ),
                      decoration: BoxDecoration(
                        color: isDark ? AppTheme.gray700 : AppTheme.gray100,
                        borderRadius: BorderRadius.circular(4),
                      ),
                      child: Text(
                        '${widget.project.taskCount}',
                        style: TextStyle(
                          fontSize: 10,
                          fontWeight: FontWeight.w500,
                          color: isDark ? AppTheme.gray400 : AppTheme.gray500,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  Color _getProviderColor(String integrationId) {
    switch (integrationId) {
      case 'todoist':
        return const Color(0xFFE44332); // Todoist red
      case 'msToDo':
        return const Color(0xFF2564CF); // Microsoft blue
      default:
        return AppTheme.gray500;
    }
  }
}
