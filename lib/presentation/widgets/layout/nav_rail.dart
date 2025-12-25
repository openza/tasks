import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_router.dart';
import '../../../app/app_theme.dart';
import '../../providers/repository_provider.dart';
import '../../providers/selected_project_provider.dart';
import '../../providers/task_provider.dart';
import '../badges/sync_badge.dart';
import '../common/openza_logo.dart';
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
          // Logo and sync status
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 12),
            child: Row(
              children: [
                const OpenzaLogo(size: 28, showText: false),
                const Spacer(),
                const SyncBadge(),
              ],
            ),
          ),

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
                  icon: LucideIcons.layoutDashboard,
                  label: 'Dashboard',
                  path: AppRoutes.dashboard,
                  isActive: currentPath == AppRoutes.dashboard,
                  onTap: () => _navigateAndClearProject(context, ref, AppRoutes.dashboard),
                ),
                _NavRailItem(
                  icon: LucideIcons.star,
                  label: 'Next Actions',
                  path: AppRoutes.nextActions,
                  isActive: currentPath == AppRoutes.nextActions,
                  onTap: () => _navigateAndClearProject(context, ref, AppRoutes.nextActions),
                ),
                _NavRailItem(
                  icon: LucideIcons.calendarDays,
                  label: 'Today',
                  path: AppRoutes.today,
                  isActive: currentPath == AppRoutes.today,
                  onTap: () => _navigateAndClearProject(context, ref, AppRoutes.today),
                ),
                _NavRailItem(
                  icon: LucideIcons.alertCircle,
                  label: 'Overdue',
                  path: AppRoutes.overdue,
                  isActive: currentPath == AppRoutes.overdue,
                  badgeColor: AppTheme.errorRed,
                  onTap: () => _navigateAndClearProject(context, ref, AppRoutes.overdue),
                ),
                _NavRailItem(
                  icon: LucideIcons.listTodo,
                  label: 'Tasks',
                  path: AppRoutes.tasks,
                  isActive: currentPath == AppRoutes.tasks,
                  onTap: () => _navigateAndClearProject(context, ref, AppRoutes.tasks),
                ),
                _NavRailItem(
                  icon: LucideIcons.checkCircle2,
                  label: 'Completed',
                  path: AppRoutes.completed,
                  isActive: currentPath == AppRoutes.completed,
                  badgeColor: AppTheme.successGreen,
                  onTap: () => _navigateAndClearProject(context, ref, AppRoutes.completed),
                ),
              ],
            ),
          ),

          // Settings at bottom
          Container(
            padding: const EdgeInsets.all(8),
            decoration: BoxDecoration(
              border: Border(
                top: BorderSide(color: Theme.of(context).dividerColor),
              ),
            ),
            child: _NavRailItem(
              icon: LucideIcons.settings,
              label: 'Settings',
              path: AppRoutes.settings,
              isActive: currentPath == AppRoutes.settings,
              onTap: () => context.go(AppRoutes.settings),
            ),
          ),
        ],
      ),
    );
  }

  /// Navigate to a path and clear the project selection
  void _navigateAndClearProject(BuildContext context, WidgetRef ref, String path) {
    // Clear project selection when navigating via nav rail
    ref.read(selectedProjectIdProvider.notifier).state = null;
    context.go(path);
  }

  /// Show the create task dialog
  Future<void> _showCreateTaskDialog(BuildContext context, WidgetRef ref) async {
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
      ref.invalidate(localTasksProvider);
      ref.invalidate(unifiedDataProvider);
    }
  }
}

/// Individual navigation item in the rail
class _NavRailItem extends StatelessWidget {
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
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Material(
        color: isActive
            ? AppTheme.primaryBlue.withValues(alpha: 0.1)
            : Colors.transparent,
        borderRadius: BorderRadius.circular(8),
        child: InkWell(
          onTap: onTap,
          borderRadius: BorderRadius.circular(8),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
            child: Row(
              children: [
                Icon(
                  icon,
                  size: 18,
                  color: isActive ? AppTheme.primaryBlue : AppTheme.gray600,
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    label,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: isActive ? FontWeight.w600 : FontWeight.w500,
                      color: isActive ? AppTheme.primaryBlue : AppTheme.gray700,
                    ),
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                if (badgeColor != null)
                  Container(
                    width: 6,
                    height: 6,
                    decoration: BoxDecoration(
                      color: badgeColor,
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
