import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';

class OverdueScreen extends ConsumerWidget {
  const OverdueScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Container(
      color: AppTheme.gray50,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Header with red accent for overdue
          Container(
            padding: const EdgeInsets.all(24),
            decoration: BoxDecoration(
              color: Theme.of(context).colorScheme.surface,
              border: Border(
                bottom: BorderSide(color: Theme.of(context).dividerColor),
              ),
            ),
            child: Row(
              children: [
                Icon(LucideIcons.alertCircle, size: 24, color: AppTheme.errorRed),
                const SizedBox(width: 12),
                Text(
                  'Overdue',
                  style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                        color: AppTheme.errorRed,
                      ),
                ),
                const SizedBox(width: 12),
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                  decoration: BoxDecoration(
                    color: AppTheme.errorRed.withValues(alpha: 0.1),
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Text(
                    '4 tasks',
                    style: TextStyle(
                      fontSize: 12,
                      color: AppTheme.errorRed,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ),
                const Spacer(),
              ],
            ),
          ),

          // Task List
          Expanded(
            child: _buildPlaceholderList(context),
          ),
        ],
      ),
    );
  }

  Widget _buildPlaceholderList(BuildContext context) {
    return ListView.builder(
      padding: const EdgeInsets.all(16),
      itemCount: 4,
      itemBuilder: (context, index) {
        final daysOverdue = index + 1;
        return Card(
          margin: const EdgeInsets.only(bottom: 8),
          child: ListTile(
            leading: Checkbox(
              value: false,
              onChanged: (value) {},
            ),
            title: Text('Overdue task ${index + 1}'),
            subtitle: Text(
              'This task was due $daysOverdue day${daysOverdue > 1 ? 's' : ''} ago',
              style: Theme.of(context).textTheme.bodySmall,
            ),
            trailing: Container(
              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
              decoration: BoxDecoration(
                color: AppTheme.errorRed.withValues(alpha: 0.1),
                borderRadius: BorderRadius.circular(4),
              ),
              child: Text(
                '$daysOverdue day${daysOverdue > 1 ? 's' : ''} ago',
                style: TextStyle(
                  fontSize: 12,
                  color: AppTheme.errorRed,
                  fontWeight: FontWeight.w500,
                ),
              ),
            ),
          ),
        );
      },
    );
  }
}
