import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';

class TodayScreen extends ConsumerWidget {
  const TodayScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Container(
      color: AppTheme.gray50,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Header
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
                const Icon(LucideIcons.calendarDays, size: 24),
                const SizedBox(width: 12),
                Text(
                  'Today',
                  style: Theme.of(context).textTheme.headlineSmall,
                ),
                const Spacer(),
                // TODO: Add TaskSourceSelector here
              ],
            ),
          ),

          // Task List
          Expanded(
            child: _buildPlaceholderList(context, 'today'),
          ),
        ],
      ),
    );
  }

  Widget _buildPlaceholderList(BuildContext context, String filter) {
    // Placeholder task list - will be replaced with actual TaskList widget
    return ListView.builder(
      padding: const EdgeInsets.all(16),
      itemCount: 5,
      itemBuilder: (context, index) {
        return Card(
          margin: const EdgeInsets.only(bottom: 8),
          child: ListTile(
            leading: Checkbox(
              value: false,
              onChanged: (value) {},
            ),
            title: Text('Task ${index + 1} due today'),
            subtitle: Text(
              'This is a placeholder task description',
              style: Theme.of(context).textTheme.bodySmall,
            ),
            trailing: Container(
              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
              decoration: BoxDecoration(
                color: AppTheme.warningOrange.withValues(alpha: 0.1),
                borderRadius: BorderRadius.circular(4),
              ),
              child: Text(
                'Today',
                style: TextStyle(
                  fontSize: 12,
                  color: AppTheme.warningOrange,
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
