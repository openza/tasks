import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/task.dart';
import '../../providers/task_provider.dart';

class DashboardScreen extends ConsumerWidget {
  const DashboardScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final unifiedDataAsync = ref.watch(unifiedDataProvider);
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

            const SizedBox(height: 32),

            // Tasks by Context and Energy Level - skipLoadingOnRefresh keeps previous data
            unifiedDataAsync.when(
              skipLoadingOnRefresh: true,
              data: (data) => _buildDetailsSection(context, data.tasks),
              loading: () => _buildDetailsSection(context, []),
              error: (_, __) => _buildDetailsSection(context, []),
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

  Widget _buildDetailsSection(BuildContext context, List<TaskEntity> tasks) {
    // Calculate context statistics
    final contextStats = <TaskContext, int>{};
    final energyStats = <int, int>{};

    for (final task in tasks.where((t) => !t.isCompleted)) {
      contextStats[task.context] = (contextStats[task.context] ?? 0) + 1;
      energyStats[task.energyLevel] = (energyStats[task.energyLevel] ?? 0) + 1;
    }

    return LayoutBuilder(
      builder: (context, constraints) {
        if (constraints.maxWidth > 800) {
          return Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(child: _buildContextCard(context, contextStats)),
              const SizedBox(width: 16),
              Expanded(child: _buildEnergyCard(context, energyStats)),
            ],
          );
        }
        return Column(
          children: [
            _buildContextCard(context, contextStats),
            const SizedBox(height: 16),
            _buildEnergyCard(context, energyStats),
          ],
        );
      },
    );
  }

  Widget _buildContextCard(BuildContext context, Map<TaskContext, int> stats) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Tasks by Context',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 16),
            if (stats.isEmpty)
              Text(
                'No tasks with context assigned',
                style: TextStyle(color: AppTheme.gray400),
              )
            else
              ...TaskContext.values.where((c) => stats[c] != null).map(
                    (ctx) => _ContextRow(
                      label: ctx.displayName,
                      count: stats[ctx] ?? 0,
                      color: _getContextColor(ctx),
                    ),
                  ),
          ],
        ),
      ),
    );
  }

  Widget _buildEnergyCard(BuildContext context, Map<int, int> stats) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Tasks by Energy Level',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 16),
            _ContextRow(
              label: 'Low Energy',
              count: stats[1] ?? 0,
              color: AppTheme.energyColors[1] ?? AppTheme.successGreen,
            ),
            _ContextRow(
              label: 'Normal',
              count: stats[2] ?? 0,
              color: AppTheme.energyColors[2] ?? AppTheme.primaryBlue,
            ),
            _ContextRow(
              label: 'Medium',
              count: stats[3] ?? 0,
              color: AppTheme.energyColors[3] ?? AppTheme.warningOrange,
            ),
            _ContextRow(
              label: 'High',
              count: stats[4] ?? 0,
              color: AppTheme.energyColors[4] ?? AppTheme.errorRed,
            ),
            _ContextRow(
              label: 'Peak',
              count: stats[5] ?? 0,
              color: AppTheme.energyColors[5] ?? AppTheme.accentPurple,
            ),
          ],
        ),
      ),
    );
  }

  Color _getContextColor(TaskContext ctx) {
    switch (ctx) {
      case TaskContext.work:
        return AppTheme.primaryBlue;
      case TaskContext.personal:
        return AppTheme.accentPink;
      case TaskContext.errands:
        return AppTheme.warningOrange;
      case TaskContext.home:
        return AppTheme.successGreen;
      case TaskContext.office:
        return AppTheme.accentPurple;
      case TaskContext.anywhere:
        return AppTheme.gray500;
      case TaskContext.phone:
        return const Color(0xFF6366F1);
      case TaskContext.computer:
        return const Color(0xFF0EA5E9);
      case TaskContext.waiting:
        return const Color(0xFFEAB308);
    }
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

class _ContextRow extends StatelessWidget {
  final String label;
  final int count;
  final Color color;

  const _ContextRow({
    required this.label,
    required this.count,
    required this.color,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        children: [
          Container(
            width: 12,
            height: 12,
            decoration: BoxDecoration(
              color: color,
              borderRadius: BorderRadius.circular(3),
            ),
          ),
          const SizedBox(width: 12),
          Text(label, style: Theme.of(context).textTheme.bodyMedium),
          const Spacer(),
          Text(
            count.toString(),
            style: Theme.of(context).textTheme.titleSmall,
          ),
        ],
      ),
    );
  }
}
