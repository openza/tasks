import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_router.dart';
import '../../../app/app_theme.dart';
import '../../providers/auth_provider.dart';
import '../../providers/task_provider.dart';
import '../common/api_error_listener.dart';
import '../common/openza_logo.dart';

class DashboardLayout extends ConsumerStatefulWidget {
  final Widget child;

  const DashboardLayout({super.key, required this.child});

  @override
  ConsumerState<DashboardLayout> createState() => _DashboardLayoutState();
}

class _DashboardLayoutState extends ConsumerState<DashboardLayout> {
  bool _isProjectsExpanded = true;

  Widget _buildSourceSelector() {
    final authState = ref.watch(authProvider);
    final taskSource = ref.watch(taskSourceProvider);

    // Build list of available sources
    final sources = <_SourceOption>[
      _SourceOption(
        source: TaskSource.all,
        label: 'All Sources',
        icon: LucideIcons.layers,
        color: AppTheme.primaryBlue,
      ),
      _SourceOption(
        source: TaskSource.local,
        label: 'Local',
        icon: LucideIcons.database,
        color: AppTheme.gray600,
      ),
    ];

    if (authState.todoistAuthenticated) {
      sources.add(_SourceOption(
        source: TaskSource.todoist,
        label: 'Todoist',
        icon: LucideIcons.checkCircle,
        color: const Color(0xFFE44332),
      ));
    }

    if (authState.msToDoAuthenticated) {
      sources.add(_SourceOption(
        source: TaskSource.msToDo,
        label: 'MS To-Do',
        icon: LucideIcons.layoutGrid,
        color: const Color(0xFF00A4EF),
      ));
    }

    final selectedSource = sources.firstWhere(
      (s) => s.source == taskSource,
      orElse: () => sources.first,
    );

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 16),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
        decoration: BoxDecoration(
          color: AppTheme.gray100,
          borderRadius: BorderRadius.circular(8),
          border: Border.all(color: AppTheme.gray200),
        ),
        child: DropdownButton<TaskSource>(
          value: selectedSource.source,
          isExpanded: true,
          underline: const SizedBox.shrink(),
          icon: Icon(LucideIcons.chevronDown, size: 16, color: AppTheme.gray500),
          items: sources.map((option) {
            return DropdownMenuItem<TaskSource>(
              value: option.source,
              child: Row(
                children: [
                  Icon(option.icon, size: 16, color: option.color),
                  const SizedBox(width: 8),
                  Text(
                    option.label,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w500,
                      color: AppTheme.gray700,
                    ),
                  ),
                ],
              ),
            );
          }).toList(),
          onChanged: (value) {
            if (value != null) {
              ref.read(taskSourceProvider.notifier).state = value;
            }
          },
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final currentPath = GoRouterState.of(context).uri.path;

    return ApiErrorListener(
      child: Scaffold(
      body: Row(
        children: [
          // Sidebar
          Container(
            width: 240,
            decoration: BoxDecoration(
              color: Theme.of(context).colorScheme.surface,
              border: Border(
                right: BorderSide(
                  color: Theme.of(context).dividerColor,
                ),
              ),
            ),
            child: Column(
              children: [
                // Logo/Header
                Padding(
                  padding: const EdgeInsets.all(16),
                  child: Row(
                    children: [
                      const OpenzaLogo(size: 32, showText: true),
                      const Spacer(),
                      IconButton(
                        icon: const Icon(LucideIcons.settings, size: 20),
                        onPressed: () => context.go(AppRoutes.settings),
                        tooltip: 'Settings',
                      ),
                    ],
                  ),
                ),

                // Add Task Button
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 16),
                  child: SizedBox(
                    width: double.infinity,
                    child: ElevatedButton.icon(
                      onPressed: () {
                        // TODO: Show create task dialog
                      },
                      icon: const Icon(LucideIcons.plus, size: 18),
                      label: const Text('Add Task'),
                    ),
                  ),
                ),

                const SizedBox(height: 16),

                // Provider Source Selector
                _buildSourceSelector(),

                const SizedBox(height: 16),

                // Navigation Items
                Expanded(
                  child: ListView(
                    padding: const EdgeInsets.symmetric(horizontal: 8),
                    children: [
                      _NavItem(
                        icon: LucideIcons.layoutDashboard,
                        label: 'Dashboard',
                        path: AppRoutes.dashboard,
                        isActive: currentPath == AppRoutes.dashboard,
                      ),
                      _NavItem(
                        icon: LucideIcons.star,
                        label: 'Next Actions',
                        path: AppRoutes.nextActions,
                        isActive: currentPath == AppRoutes.nextActions,
                      ),
                      _NavItem(
                        icon: LucideIcons.calendarDays,
                        label: 'Today',
                        path: AppRoutes.today,
                        isActive: currentPath == AppRoutes.today,
                      ),
                      _NavItem(
                        icon: LucideIcons.alertCircle,
                        label: 'Overdue',
                        path: AppRoutes.overdue,
                        isActive: currentPath == AppRoutes.overdue,
                        badgeColor: AppTheme.errorRed,
                      ),
                      _NavItem(
                        icon: LucideIcons.listTodo,
                        label: 'Tasks',
                        path: AppRoutes.tasks,
                        isActive: currentPath == AppRoutes.tasks,
                      ),

                      const SizedBox(height: 16),
                      const Divider(),
                      const SizedBox(height: 8),

                      // Projects Section
                      InkWell(
                        onTap: () {
                          setState(() {
                            _isProjectsExpanded = !_isProjectsExpanded;
                          });
                        },
                        borderRadius: BorderRadius.circular(8),
                        child: Padding(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 12,
                            vertical: 8,
                          ),
                          child: Row(
                            children: [
                              Icon(
                                _isProjectsExpanded
                                    ? LucideIcons.chevronDown
                                    : LucideIcons.chevronRight,
                                size: 16,
                                color: AppTheme.gray500,
                              ),
                              const SizedBox(width: 8),
                              Text(
                                'Projects',
                                style: Theme.of(context)
                                    .textTheme
                                    .labelMedium
                                    ?.copyWith(
                                      color: AppTheme.gray500,
                                      fontWeight: FontWeight.w600,
                                    ),
                              ),
                            ],
                          ),
                        ),
                      ),

                      if (_isProjectsExpanded) ...[
                        // TODO: Load projects from provider
                        _ProjectItem(
                          name: 'Inbox',
                          color: AppTheme.gray500,
                          icon: LucideIcons.inbox,
                          onTap: () =>
                              context.go('${AppRoutes.tasks}?projectId=inbox'),
                        ),
                        _ProjectItem(
                          name: 'Work',
                          color: AppTheme.primaryBlue,
                          onTap: () =>
                              context.go('${AppRoutes.tasks}?projectId=work'),
                        ),
                        _ProjectItem(
                          name: 'Personal',
                          color: AppTheme.accentPink,
                          onTap: () => context
                              .go('${AppRoutes.tasks}?projectId=personal'),
                        ),
                      ],
                    ],
                  ),
                ),

                // User/Profile Section
                Container(
                  padding: const EdgeInsets.all(16),
                  decoration: BoxDecoration(
                    border: Border(
                      top: BorderSide(color: Theme.of(context).dividerColor),
                    ),
                  ),
                  child: InkWell(
                    onTap: () => context.go(AppRoutes.profile),
                    borderRadius: BorderRadius.circular(8),
                    child: Row(
                      children: [
                        CircleAvatar(
                          radius: 16,
                          backgroundColor: AppTheme.primaryBlue,
                          child: const Icon(
                            LucideIcons.user,
                            size: 16,
                            color: Colors.white,
                          ),
                        ),
                        const SizedBox(width: 12),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                'Profile',
                                style: Theme.of(context).textTheme.titleSmall,
                              ),
                              Text(
                                'Connected',
                                style: Theme.of(context)
                                    .textTheme
                                    .bodySmall
                                    ?.copyWith(
                                      color: AppTheme.successGreen,
                                    ),
                              ),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ],
            ),
          ),

          // Main Content
          Expanded(
            child: widget.child,
          ),
        ],
      ),
      ),
    );
  }
}

class _NavItem extends StatelessWidget {
  final IconData icon;
  final String label;
  final String path;
  final bool isActive;
  final Color? badgeColor;

  const _NavItem({
    required this.icon,
    required this.label,
    required this.path,
    this.isActive = false,
    this.badgeColor,
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
          onTap: () => context.go(path),
          borderRadius: BorderRadius.circular(8),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
            child: Row(
              children: [
                Icon(
                  icon,
                  size: 20,
                  color: isActive ? AppTheme.primaryBlue : AppTheme.gray600,
                ),
                const SizedBox(width: 12),
                Text(
                  label,
                  style: TextStyle(
                    fontSize: 14,
                    fontWeight: isActive ? FontWeight.w600 : FontWeight.w500,
                    color: isActive ? AppTheme.primaryBlue : AppTheme.gray700,
                  ),
                ),
                if (badgeColor != null) ...[
                  const Spacer(),
                  Container(
                    width: 8,
                    height: 8,
                    decoration: BoxDecoration(
                      color: badgeColor,
                      shape: BoxShape.circle,
                    ),
                  ),
                ],
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _ProjectItem extends StatelessWidget {
  final String name;
  final Color color;
  final IconData? icon;
  final VoidCallback onTap;

  const _ProjectItem({
    required this.name,
    required this.color,
    this.icon,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(left: 24, top: 2, bottom: 2),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(8),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          child: Row(
            children: [
              if (icon != null)
                Icon(icon, size: 16, color: color)
              else
                Container(
                  width: 12,
                  height: 12,
                  decoration: BoxDecoration(
                    color: color,
                    borderRadius: BorderRadius.circular(3),
                  ),
                ),
              const SizedBox(width: 12),
              Text(
                name,
                style: Theme.of(context).textTheme.bodyMedium,
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _SourceOption {
  final TaskSource source;
  final String label;
  final IconData icon;
  final Color color;

  const _SourceOption({
    required this.source,
    required this.label,
    required this.icon,
    required this.color,
  });
}
