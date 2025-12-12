import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';

class NextActionsScreen extends ConsumerStatefulWidget {
  const NextActionsScreen({super.key});

  @override
  ConsumerState<NextActionsScreen> createState() => _NextActionsScreenState();
}

class _NextActionsScreenState extends ConsumerState<NextActionsScreen> {
  String? _selectedLabel;

  // Placeholder labels - will be replaced with actual data
  final List<Map<String, dynamic>> _labels = [
    {'name': 'urgent', 'color': AppTheme.errorRed, 'count': 3},
    {'name': 'important', 'color': AppTheme.warningOrange, 'count': 5},
    {'name': 'quick-win', 'color': AppTheme.successGreen, 'count': 4},
    {'name': 'waiting', 'color': AppTheme.primaryBlue, 'count': 2},
  ];

  @override
  Widget build(BuildContext context) {
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
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    const Icon(LucideIcons.star, size: 24),
                    const SizedBox(width: 12),
                    Text(
                      'Next Actions',
                      style: Theme.of(context).textTheme.headlineSmall,
                    ),
                  ],
                ),
                const SizedBox(height: 16),

                // Label filters
                Wrap(
                  spacing: 8,
                  runSpacing: 8,
                  children: [
                    _LabelFilterChip(
                      label: 'All Labels',
                      count: _labels.fold(0, (sum, l) => sum + (l['count'] as int)),
                      isSelected: _selectedLabel == null,
                      onTap: () => setState(() => _selectedLabel = null),
                    ),
                    ..._labels.map((label) => _LabelFilterChip(
                          label: label['name'] as String,
                          count: label['count'] as int,
                          color: label['color'] as Color,
                          isSelected: _selectedLabel == label['name'],
                          onTap: () => setState(
                              () => _selectedLabel = label['name'] as String),
                        )),
                  ],
                ),
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
    final filteredLabels = _selectedLabel != null
        ? _labels.where((l) => l['name'] == _selectedLabel).toList()
        : _labels;

    return ListView.builder(
      padding: const EdgeInsets.all(16),
      itemCount: filteredLabels.fold<int>(0, (sum, l) => sum + (l['count'] as int)),
      itemBuilder: (context, index) {
        return Card(
          margin: const EdgeInsets.only(bottom: 8),
          child: ListTile(
            leading: Checkbox(
              value: false,
              onChanged: (value) {},
            ),
            title: Text('Labeled task ${index + 1}'),
            subtitle: Row(
              children: [
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                  decoration: BoxDecoration(
                    color: AppTheme.warningOrange.withValues(alpha: 0.1),
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Text(
                    'important',
                    style: TextStyle(
                      fontSize: 11,
                      color: AppTheme.warningOrange,
                      fontWeight: FontWeight.w500,
                    ),
                  ),
                ),
              ],
            ),
          ),
        );
      },
    );
  }
}

class _LabelFilterChip extends StatelessWidget {
  final String label;
  final int count;
  final Color? color;
  final bool isSelected;
  final VoidCallback onTap;

  const _LabelFilterChip({
    required this.label,
    required this.count,
    this.color,
    required this.isSelected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Material(
      color: isSelected
          ? (color ?? AppTheme.gray700)
          : (color ?? AppTheme.gray500).withValues(alpha: 0.1),
      borderRadius: BorderRadius.circular(16),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(16),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                label,
                style: TextStyle(
                  fontSize: 13,
                  fontWeight: FontWeight.w500,
                  color: isSelected ? Colors.white : (color ?? AppTheme.gray700),
                ),
              ),
              const SizedBox(width: 6),
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                decoration: BoxDecoration(
                  color: isSelected
                      ? Colors.white.withValues(alpha: 0.2)
                      : (color ?? AppTheme.gray500).withValues(alpha: 0.2),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(
                  count.toString(),
                  style: TextStyle(
                    fontSize: 11,
                    fontWeight: FontWeight.w600,
                    color:
                        isSelected ? Colors.white : (color ?? AppTheme.gray700),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
