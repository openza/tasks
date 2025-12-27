import 'dart:io' show Platform;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_router.dart';
import '../../../app/app_theme.dart';
import '../../providers/auth_provider.dart';
import '../../providers/task_provider.dart';

/// Provider for weekly completed tasks
final weeklyCompletedProvider = FutureProvider<int>((ref) async {
  final data = await ref.watch(unifiedDataProvider.future);
  final now = DateTime.now();
  final weekStart = now.subtract(Duration(days: now.weekday - 1));
  final weekStartDate = DateTime(weekStart.year, weekStart.month, weekStart.day);

  return data.tasks.where((t) {
    if (!t.isCompleted || t.completedAt == null) return false;
    return t.completedAt!.isAfter(weekStartDate);
  }).length;
});

/// Provider for today's completed tasks
final todayCompletedProvider = FutureProvider<int>((ref) async {
  final data = await ref.watch(unifiedDataProvider.future);
  final now = DateTime.now();
  final todayStart = DateTime(now.year, now.month, now.day);

  return data.tasks.where((t) {
    if (!t.isCompleted || t.completedAt == null) return false;
    return t.completedAt!.isAfter(todayStart);
  }).length;
});

/// Provider for priority distribution
final priorityDistributionProvider = FutureProvider<Map<int, int>>((ref) async {
  final data = await ref.watch(unifiedDataProvider.future);
  final activeTasks = data.tasks.where((t) => !t.isCompleted).toList();

  final distribution = <int, int>{1: 0, 2: 0, 3: 0, 4: 0};
  for (final task in activeTasks) {
    distribution[task.priority] = (distribution[task.priority] ?? 0) + 1;
  }
  return distribution;
});

class ProfileScreen extends ConsumerWidget {
  const ProfileScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final authState = ref.watch(authProvider);
    final statsAsync = ref.watch(taskStatisticsProvider);
    final weeklyCompleted = ref.watch(weeklyCompletedProvider);
    final todayCompleted = ref.watch(todayCompletedProvider);
    final priorityDist = ref.watch(priorityDistributionProvider);
    final isDark = Theme.of(context).brightness == Brightness.dark;

    return Container(
      color: isDark ? AppTheme.gray900 : Colors.white,
      child: SingleChildScrollView(
        padding: const EdgeInsets.all(24),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Header
            Text(
              'Profile',
              style: Theme.of(context).textTheme.headlineMedium?.copyWith(
                    fontWeight: FontWeight.bold,
                  ),
            ),
            const SizedBox(height: 8),
            Text(
              'Your productivity overview and connected accounts',
              style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                    color: AppTheme.gray500,
                  ),
            ),
            const SizedBox(height: 32),

            // Connected Accounts Card
            _buildSectionCard(
              context,
              title: 'Connected Accounts',
              icon: LucideIcons.link,
              child: Column(
                children: [
                  _buildAccountRow(
                    context,
                    icon: LucideIcons.checkSquare,
                    name: 'Todoist',
                    color: const Color(0xFFE44332),
                    isConnected: authState.todoistAuthenticated,
                  ),
                  const Divider(height: 24),
                  _buildAccountRow(
                    context,
                    icon: LucideIcons.checkCircle,
                    name: 'Microsoft To-Do',
                    color: const Color(0xFF3B82F6),
                    isConnected: authState.msToDoAuthenticated,
                  ),
                  const Divider(height: 24),
                  _buildAccountRow(
                    context,
                    icon: LucideIcons.database,
                    name: 'Local Database',
                    color: AppTheme.gray600,
                    isConnected: true,
                    alwaysActive: true,
                  ),
                ],
              ),
            ),
            const SizedBox(height: 24),

            // Statistics Grid
            statsAsync.when(
              data: (stats) => _buildStatisticsSection(
                context,
                stats: stats,
                weeklyCompleted: weeklyCompleted.value ?? 0,
                todayCompleted: todayCompleted.value ?? 0,
              ),
              loading: () => const Center(
                child: Padding(
                  padding: EdgeInsets.all(32),
                  child: CircularProgressIndicator(),
                ),
              ),
              error: (e, _) => _buildErrorCard(context, 'Failed to load statistics'),
            ),
            const SizedBox(height: 24),

            // Priority Distribution
            priorityDist.when(
              data: (distribution) => _buildPrioritySection(context, distribution),
              loading: () => const SizedBox.shrink(),
              error: (_, __) => const SizedBox.shrink(),
            ),
            const SizedBox(height: 24),

            // Quick Actions
            _buildSectionCard(
              context,
              title: 'Quick Actions',
              icon: LucideIcons.zap,
              child: Column(
                children: [
                  _buildActionRow(
                    context,
                    icon: LucideIcons.refreshCw,
                    title: 'Sync All Data',
                    subtitle: 'Refresh tasks from all connected sources',
                    onTap: () => _syncAllData(context, ref),
                  ),
                  const Divider(height: 24),
                  _buildActionRow(
                    context,
                    icon: LucideIcons.download,
                    title: 'Export Tasks',
                    subtitle: 'Download all tasks as JSON',
                    onTap: () => _showExportDialog(context),
                  ),
                  const Divider(height: 24),
                  _buildActionRow(
                    context,
                    icon: LucideIcons.settings,
                    title: 'Settings',
                    subtitle: 'Configure app preferences',
                    onTap: () => context.go(AppRoutes.settings),
                  ),
                ],
              ),
            ),
            const SizedBox(height: 24),

            // App Info
            _buildSectionCard(
              context,
              title: 'About Openza',
              icon: LucideIcons.info,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  _buildInfoRow(context, 'Version', '1.0.0'),
                  const SizedBox(height: 12),
                  _buildInfoRow(context, 'Platform', _getPlatform()),
                  const SizedBox(height: 12),
                  _buildInfoRow(context, 'Framework', 'Flutter Desktop'),
                  const SizedBox(height: 16),
                  Text(
                    'Openza is a unified task management app that brings together your tasks from Todoist, Microsoft To-Do, and local storage into one beautiful interface.',
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AppTheme.gray500,
                        ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildSectionCard(
    BuildContext context, {
    required String title,
    required IconData icon,
    required Widget child,
  }) {
    return Card(
      elevation: 0,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: BorderSide(color: AppTheme.gray200),
      ),
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(icon, size: 20, color: AppTheme.primaryBlue),
                const SizedBox(width: 8),
                Text(
                  title,
                  style: Theme.of(context).textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                ),
              ],
            ),
            const SizedBox(height: 16),
            child,
          ],
        ),
      ),
    );
  }

  Widget _buildAccountRow(
    BuildContext context, {
    required IconData icon,
    required String name,
    required Color color,
    required bool isConnected,
    bool alwaysActive = false,
  }) {
    return Row(
      children: [
        Container(
          padding: const EdgeInsets.all(8),
          decoration: BoxDecoration(
            color: color.withValues(alpha: 0.1),
            borderRadius: BorderRadius.circular(8),
          ),
          child: Icon(icon, size: 20, color: color),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                name,
                style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                      fontWeight: FontWeight.w500,
                    ),
              ),
              Text(
                alwaysActive ? 'Always active' : (isConnected ? 'Connected' : 'Not connected'),
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: isConnected ? AppTheme.green600 : AppTheme.gray500,
                    ),
              ),
            ],
          ),
        ),
        Container(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
          decoration: BoxDecoration(
            color: isConnected
                ? AppTheme.green600.withValues(alpha: 0.1)
                : AppTheme.gray200,
            borderRadius: BorderRadius.circular(12),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(
                isConnected ? LucideIcons.check : LucideIcons.x,
                size: 14,
                color: isConnected ? AppTheme.green600 : AppTheme.gray500,
              ),
              const SizedBox(width: 4),
              Text(
                isConnected ? 'Active' : 'Inactive',
                style: Theme.of(context).textTheme.labelSmall?.copyWith(
                      color: isConnected ? AppTheme.green600 : AppTheme.gray500,
                      fontWeight: FontWeight.w500,
                    ),
              ),
            ],
          ),
        ),
      ],
    );
  }

  Widget _buildStatisticsSection(
    BuildContext context, {
    required Map<String, int> stats,
    required int weeklyCompleted,
    required int todayCompleted,
  }) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Icon(LucideIcons.barChart3, size: 20, color: AppTheme.primaryBlue),
            const SizedBox(width: 8),
            Text(
              'Statistics',
              style: Theme.of(context).textTheme.titleMedium?.copyWith(
                    fontWeight: FontWeight.w600,
                  ),
            ),
          ],
        ),
        const SizedBox(height: 16),
        LayoutBuilder(
          builder: (context, constraints) {
            final cardWidth = (constraints.maxWidth - 32) / 3;
            return Wrap(
              spacing: 16,
              runSpacing: 16,
              children: [
                _buildStatCard(
                  context,
                  title: 'Total Tasks',
                  value: stats['total']?.toString() ?? '0',
                  icon: LucideIcons.listTodo,
                  color: AppTheme.primaryBlue,
                  width: cardWidth,
                ),
                _buildStatCard(
                  context,
                  title: 'Active',
                  value: stats['active']?.toString() ?? '0',
                  icon: LucideIcons.clock,
                  color: AppTheme.amber500,
                  width: cardWidth,
                ),
                _buildStatCard(
                  context,
                  title: 'Completed',
                  value: stats['completed']?.toString() ?? '0',
                  icon: LucideIcons.checkCircle2,
                  color: AppTheme.green600,
                  width: cardWidth,
                ),
                _buildStatCard(
                  context,
                  title: 'Overdue',
                  value: stats['overdue']?.toString() ?? '0',
                  icon: LucideIcons.alertCircle,
                  color: AppTheme.red500,
                  width: cardWidth,
                ),
                _buildStatCard(
                  context,
                  title: 'Due Today',
                  value: stats['today']?.toString() ?? '0',
                  icon: LucideIcons.calendar,
                  color: AppTheme.purple500,
                  width: cardWidth,
                ),
                _buildStatCard(
                  context,
                  title: 'Done Today',
                  value: todayCompleted.toString(),
                  icon: LucideIcons.trophy,
                  color: AppTheme.teal500,
                  width: cardWidth,
                ),
              ],
            );
          },
        ),
        const SizedBox(height: 16),
        Card(
          elevation: 0,
          color: AppTheme.green50,
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(12),
            side: BorderSide(color: AppTheme.green200),
          ),
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Row(
              children: [
                Container(
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: AppTheme.green100,
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Icon(
                    LucideIcons.trendingUp,
                    color: AppTheme.green600,
                    size: 24,
                  ),
                ),
                const SizedBox(width: 16),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Weekly Progress',
                        style: Theme.of(context).textTheme.titleSmall?.copyWith(
                              fontWeight: FontWeight.w600,
                              color: AppTheme.green700,
                            ),
                      ),
                      const SizedBox(height: 4),
                      Text(
                        'You completed $weeklyCompleted tasks this week!',
                        style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                              color: AppTheme.green600,
                            ),
                      ),
                    ],
                  ),
                ),
                Text(
                  weeklyCompleted.toString(),
                  style: Theme.of(context).textTheme.headlineMedium?.copyWith(
                        fontWeight: FontWeight.bold,
                        color: AppTheme.green600,
                      ),
                ),
              ],
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildStatCard(
    BuildContext context, {
    required String title,
    required String value,
    required IconData icon,
    required Color color,
    required double width,
  }) {
    return SizedBox(
      width: width,
      child: Card(
        elevation: 0,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(12),
          side: BorderSide(color: AppTheme.gray200),
        ),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Icon(icon, size: 18, color: color),
                  const Spacer(),
                  Container(
                    padding: const EdgeInsets.all(4),
                    decoration: BoxDecoration(
                      color: color.withValues(alpha: 0.1),
                      borderRadius: BorderRadius.circular(4),
                    ),
                    child: Icon(icon, size: 12, color: color),
                  ),
                ],
              ),
              const SizedBox(height: 12),
              Text(
                value,
                style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                      fontWeight: FontWeight.bold,
                    ),
              ),
              const SizedBox(height: 4),
              Text(
                title,
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: AppTheme.gray500,
                    ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildPrioritySection(
    BuildContext context,
    Map<int, int> distribution,
  ) {
    final total = distribution.values.fold(0, (sum, count) => sum + count);
    if (total == 0) return const SizedBox.shrink();

    return _buildSectionCard(
      context,
      title: 'Priority Distribution',
      icon: LucideIcons.layers,
      child: Column(
        children: [
          _buildPriorityBar(context, 'High', distribution[1] ?? 0, total, AppTheme.red500),
          const SizedBox(height: 12),
          _buildPriorityBar(context, 'Medium', distribution[2] ?? 0, total, AppTheme.amber500),
          const SizedBox(height: 12),
          _buildPriorityBar(context, 'Normal', distribution[3] ?? 0, total, AppTheme.primaryBlue),
          const SizedBox(height: 12),
          _buildPriorityBar(context, 'Low', distribution[4] ?? 0, total, AppTheme.gray400),
        ],
      ),
    );
  }

  Widget _buildPriorityBar(
    BuildContext context,
    String label,
    int count,
    int total,
    Color color,
  ) {
    final percentage = total > 0 ? (count / total) : 0.0;

    return Row(
      children: [
        SizedBox(
          width: 60,
          child: Text(
            label,
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: AppTheme.gray600,
                ),
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: ClipRRect(
            borderRadius: BorderRadius.circular(4),
            child: LinearProgressIndicator(
              value: percentage,
              backgroundColor: AppTheme.gray100,
              valueColor: AlwaysStoppedAnimation(color),
              minHeight: 8,
            ),
          ),
        ),
        const SizedBox(width: 12),
        SizedBox(
          width: 40,
          child: Text(
            count.toString(),
            textAlign: TextAlign.end,
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  fontWeight: FontWeight.w600,
                ),
          ),
        ),
      ],
    );
  }

  Widget _buildActionRow(
    BuildContext context, {
    required IconData icon,
    required String title,
    required String subtitle,
    required VoidCallback onTap,
  }) {
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(8),
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 4),
        child: Row(
          children: [
            Container(
              padding: const EdgeInsets.all(8),
              decoration: BoxDecoration(
                color: AppTheme.gray100,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Icon(icon, size: 20, color: AppTheme.gray600),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    title,
                    style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                          fontWeight: FontWeight.w500,
                        ),
                  ),
                  Text(
                    subtitle,
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AppTheme.gray500,
                        ),
                  ),
                ],
              ),
            ),
            Icon(LucideIcons.chevronRight, size: 20, color: AppTheme.gray400),
          ],
        ),
      ),
    );
  }

  Widget _buildInfoRow(BuildContext context, String label, String value) {
    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        Text(
          label,
          style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                color: AppTheme.gray500,
              ),
        ),
        Text(
          value,
          style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                fontWeight: FontWeight.w500,
              ),
        ),
      ],
    );
  }

  Widget _buildErrorCard(BuildContext context, String message) {
    return Card(
      elevation: 0,
      color: AppTheme.red50,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: BorderSide(color: AppTheme.red200),
      ),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(LucideIcons.alertTriangle, color: AppTheme.red500),
            const SizedBox(width: 12),
            Text(
              message,
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AppTheme.red700,
                  ),
            ),
          ],
        ),
      ),
    );
  }

  String _getPlatform() {
    if (Platform.isLinux) return 'Linux';
    if (Platform.isWindows) return 'Windows';
    if (Platform.isMacOS) return 'macOS';
    return 'Desktop';
  }

  void _syncAllData(BuildContext context, WidgetRef ref) {
    // Invalidate all data providers to refresh
    ref.invalidate(localTasksProvider);
    ref.invalidate(localProjectsProvider);
    ref.invalidate(localLabelsProvider);
    ref.invalidate(todoistTasksProvider);
    ref.invalidate(todoistProjectsProvider);
    ref.invalidate(todoistLabelsProvider);
    ref.invalidate(msToDoTasksProvider);
    ref.invalidate(msToDoProjectsProvider);
    ref.invalidate(unifiedDataProvider);
    ref.invalidate(taskStatisticsProvider);

    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: const Row(
          children: [
            Icon(LucideIcons.refreshCw, color: Colors.white, size: 18),
            SizedBox(width: 8),
            Text('Syncing data from all sources...'),
          ],
        ),
        backgroundColor: AppTheme.primaryBlue,
        behavior: SnackBarBehavior.floating,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
      ),
    );
  }

  void _showExportDialog(BuildContext context) {
    showDialog(
      context: context,
      builder: (context) => AlertDialog(
        title: const Row(
          children: [
            Icon(LucideIcons.download, size: 20),
            SizedBox(width: 8),
            Text('Export Tasks'),
          ],
        ),
        content: const Text(
          'Export functionality will be available in a future update. '
          'This will allow you to download all your tasks as JSON or CSV.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('OK'),
          ),
        ],
      ),
    );
  }
}
