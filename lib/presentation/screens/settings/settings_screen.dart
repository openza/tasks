import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';

class SettingsScreen extends ConsumerStatefulWidget {
  const SettingsScreen({super.key});

  @override
  ConsumerState<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends ConsumerState<SettingsScreen> {
  String _selectedCategory = 'provider';

  final List<Map<String, dynamic>> _categories = [
    {'id': 'provider', 'label': 'Active Provider', 'icon': LucideIcons.toggleRight},
    {'id': 'todoist', 'label': 'Todoist', 'icon': LucideIcons.checkCircle},
    {'id': 'mstodo', 'label': 'Microsoft To-Do', 'icon': LucideIcons.layoutGrid},
    {'id': 'about', 'label': 'About', 'icon': LucideIcons.info},
  ];

  @override
  Widget build(BuildContext context) {
    return Container(
      color: AppTheme.gray50,
      child: Center(
        child: Card(
          margin: const EdgeInsets.all(24),
          child: Container(
            constraints: const BoxConstraints(maxWidth: 800, maxHeight: 600),
            child: Row(
              children: [
                // Sidebar
                Container(
                  width: 200,
                  decoration: BoxDecoration(
                    border: Border(
                      right: BorderSide(color: Theme.of(context).dividerColor),
                    ),
                  ),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Padding(
                        padding: const EdgeInsets.all(20),
                        child: Text(
                          'Settings',
                          style: Theme.of(context).textTheme.titleLarge,
                        ),
                      ),
                      const Divider(height: 1),
                      Expanded(
                        child: ListView(
                          padding: const EdgeInsets.all(8),
                          children: _categories.map((cat) {
                            final isSelected = _selectedCategory == cat['id'];
                            return Padding(
                              padding: const EdgeInsets.symmetric(vertical: 2),
                              child: Material(
                                color: isSelected
                                    ? AppTheme.primaryBlue.withValues(alpha: 0.1)
                                    : Colors.transparent,
                                borderRadius: BorderRadius.circular(8),
                                child: InkWell(
                                  onTap: () => setState(
                                      () => _selectedCategory = cat['id']),
                                  borderRadius: BorderRadius.circular(8),
                                  child: Padding(
                                    padding: const EdgeInsets.symmetric(
                                        horizontal: 12, vertical: 10),
                                    child: Row(
                                      children: [
                                        Icon(
                                          cat['icon'] as IconData,
                                          size: 18,
                                          color: isSelected
                                              ? AppTheme.primaryBlue
                                              : AppTheme.gray600,
                                        ),
                                        const SizedBox(width: 12),
                                        Text(
                                          cat['label'] as String,
                                          style: TextStyle(
                                            fontSize: 14,
                                            fontWeight: isSelected
                                                ? FontWeight.w600
                                                : FontWeight.w400,
                                            color: isSelected
                                                ? AppTheme.primaryBlue
                                                : AppTheme.gray700,
                                          ),
                                        ),
                                      ],
                                    ),
                                  ),
                                ),
                              ),
                            );
                          }).toList(),
                        ),
                      ),
                    ],
                  ),
                ),

                // Content
                Expanded(
                  child: _buildContent(context),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildContent(BuildContext context) {
    switch (_selectedCategory) {
      case 'provider':
        return _buildProviderContent(context);
      case 'todoist':
        return _buildTodoistContent(context);
      case 'mstodo':
        return _buildMsToDoContent(context);
      case 'about':
        return _buildAboutContent(context);
      default:
        return const SizedBox.shrink();
    }
  }

  Widget _buildProviderContent(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Active Provider',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 8),
          Text(
            'Select which task provider to use as the primary source.',
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: AppTheme.gray500,
                ),
          ),
          const SizedBox(height: 24),
          // TODO: Replace with actual provider selection
          _ProviderOption(
            name: 'Todoist',
            isConnected: true,
            isSelected: true,
            onSelect: () {},
          ),
          const SizedBox(height: 12),
          _ProviderOption(
            name: 'Microsoft To-Do',
            isConnected: false,
            isSelected: false,
            onSelect: () {},
          ),
        ],
      ),
    );
  }

  Widget _buildTodoistContent(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Todoist',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 8),
          Text(
            'Manage your Todoist connection.',
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: AppTheme.gray500,
                ),
          ),
          const SizedBox(height: 24),
          Row(
            children: [
              const Icon(LucideIcons.checkCircle2,
                  size: 20, color: AppTheme.successGreen),
              const SizedBox(width: 8),
              Text(
                'Connected',
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: AppTheme.successGreen,
                      fontWeight: FontWeight.w500,
                    ),
              ),
            ],
          ),
          const SizedBox(height: 16),
          OutlinedButton.icon(
            onPressed: () {},
            icon: const Icon(LucideIcons.logOut, size: 16),
            label: const Text('Disconnect'),
            style: OutlinedButton.styleFrom(
              foregroundColor: AppTheme.errorRed,
              side: const BorderSide(color: AppTheme.errorRed),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildMsToDoContent(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Microsoft To-Do',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 8),
          Text(
            'Manage your Microsoft To-Do connection.',
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: AppTheme.gray500,
                ),
          ),
          const SizedBox(height: 24),
          Row(
            children: [
              const Icon(LucideIcons.xCircle, size: 20, color: AppTheme.gray400),
              const SizedBox(width: 8),
              Text(
                'Not Connected',
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: AppTheme.gray500,
                    ),
              ),
            ],
          ),
          const SizedBox(height: 16),
          ElevatedButton.icon(
            onPressed: () {
              // TODO: Implement MS To-Do OAuth
            },
            icon: const Icon(LucideIcons.link, size: 16),
            label: const Text('Connect'),
          ),
        ],
      ),
    );
  }

  Widget _buildAboutContent(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'About Openza',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 24),
          _AboutRow(label: 'Version', value: '1.0.0'),
          _AboutRow(label: 'License', value: 'MIT'),
          const SizedBox(height: 16),
          Text(
            'Openza is a unified task manager that brings together tasks from Todoist, Microsoft To-Do, and local storage into one seamless experience.',
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: AppTheme.gray600,
                ),
          ),
          const SizedBox(height: 24),
          Row(
            children: [
              OutlinedButton.icon(
                onPressed: () {
                  // TODO: Open GitHub
                },
                icon: const Icon(LucideIcons.github, size: 16),
                label: const Text('GitHub'),
              ),
              const SizedBox(width: 12),
              OutlinedButton.icon(
                onPressed: () {
                  // TODO: Open documentation
                },
                icon: const Icon(LucideIcons.externalLink, size: 16),
                label: const Text('Documentation'),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

class _ProviderOption extends StatelessWidget {
  final String name;
  final bool isConnected;
  final bool isSelected;
  final VoidCallback onSelect;

  const _ProviderOption({
    required this.name,
    required this.isConnected,
    required this.isSelected,
    required this.onSelect,
  });

  @override
  Widget build(BuildContext context) {
    return Material(
      color: isSelected
          ? AppTheme.primaryBlue.withValues(alpha: 0.1)
          : AppTheme.gray100,
      borderRadius: BorderRadius.circular(12),
      child: InkWell(
        onTap: isConnected ? onSelect : null,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              Container(
                width: 20,
                height: 20,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  border: Border.all(
                    color: isSelected ? AppTheme.primaryBlue : AppTheme.gray400,
                    width: 2,
                  ),
                ),
                child: isSelected
                    ? Center(
                        child: Container(
                          width: 10,
                          height: 10,
                          decoration: const BoxDecoration(
                            shape: BoxShape.circle,
                            color: AppTheme.primaryBlue,
                          ),
                        ),
                      )
                    : null,
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      name,
                      style: Theme.of(context).textTheme.titleSmall,
                    ),
                    Text(
                      isConnected ? 'Connected' : 'Not connected',
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                            color: isConnected
                                ? AppTheme.successGreen
                                : AppTheme.gray400,
                          ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _AboutRow extends StatelessWidget {
  final String label;
  final String value;

  const _AboutRow({required this.label, required this.value});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        children: [
          SizedBox(
            width: 80,
            child: Text(
              label,
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ),
          Text(
            value,
            style: Theme.of(context).textTheme.bodyMedium,
          ),
        ],
      ),
    );
  }
}
