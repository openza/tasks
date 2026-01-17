import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/project.dart';
import '../../../domain/entities/task.dart';
import '../../providers/task_provider.dart';

/// Context menu actions for tasks
enum TaskContextAction {
  moveToInbox,
  moveToProject,
  edit,
  delete,
}

/// Result of a task context menu action
class TaskContextResult {
  final TaskContextAction action;
  final ProjectEntity? targetProject;

  const TaskContextResult({
    required this.action,
    this.targetProject,
  });
}

/// Shows a context menu for task actions including "Move to Project"
class TaskContextMenu {
  /// Show the task context menu at the given position
  /// Returns the selected action or null if cancelled
  static Future<TaskContextResult?> show(
    BuildContext context,
    WidgetRef ref,
    Offset position, {
    required TaskEntity task,
    VoidCallback? onEdit,
    VoidCallback? onDelete,
  }) async {
    // Capture theme info before async gap
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final menuBgColor = isDark ? AppTheme.gray800 : Colors.white;
    final textColor = isDark ? AppTheme.gray100 : AppTheme.gray900;
    final subtleTextColor = isDark ? AppTheme.gray400 : AppTheme.gray500;

    // Get overlay size for proper menu positioning
    // Using Overlay's render box ensures correct positioning in multi-pane layouts
    final RenderBox overlay = Overlay.of(context).context.findRenderObject()! as RenderBox;
    final overlaySize = overlay.size;

    final data = await ref.read(unifiedDataProvider.future);

    // Get local projects (only openza_tasks projects for local organization)
    // Also include Inbox as a special option
    final localProjects = data.projects
        .where((p) => p.integrationId == 'openza_tasks')
        .toList();

    // Find Inbox project
    final inboxProject = localProjects.where((p) => p.isInbox).firstOrNull;

    // Other projects (excluding Inbox)
    final otherProjects = localProjects.where((p) => !p.isInbox).toList();

    // Sort: favorites first, then by name
    otherProjects.sort((a, b) {
      if (a.isFavorite != b.isFavorite) {
        return a.isFavorite ? -1 : 1;
      }
      return a.name.compareTo(b.name);
    });

    // Get recent projects (up to 3, excluding current project)
    final recentProjects = otherProjects
        .where((p) => p.id != task.projectId)
        .take(3)
        .toList();

    return showMenu<TaskContextResult>(
      context: context,
      position: RelativeRect.fromRect(
        Rect.fromLTWH(position.dx, position.dy, 0, 0),
        Offset.zero & overlaySize,
      ),
      color: menuBgColor,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
      items: [
        // Move to Inbox (if not already in Inbox)
        if (task.projectId != inboxProject?.id && inboxProject != null)
          PopupMenuItem<TaskContextResult>(
            value: TaskContextResult(
              action: TaskContextAction.moveToInbox,
              targetProject: inboxProject,
            ),
            height: 40,
            child: Row(
              children: [
                Icon(LucideIcons.inbox, size: 16, color: subtleTextColor),
                const SizedBox(width: 10),
                Text(
                  'Move to Inbox',
                  style: TextStyle(fontSize: 13, color: textColor),
                ),
              ],
            ),
          ),

        // Move to Project submenu
        if (otherProjects.isNotEmpty)
          PopupMenuItem<TaskContextResult>(
            enabled: false,
            height: 40,
            child: Row(
              children: [
                Icon(LucideIcons.folderInput, size: 16, color: subtleTextColor),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    'Move to Project',
                    style: TextStyle(fontSize: 13, color: textColor),
                  ),
                ),
                Icon(LucideIcons.chevronRight, size: 14, color: subtleTextColor),
              ],
            ),
          ),

        // Recent projects (quick access)
        ...recentProjects.map((project) => PopupMenuItem<TaskContextResult>(
          value: TaskContextResult(
            action: TaskContextAction.moveToProject,
            targetProject: project,
          ),
          height: 36,
          padding: const EdgeInsets.only(left: 42, right: 16),
          child: Row(
            children: [
              Container(
                width: 10,
                height: 10,
                decoration: BoxDecoration(
                  color: _parseColor(project.color),
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  project.name,
                  style: TextStyle(fontSize: 12, color: textColor),
                  overflow: TextOverflow.ellipsis,
                ),
              ),
            ],
          ),
        )),

        // More projects option if there are more than 3
        if (otherProjects.length > 3)
          PopupMenuItem<TaskContextResult>(
            height: 36,
            padding: const EdgeInsets.only(left: 42, right: 16),
            onTap: () {
              // This will close the menu and open the full project picker
              Future.delayed(Duration.zero, () {
                _showFullProjectPicker(
                  context,
                  otherProjects,
                  task,
                  isDark,
                );
              });
            },
            child: Row(
              children: [
                Icon(LucideIcons.moreHorizontal, size: 14, color: subtleTextColor),
                const SizedBox(width: 8),
                Text(
                  'More projects...',
                  style: TextStyle(fontSize: 12, color: subtleTextColor),
                ),
              ],
            ),
          ),

        const PopupMenuDivider(height: 8),

        // Edit
        if (onEdit != null)
          PopupMenuItem<TaskContextResult>(
            value: const TaskContextResult(action: TaskContextAction.edit),
            height: 40,
            child: Row(
              children: [
                Icon(LucideIcons.pencil, size: 16, color: subtleTextColor),
                const SizedBox(width: 10),
                Text(
                  'Edit',
                  style: TextStyle(fontSize: 13, color: textColor),
                ),
              ],
            ),
          ),

        // Delete
        if (onDelete != null)
          PopupMenuItem<TaskContextResult>(
            value: const TaskContextResult(action: TaskContextAction.delete),
            height: 40,
            child: Row(
              children: [
                const Icon(LucideIcons.trash2, size: 16, color: Colors.red),
                const SizedBox(width: 10),
                const Text(
                  'Delete',
                  style: TextStyle(fontSize: 13, color: Colors.red),
                ),
              ],
            ),
          ),
      ],
    );
  }

  static Future<ProjectEntity?> _showFullProjectPicker(
    BuildContext context,
    List<ProjectEntity> projects,
    TaskEntity task,
    bool isDark,
  ) async {
    return showDialog<ProjectEntity>(
      context: context,
      builder: (context) => _ProjectPickerDialog(
        projects: projects,
        currentProjectId: task.projectId,
        isDark: isDark,
      ),
    );
  }

  static Color _parseColor(String colorStr) {
    if (colorStr.startsWith('#')) {
      try {
        return Color(int.parse(colorStr.substring(1), radix: 16) + 0xFF000000);
      } catch (_) {
        return AppTheme.gray500;
      }
    }
    return AppTheme.gray500;
  }
}

/// Dialog for selecting from all projects
class _ProjectPickerDialog extends StatefulWidget {
  final List<ProjectEntity> projects;
  final String? currentProjectId;
  final bool isDark;

  const _ProjectPickerDialog({
    required this.projects,
    required this.currentProjectId,
    required this.isDark,
  });

  @override
  State<_ProjectPickerDialog> createState() => _ProjectPickerDialogState();
}

class _ProjectPickerDialogState extends State<_ProjectPickerDialog> {
  String _searchQuery = '';
  final _searchController = TextEditingController();

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final filteredProjects = _searchQuery.isEmpty
        ? widget.projects
        : widget.projects
            .where((p) => p.name.toLowerCase().contains(_searchQuery.toLowerCase()))
            .toList();

    return Dialog(
      backgroundColor: widget.isDark ? AppTheme.gray800 : Colors.white,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 320, maxHeight: 400),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // Header
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 16, 8, 8),
              child: Row(
                children: [
                  Text(
                    'Move to Project',
                    style: TextStyle(
                      fontSize: 16,
                      fontWeight: FontWeight.w600,
                      color: widget.isDark ? Colors.white : Colors.black,
                    ),
                  ),
                  const Spacer(),
                  IconButton(
                    icon: Icon(
                      LucideIcons.x,
                      size: 18,
                      color: widget.isDark ? AppTheme.gray400 : AppTheme.gray500,
                    ),
                    onPressed: () => Navigator.pop(context),
                  ),
                ],
              ),
            ),

            // Search
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16),
              child: TextField(
                controller: _searchController,
                onChanged: (value) => setState(() => _searchQuery = value),
                decoration: InputDecoration(
                  hintText: 'Search projects...',
                  hintStyle: TextStyle(
                    fontSize: 13,
                    color: widget.isDark ? AppTheme.gray400 : AppTheme.gray500,
                  ),
                  prefixIcon: Icon(
                    LucideIcons.search,
                    size: 16,
                    color: widget.isDark ? AppTheme.gray400 : AppTheme.gray500,
                  ),
                  isDense: true,
                  contentPadding: const EdgeInsets.symmetric(vertical: 10),
                  border: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(8),
                    borderSide: BorderSide(
                      color: widget.isDark ? AppTheme.gray600 : AppTheme.gray300,
                    ),
                  ),
                  enabledBorder: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(8),
                    borderSide: BorderSide(
                      color: widget.isDark ? AppTheme.gray600 : AppTheme.gray300,
                    ),
                  ),
                  focusedBorder: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(8),
                    borderSide: const BorderSide(color: AppTheme.primaryBlue),
                  ),
                  filled: true,
                  fillColor: widget.isDark ? AppTheme.gray700 : AppTheme.gray100,
                ),
                style: TextStyle(
                  fontSize: 13,
                  color: widget.isDark ? Colors.white : Colors.black,
                ),
              ),
            ),

            const SizedBox(height: 8),

            // Projects list
            Flexible(
              child: filteredProjects.isEmpty
                  ? Center(
                      child: Padding(
                        padding: const EdgeInsets.all(24),
                        child: Text(
                          'No projects found',
                          style: TextStyle(
                            color: widget.isDark ? AppTheme.gray400 : AppTheme.gray500,
                          ),
                        ),
                      ),
                    )
                  : ListView.builder(
                      shrinkWrap: true,
                      padding: const EdgeInsets.symmetric(horizontal: 8),
                      itemCount: filteredProjects.length,
                      itemBuilder: (context, index) {
                        final project = filteredProjects[index];
                        final isCurrentProject = project.id == widget.currentProjectId;

                        return ListTile(
                          dense: true,
                          enabled: !isCurrentProject,
                          leading: Container(
                            width: 12,
                            height: 12,
                            decoration: BoxDecoration(
                              color: TaskContextMenu._parseColor(project.color),
                              borderRadius: BorderRadius.circular(3),
                            ),
                          ),
                          title: Text(
                            project.name,
                            style: TextStyle(
                              fontSize: 13,
                              color: isCurrentProject
                                  ? (widget.isDark ? AppTheme.gray500 : AppTheme.gray400)
                                  : (widget.isDark ? Colors.white : Colors.black),
                            ),
                          ),
                          trailing: isCurrentProject
                              ? Icon(
                                  LucideIcons.check,
                                  size: 16,
                                  color: AppTheme.primaryBlue,
                                )
                              : null,
                          onTap: isCurrentProject
                              ? null
                              : () => Navigator.pop(context, project),
                        );
                      },
                    ),
            ),

            const SizedBox(height: 8),
          ],
        ),
      ),
    );
  }
}
