import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';

class DashboardScreen extends ConsumerWidget {
  const DashboardScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
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

            // Stats Cards
            LayoutBuilder(
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
                  children: const [
                    _StatCard(
                      title: 'Total Tasks',
                      value: '24',
                      icon: LucideIcons.listTodo,
                      color: AppTheme.primaryBlue,
                    ),
                    _StatCard(
                      title: 'Active',
                      value: '12',
                      icon: LucideIcons.clock,
                      color: AppTheme.warningOrange,
                    ),
                    _StatCard(
                      title: 'Completed',
                      value: '8',
                      icon: LucideIcons.checkCircle,
                      color: AppTheme.successGreen,
                    ),
                    _StatCard(
                      title: 'Overdue',
                      value: '4',
                      icon: LucideIcons.alertCircle,
                      color: AppTheme.errorRed,
                    ),
                  ],
                );
              },
            ),

            const SizedBox(height: 32),

            // Tasks by Context and Energy Level
            LayoutBuilder(
              builder: (context, constraints) {
                if (constraints.maxWidth > 800) {
                  return Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Expanded(child: _buildContextCard(context)),
                      const SizedBox(width: 16),
                      Expanded(child: _buildEnergyCard(context)),
                    ],
                  );
                }
                return Column(
                  children: [
                    _buildContextCard(context),
                    const SizedBox(height: 16),
                    _buildEnergyCard(context),
                  ],
                );
              },
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildContextCard(BuildContext context) {
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
            _ContextRow(label: 'Work', count: 8, color: AppTheme.primaryBlue),
            _ContextRow(label: 'Personal', count: 5, color: AppTheme.accentPink),
            _ContextRow(label: 'Errands', count: 3, color: AppTheme.warningOrange),
            _ContextRow(label: 'Home', count: 4, color: AppTheme.successGreen),
            _ContextRow(label: 'Office', count: 4, color: AppTheme.accentPurple),
          ],
        ),
      ),
    );
  }

  Widget _buildEnergyCard(BuildContext context) {
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
            _ContextRow(label: 'Low Energy', count: 6, color: AppTheme.successGreen),
            _ContextRow(label: 'Normal', count: 8, color: AppTheme.primaryBlue),
            _ContextRow(label: 'Medium', count: 5, color: AppTheme.warningOrange),
            _ContextRow(label: 'High', count: 3, color: AppTheme.errorRed),
            _ContextRow(label: 'Peak', count: 2, color: AppTheme.accentPurple),
          ],
        ),
      ),
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
