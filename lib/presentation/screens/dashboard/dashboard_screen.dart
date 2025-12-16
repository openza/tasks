import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../providers/task_provider.dart';

class DashboardScreen extends ConsumerWidget {
  const DashboardScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final statsAsync = ref.watch(taskStatisticsProvider);

    return Container(
      decoration: const BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: [
            Color(0xFFEFF6FF), // blue-50
            Color(0xFFFDF2F8), // pink-50
            Color(0xFFF3E8FF), // purple-100
          ],
        ),
      ),
      child: SingleChildScrollView(
        padding: const EdgeInsets.all(24),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Header
            Text(
              'Dashboard',
              style: Theme.of(context).textTheme.headlineMedium,
            ),
            const SizedBox(height: 8),
            Text(
              'Welcome back! Here\'s your task overview.',
              style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                    color: AppTheme.gray500,
                  ),
            ),
            const SizedBox(height: 24),

            // Stats Cards - skipLoadingOnRefresh keeps previous data during refresh
            statsAsync.when(
              skipLoadingOnRefresh: true,
              data: (stats) => _buildStatsGrid(context, stats),
              loading: () => _buildStatsGrid(context, {
                'total': 0,
                'active': 0,
                'completed': 0,
                'overdue': 0,
              }),
              error: (_, __) => _buildStatsGrid(context, {
                'total': 0,
                'active': 0,
                'completed': 0,
                'overdue': 0,
              }),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildStatsGrid(BuildContext context, Map<String, int> stats) {
    return LayoutBuilder(
      builder: (context, constraints) {
        final crossAxisCount = constraints.maxWidth > 1000
            ? 4
            : constraints.maxWidth > 600
                ? 2
                : 1;
        return GridView.count(
          crossAxisCount: crossAxisCount,
          shrinkWrap: true,
          physics: const NeverScrollableScrollPhysics(),
          mainAxisSpacing: 16,
          crossAxisSpacing: 16,
          childAspectRatio: 1.5,
          children: [
            _StatCard(
              title: 'Total Tasks',
              value: stats['total']?.toString() ?? '0',
              icon: LucideIcons.listTodo,
              color: AppTheme.primaryBlue,
            ),
            _StatCard(
              title: 'Active',
              value: stats['active']?.toString() ?? '0',
              icon: LucideIcons.clock,
              color: AppTheme.warningOrange,
            ),
            _StatCard(
              title: 'Completed',
              value: stats['completed']?.toString() ?? '0',
              icon: LucideIcons.checkCircle,
              color: AppTheme.successGreen,
            ),
            _StatCard(
              title: 'Overdue',
              value: stats['overdue']?.toString() ?? '0',
              icon: LucideIcons.alertCircle,
              color: AppTheme.errorRed,
            ),
          ],
        );
      },
    );
  }

}

class _StatCard extends StatelessWidget {
  final String title;
  final String value;
  final IconData icon;
  final Color color;

  const _StatCard({
    required this.title,
    required this.value,
    required this.icon,
    required this.color,
  });

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Container(
                  padding: const EdgeInsets.all(10),
                  decoration: BoxDecoration(
                    color: color.withValues(alpha: 0.1),
                    borderRadius: BorderRadius.circular(10),
                  ),
                  child: Icon(icon, color: color, size: 24),
                ),
              ],
            ),
            Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  value,
                  style: Theme.of(context).textTheme.headlineMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                ),
                Text(
                  title,
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

