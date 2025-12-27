import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_router.dart';
import '../../../app/app_theme.dart';
import '../../providers/repository_provider.dart';
import '../../providers/selected_project_provider.dart';
import '../../providers/sync_provider.dart';
import '../../providers/task_provider.dart';
import '../dialogs/create_task_dialog.dart';
import '../dialogs/import_markdown_dialog.dart';

/// Navigation rail widget (160px) with icon + text labels
/// Part of the 4-pane layout: NavRail | ProjectsPane | TasksList | TaskDetails
class NavRail extends ConsumerWidget {
  const NavRail({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final currentPath = GoRouterState.of(context).uri.path;

    return Container(
      width: 160,
      decoration: BoxDecoration(
        color: Theme.of(context).colorScheme.surface,
        border: Border(
          right: BorderSide(color: Theme.of(context).dividerColor),
        ),
      ),
      child: Column(
        children: [
          const SizedBox(height: 12),

          // Add Task Button + Import Button
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 12),
            child: Column(
              children: [
                SizedBox(
                  width: double.infinity,
                  child: ElevatedButton.icon(
                    onPressed: () => _showCreateTaskDialog(context, ref),
                    icon: const Icon(LucideIcons.plus, size: 16),
                    label: const Text('Add Task'),
                    style: ElevatedButton.styleFrom(
                      padding: const EdgeInsets.symmetric(vertical: 10),
                    ),
                  ),
                ),
                const SizedBox(height: 8),
                SizedBox(
                  width: double.infinity,
                  child: OutlinedButton.icon(
                    onPressed: () => ImportMarkdownDialog.show(context),
                    icon: Icon(LucideIcons.fileDown, size: 14),
                    label: const Text('Import'),
                    style: OutlinedButton.styleFrom(
                      padding: const EdgeInsets.symmetric(vertical: 8),
                      foregroundColor: AppTheme.gray600,
                      side: BorderSide(color: AppTheme.gray300),
                    ),
                  ),
                ),
              ],
            ),
          ),

          const SizedBox(height: 12),

          // Navigation items
          Expanded(
            child: ListView(
              padding: const EdgeInsets.symmetric(horizontal: 8),
              children: [
                _NavRailItem(
                  icon: LucideIcons.star,
                  label: 'Next Actions',
                  path: AppRoutes.nextActions,
                  isActive: currentPath == AppRoutes.nextActions,
                  onTap:
                      () => _navigateAndClearProject(
                        context,
                        ref,
                        AppRoutes.nextActions,
                      ),
                ),
                _NavRailItem(
                  icon: LucideIcons.calendarDays,
                  label: 'Today',
                  path: AppRoutes.today,
                  isActive: currentPath == AppRoutes.today,
                  onTap:
                      () => _navigateAndClearProject(
                        context,
                        ref,
                        AppRoutes.today,
                      ),
                ),
                _NavRailItem(
                  icon: LucideIcons.alertCircle,
                  label: 'Overdue',
                  path: AppRoutes.overdue,
                  isActive: currentPath == AppRoutes.overdue,
                  badgeColor: AppTheme.errorRed,
                  onTap:
                      () => _navigateAndClearProject(
                        context,
                        ref,
                        AppRoutes.overdue,
                      ),
                ),
                _NavRailItem(
                  icon: LucideIcons.listTodo,
                  label: 'Tasks',
                  path: AppRoutes.tasks,
                  isActive: currentPath == AppRoutes.tasks,
                  onTap:
                      () => _navigateAndClearProject(
                        context,
                        ref,
                        AppRoutes.tasks,
                      ),
                ),
                _NavRailItem(
                  icon: LucideIcons.checkCircle2,
                  label: 'Completed',
                  path: AppRoutes.completed,
                  isActive: currentPath == AppRoutes.completed,
                  badgeColor: AppTheme.successGreen,
                  onTap:
                      () => _navigateAndClearProject(
                        context,
                        ref,
                        AppRoutes.completed,
                      ),
                ),
              ],
            ),
          ),

          // Sync + Settings at bottom
          Container(
            padding: const EdgeInsets.all(8),
            decoration: BoxDecoration(
              border: Border(
                top: BorderSide(color: Theme.of(context).dividerColor),
              ),
            ),
            child: Column(
              children: [
                // Sync
                const _SyncNavItem(),
                // Settings
                _NavRailItem(
                  icon: LucideIcons.settings,
                  label: 'Settings',
                  path: AppRoutes.settings,
                  isActive: currentPath == AppRoutes.settings,
                  onTap: () => context.go(AppRoutes.settings),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  /// Navigate to a path and clear the project selection
  void _navigateAndClearProject(
    BuildContext context,
    WidgetRef ref,
    String path,
  ) {
    // Clear project selection when navigating via nav rail
    ref.read(selectedProjectIdProvider.notifier).state = null;
    context.go(path);
  }

  /// Show the create task dialog
  Future<void> _showCreateTaskDialog(
    BuildContext context,
    WidgetRef ref,
  ) async {
    final data = ref.read(unifiedDataProvider).value;
    if (data == null) return;

    final task = await CreateTaskDialog.show(
      context,
      projects: data.projects,
      labels: data.labels,
    );

    if (task != null) {
      final repository = await ref.read(taskRepositoryProvider.future);
      await repository.createTask(task);

      // Defer provider invalidation to next frame to avoid GPU context issues
      // during dialog close animation
      WidgetsBinding.instance.addPostFrameCallback((_) {
        ref.invalidate(localTasksProvider);
        ref.invalidate(unifiedDataProvider);
      });
    }
  }
}

/// Individual navigation item in the rail
class _NavRailItem extends StatefulWidget {
  final IconData icon;
  final String label;
  final String path;
  final bool isActive;
  final Color? badgeColor;
  final VoidCallback onTap;

  const _NavRailItem({
    required this.icon,
    required this.label,
    required this.path,
    this.isActive = false,
    this.badgeColor,
    required this.onTap,
  });

  @override
  State<_NavRailItem> createState() => _NavRailItemState();
}

class _NavRailItemState extends State<_NavRailItem> {
  bool _isHovered = false;

  @override
  Widget build(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;

    // Color scheme - use primaryBlue for active state, bold text like Todoist
    final activeIconColor = AppTheme.primaryBlue;
    final inactiveColor = isDark ? AppTheme.gray300 : AppTheme.gray600;
    final textActiveColor = isDark ? Colors.white : Colors.black;
    final textInactiveColor = isDark ? AppTheme.gray200 : AppTheme.gray900;

    // Background colors - blue tint for active
    final activeBg =
        isDark
            ? AppTheme.primaryBlue.withValues(alpha: 0.15)
            : AppTheme.primaryBlue.withValues(alpha: 0.08);
    final hoverBg =
        isDark ? AppTheme.gray700.withValues(alpha: 0.3) : AppTheme.gray100;

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: MouseRegion(
        onEnter: (_) => setState(() => _isHovered = true),
        onExit: (_) => setState(() => _isHovered = false),
        child: AnimatedContainer(
          duration: AppTheme.animationFast,
          decoration: BoxDecoration(
            color:
                widget.isActive
                    ? activeBg
                    : _isHovered
                    ? hoverBg
                    : Colors.transparent,
            borderRadius: BorderRadius.circular(8),
            border: Border(
              left: BorderSide(
                color:
                    widget.isActive ? AppTheme.primaryBlue : Colors.transparent,
                width: 4,
              ),
            ),
          ),
          child: GestureDetector(
            onTap: widget.onTap,
            behavior: HitTestBehavior.opaque,
            child: Padding(
              padding: const EdgeInsets.symmetric(
                horizontal: 10,
                vertical: 8,
              ),
              child: Row(
                children: [
                  Icon(
                    widget.icon,
                    size: 18,
                    color: widget.isActive ? activeIconColor : inactiveColor,
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      widget.label,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight:
                            widget.isActive
                                ? FontWeight.w600
                                : FontWeight.w500,
                        color:
                            widget.isActive
                                ? textActiveColor
                                : textInactiveColor,
                      ),
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  if (widget.badgeColor != null)
                    Container(
                      width: 6,
                      height: 6,
                      decoration: BoxDecoration(
                        color: widget.badgeColor,
                        shape: BoxShape.circle,
                      ),
                    ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// Sync nav item widget with status indicator
class _SyncNavItem extends ConsumerWidget {
  const _SyncNavItem();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final syncState = ref.watch(syncProvider);

    final icon = switch (syncState.status) {
      SyncStatus.idle => LucideIcons.refreshCw,
      SyncStatus.syncing => LucideIcons.loader2,
      SyncStatus.success => LucideIcons.checkCircle,
      SyncStatus.error => LucideIcons.alertCircle,
    };

    final label = switch (syncState.status) {
      SyncStatus.idle => 'Sync',
      SyncStatus.syncing => 'Syncing...',
      SyncStatus.success => 'Synced',
      SyncStatus.error => 'Retry',
    };

    final color = switch (syncState.status) {
      SyncStatus.idle => AppTheme.gray600,
      SyncStatus.syncing => AppTheme.primaryBlue,
      SyncStatus.success => AppTheme.successGreen,
      SyncStatus.error => AppTheme.errorRed,
    };

    // Build tooltip message
    String tooltip = 'Click to sync';
    if (syncState.lastSyncTime != null) {
      final ago = DateTime.now().difference(syncState.lastSyncTime!);
      if (ago.inMinutes < 1) {
        tooltip = 'Last synced just now';
      } else if (ago.inMinutes < 60) {
        tooltip = 'Last synced ${ago.inMinutes}m ago';
      } else {
        tooltip = 'Last synced ${ago.inHours}h ago';
      }
    }
    if (syncState.pendingCompletions > 0) {
      tooltip += '\n${syncState.pendingCompletions} pending';
    }

    return Tooltip(
      message: tooltip,
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 2),
        child: GestureDetector(
          onTap:
              syncState.isSyncing
                  ? null
                  : () => ref.read(syncProvider.notifier).syncNow(),
          behavior: HitTestBehavior.opaque,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
              child: Row(
                children: [
                  syncState.isSyncing
                      ? SizedBox(
                        width: 18,
                        height: 18,
                        child: CircularProgressIndicator(
                          strokeWidth: 2,
                          color: color,
                        ),
                      )
                      : Icon(icon, size: 18, color: color),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      label,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w500,
                        color: color,
                      ),
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                if (syncState.pendingCompletions > 0)
                  Container(
                    width: 6,
                    height: 6,
                    decoration: BoxDecoration(
                      color: AppTheme.warningOrange,
                      shape: BoxShape.circle,
                    ),
                  ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
