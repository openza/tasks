import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../../app/app_theme.dart';
import '../../../core/constants/app_constants.dart';
import '../../../data/datasources/remote/todoist_api.dart';
import '../../../domain/entities/task.dart';
import '../../providers/auth_provider.dart';
import '../../providers/repository_provider.dart';
import '../../providers/task_provider.dart';
import '../../widgets/common/openza_logo.dart';

class SettingsScreen extends ConsumerStatefulWidget {
  const SettingsScreen({super.key});

  @override
  ConsumerState<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends ConsumerState<SettingsScreen> {
  String _selectedCategory = 'provider';
  bool _isConnecting = false;
  bool _isValidatingToken = false;
  String? _tokenError;

  final _todoistTokenController = TextEditingController();
  final _todoistTokenFocusNode = FocusNode();

  @override
  void dispose() {
    _todoistTokenController.dispose();
    _todoistTokenFocusNode.dispose();
    super.dispose();
  }

  final List<Map<String, dynamic>> _categories = [
    {'id': 'provider', 'label': 'Active Provider', 'icon': LucideIcons.toggleRight},
    {'id': 'todoist', 'label': 'Todoist', 'icon': LucideIcons.checkCircle},
    {'id': 'mstodo', 'label': 'Microsoft To-Do', 'icon': LucideIcons.layoutGrid},
    {'id': 'data', 'label': 'Data & Sync', 'icon': LucideIcons.refreshCw},
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
      case 'data':
        return _buildDataContent(context);
      case 'about':
        return _buildAboutContent(context);
      default:
        return const SizedBox.shrink();
    }
  }

  Widget _buildProviderContent(BuildContext context) {
    final authState = ref.watch(authProvider);
    final taskSource = ref.watch(taskSourceProvider);

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

          // Task source selection
          _buildSourceOption(
            context,
            title: 'All Sources',
            subtitle: 'Combine tasks from all connected providers',
            icon: LucideIcons.layers,
            isSelected: taskSource == TaskSource.all,
            onTap: () => ref.read(taskSourceProvider.notifier).state = TaskSource.all,
          ),
          const SizedBox(height: 12),
          _buildSourceOption(
            context,
            title: 'Local Only',
            subtitle: 'Only show locally stored tasks',
            icon: LucideIcons.database,
            isSelected: taskSource == TaskSource.local,
            onTap: () => ref.read(taskSourceProvider.notifier).state = TaskSource.local,
          ),
          const SizedBox(height: 12),
          _buildSourceOption(
            context,
            title: 'Connected Providers',
            subtitle: 'Only show tasks from connected providers',
            icon: LucideIcons.cloud,
            isSelected: taskSource == TaskSource.provider,
            onTap: () => ref.read(taskSourceProvider.notifier).state = TaskSource.provider,
          ),

          const SizedBox(height: 32),
          const Divider(),
          const SizedBox(height: 16),

          Text(
            'Connected Providers',
            style: Theme.of(context).textTheme.titleMedium,
          ),
          const SizedBox(height: 12),
          _ProviderOption(
            name: 'Todoist',
            icon: LucideIcons.checkCircle,
            color: const Color(0xFFE44332),
            isConnected: authState.todoistAuthenticated,
            isSelected: authState.activeProvider == TaskProvider.todoist,
            onSelect: () {
              if (authState.todoistAuthenticated) {
                ref.read(authProvider.notifier).setActiveProvider(TaskProvider.todoist);
              }
            },
          ),
          const SizedBox(height: 12),
          _ProviderOption(
            name: 'Microsoft To-Do',
            icon: LucideIcons.layoutGrid,
            color: const Color(0xFF00A4EF),
            isConnected: authState.msToDoAuthenticated,
            isSelected: authState.activeProvider == TaskProvider.msToDo,
            onSelect: () {
              if (authState.msToDoAuthenticated) {
                ref.read(authProvider.notifier).setActiveProvider(TaskProvider.msToDo);
              }
            },
          ),
        ],
      ),
    );
  }

  Widget _buildSourceOption(
    BuildContext context, {
    required String title,
    required String subtitle,
    required IconData icon,
    required bool isSelected,
    required VoidCallback onTap,
  }) {
    return Material(
      color: isSelected
          ? AppTheme.primaryBlue.withValues(alpha: 0.1)
          : AppTheme.gray50,
      borderRadius: BorderRadius.circular(12),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              Container(
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: isSelected
                      ? AppTheme.primaryBlue.withValues(alpha: 0.2)
                      : AppTheme.gray200,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Icon(
                  icon,
                  size: 20,
                  color: isSelected ? AppTheme.primaryBlue : AppTheme.gray500,
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      title,
                      style: TextStyle(
                        fontWeight: FontWeight.w600,
                        color: isSelected ? AppTheme.primaryBlue : AppTheme.gray900,
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
              if (isSelected)
                Icon(LucideIcons.check, size: 20, color: AppTheme.primaryBlue),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildTodoistContent(BuildContext context) {
    final authState = ref.watch(authProvider);
    final isConnected = authState.todoistAuthenticated;

    return Padding(
      padding: const EdgeInsets.all(24),
      child: SingleChildScrollView(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Container(
                  padding: const EdgeInsets.all(10),
                  decoration: BoxDecoration(
                    color: const Color(0xFFE44332).withValues(alpha: 0.1),
                    borderRadius: BorderRadius.circular(10),
                  ),
                  child: const Icon(
                    LucideIcons.checkCircle,
                    size: 24,
                    color: Color(0xFFE44332),
                  ),
                ),
                const SizedBox(width: 12),
                Text(
                  'Todoist',
                  style: Theme.of(context).textTheme.titleLarge,
                ),
              ],
            ),
            const SizedBox(height: 8),
            Text(
              'Connect to Todoist using your API token to sync tasks, projects, and labels.',
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AppTheme.gray500,
                  ),
            ),
            const SizedBox(height: 24),

            // Connection status
            Container(
              padding: const EdgeInsets.all(16),
              decoration: BoxDecoration(
                color: isConnected
                    ? AppTheme.successGreen.withValues(alpha: 0.1)
                    : AppTheme.gray100,
                borderRadius: BorderRadius.circular(12),
                border: Border.all(
                  color: isConnected
                      ? AppTheme.successGreen.withValues(alpha: 0.3)
                      : AppTheme.gray200,
                ),
              ),
              child: Row(
                children: [
                  Icon(
                    isConnected ? LucideIcons.checkCircle2 : LucideIcons.xCircle,
                    size: 20,
                    color: isConnected ? AppTheme.successGreen : AppTheme.gray400,
                  ),
                  const SizedBox(width: 8),
                  Text(
                    isConnected ? 'Connected' : 'Not Connected',
                    style: TextStyle(
                      fontWeight: FontWeight.w500,
                      color: isConnected ? AppTheme.successGreen : AppTheme.gray500,
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(height: 24),

            if (isConnected) ...[
              OutlinedButton.icon(
                onPressed: _isValidatingToken
                    ? null
                    : () async {
                        await ref.read(authProvider.notifier).signOutTodoist();
                        ref.invalidate(unifiedDataProvider);
                        _todoistTokenController.clear();
                      },
                icon: const Icon(LucideIcons.logOut, size: 16),
                label: const Text('Disconnect'),
                style: OutlinedButton.styleFrom(
                  foregroundColor: AppTheme.errorRed,
                  side: const BorderSide(color: AppTheme.errorRed),
                ),
              ),
            ] else ...[
              // API Token input
              Text(
                'API Token',
                style: Theme.of(context).textTheme.titleSmall?.copyWith(
                      fontWeight: FontWeight.w600,
                    ),
              ),
              const SizedBox(height: 8),
              TextField(
                controller: _todoistTokenController,
                focusNode: _todoistTokenFocusNode,
                obscureText: true,
                decoration: InputDecoration(
                  hintText: 'Enter your Todoist API token',
                  prefixIcon: const Icon(LucideIcons.key, size: 18),
                  suffixIcon: _todoistTokenController.text.isNotEmpty
                      ? IconButton(
                          icon: const Icon(LucideIcons.x, size: 16),
                          onPressed: () {
                            _todoistTokenController.clear();
                            setState(() => _tokenError = null);
                          },
                        )
                      : null,
                  errorText: _tokenError,
                  border: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(8),
                  ),
                ),
                onChanged: (_) => setState(() => _tokenError = null),
              ),
              const SizedBox(height: 16),
              SizedBox(
                width: double.infinity,
                child: ElevatedButton.icon(
                  onPressed: _isValidatingToken
                      ? null
                      : () => _validateAndSaveTodoistToken(),
                  icon: _isValidatingToken
                      ? const SizedBox(
                          width: 16,
                          height: 16,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            color: Colors.white,
                          ),
                        )
                      : const Icon(LucideIcons.check, size: 16),
                  label: Text(_isValidatingToken ? 'Validating...' : 'Connect'),
                  style: ElevatedButton.styleFrom(
                    backgroundColor: const Color(0xFFE44332),
                    foregroundColor: Colors.white,
                    padding: const EdgeInsets.symmetric(vertical: 12),
                  ),
                ),
              ),
              const SizedBox(height: 24),

              // Help section
              Container(
                padding: const EdgeInsets.all(16),
                decoration: BoxDecoration(
                  color: AppTheme.primaryBlue.withValues(alpha: 0.05),
                  borderRadius: BorderRadius.circular(12),
                  border: Border.all(
                    color: AppTheme.primaryBlue.withValues(alpha: 0.2),
                  ),
                ),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Icon(LucideIcons.info, size: 18, color: AppTheme.primaryBlue),
                        const SizedBox(width: 8),
                        Text(
                          'How to get your API token',
                          style: Theme.of(context).textTheme.titleSmall?.copyWith(
                                fontWeight: FontWeight.w600,
                                color: AppTheme.primaryBlue,
                              ),
                        ),
                      ],
                    ),
                    const SizedBox(height: 12),
                    Text(
                      '1. Open Todoist Settings\n'
                      '2. Go to Integrations → Developer\n'
                      '3. Copy your API token',
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                            color: AppTheme.gray600,
                            height: 1.5,
                          ),
                    ),
                    const SizedBox(height: 12),
                    OutlinedButton.icon(
                      onPressed: () => launchUrl(
                        Uri.parse('https://todoist.com/app/settings/integrations/developer'),
                      ),
                      icon: const Icon(LucideIcons.externalLink, size: 14),
                      label: const Text('Open Todoist Settings'),
                      style: OutlinedButton.styleFrom(
                        foregroundColor: AppTheme.primaryBlue,
                        side: BorderSide(color: AppTheme.primaryBlue),
                        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                        textStyle: const TextStyle(fontSize: 12),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }

  Future<void> _validateAndSaveTodoistToken() async {
    final token = _todoistTokenController.text.trim();

    if (token.isEmpty) {
      setState(() => _tokenError = 'Please enter an API token');
      return;
    }

    setState(() {
      _isValidatingToken = true;
      _tokenError = null;
    });

    try {
      // Validate the token by trying to fetch tasks
      final todoistApi = TodoistApi(accessToken: token);
      await todoistApi.getAllTasks(); // This will throw if token is invalid

      // Token is valid, save it
      await ref.read(authProvider.notifier).setTodoistAuthenticated(token);
      ref.invalidate(unifiedDataProvider);

      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: const Row(
              children: [
                Icon(LucideIcons.checkCircle2, color: Colors.white, size: 18),
                SizedBox(width: 8),
                Text('Connected to Todoist successfully!'),
              ],
            ),
            backgroundColor: AppTheme.successGreen,
            behavior: SnackBarBehavior.floating,
            shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
          ),
        );
      }
    } catch (e) {
      if (mounted) {
        setState(() => _tokenError = 'Invalid API token. Please check and try again.');
      }
    } finally {
      if (mounted) {
        setState(() => _isValidatingToken = false);
      }
    }
  }

  Widget _buildMsToDoContent(BuildContext context) {
    final authState = ref.watch(authProvider);
    final isConnected = authState.msToDoAuthenticated;

    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: const Color(0xFF00A4EF).withValues(alpha: 0.1),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: const Icon(
                  LucideIcons.layoutGrid,
                  size: 24,
                  color: Color(0xFF00A4EF),
                ),
              ),
              const SizedBox(width: 12),
              Text(
                'Microsoft To-Do',
                style: Theme.of(context).textTheme.titleLarge,
              ),
            ],
          ),
          const SizedBox(height: 8),
          Text(
            'Connect to Microsoft To-Do to sync your tasks and lists.',
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: AppTheme.gray500,
                ),
          ),
          const SizedBox(height: 24),

          // Connection status
          Container(
            padding: const EdgeInsets.all(16),
            decoration: BoxDecoration(
              color: isConnected
                  ? AppTheme.successGreen.withValues(alpha: 0.1)
                  : AppTheme.gray100,
              borderRadius: BorderRadius.circular(12),
              border: Border.all(
                color: isConnected
                    ? AppTheme.successGreen.withValues(alpha: 0.3)
                    : AppTheme.gray200,
              ),
            ),
            child: Row(
              children: [
                Icon(
                  isConnected ? LucideIcons.checkCircle2 : LucideIcons.xCircle,
                  size: 20,
                  color: isConnected ? AppTheme.successGreen : AppTheme.gray400,
                ),
                const SizedBox(width: 8),
                Text(
                  isConnected ? 'Connected' : 'Not Connected',
                  style: TextStyle(
                    fontWeight: FontWeight.w500,
                    color: isConnected ? AppTheme.successGreen : AppTheme.gray500,
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(height: 24),

          if (isConnected) ...[
            OutlinedButton.icon(
              onPressed: _isConnecting
                  ? null
                  : () async {
                      await ref.read(authProvider.notifier).signOutMsToDo();
                      ref.invalidate(unifiedDataProvider);
                    },
              icon: const Icon(LucideIcons.logOut, size: 16),
              label: const Text('Disconnect'),
              style: OutlinedButton.styleFrom(
                foregroundColor: AppTheme.errorRed,
                side: const BorderSide(color: AppTheme.errorRed),
              ),
            ),
          ] else ...[
            ElevatedButton.icon(
              onPressed: _isConnecting
                  ? null
                  : () async {
                      setState(() => _isConnecting = true);
                      try {
                        final oauthService = ref.read(oauthServiceProvider);
                        final token = await oauthService.authenticateMsToDo(
                          clientId: AppConstants.msToDoClientId,
                          tenantId: AppConstants.msToDoTenantId,
                        );
                        if (token != null && mounted) {
                          await ref.read(authProvider.notifier).setMsToDoAuthenticated(token);
                          ref.invalidate(unifiedDataProvider);
                        }
                      } finally {
                        if (mounted) setState(() => _isConnecting = false);
                      }
                    },
              icon: _isConnecting
                  ? const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(LucideIcons.link, size: 16),
              label: Text(_isConnecting ? 'Connecting...' : 'Connect to Microsoft'),
              style: ElevatedButton.styleFrom(
                backgroundColor: const Color(0xFF00A4EF),
                foregroundColor: Colors.white,
              ),
            ),
            const SizedBox(height: 16),
            Text(
              'Note: You\'ll need Microsoft Azure AD credentials configured.',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AppTheme.gray400,
                  ),
            ),
          ],
        ],
      ),
    );
  }

  Widget _buildDataContent(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Data & Sync',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 8),
          Text(
            'Manage your local data and synchronization settings.',
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: AppTheme.gray500,
                ),
          ),
          const SizedBox(height: 24),

          // Sync now
          ListTile(
            leading: Container(
              padding: const EdgeInsets.all(8),
              decoration: BoxDecoration(
                color: AppTheme.primaryBlue.withValues(alpha: 0.1),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Icon(LucideIcons.refreshCw, size: 20, color: AppTheme.primaryBlue),
            ),
            title: const Text('Sync Now'),
            subtitle: const Text('Manually sync with all connected providers'),
            trailing: const Icon(LucideIcons.chevronRight, size: 20),
            onTap: () {
              ref.invalidate(unifiedDataProvider);
              ScaffoldMessenger.of(context).showSnackBar(
                const SnackBar(content: Text('Syncing...')),
              );
            },
          ),
          const Divider(),

          // Clear cache
          ListTile(
            leading: Container(
              padding: const EdgeInsets.all(8),
              decoration: BoxDecoration(
                color: AppTheme.warningOrange.withValues(alpha: 0.1),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Icon(LucideIcons.trash2, size: 20, color: AppTheme.warningOrange),
            ),
            title: const Text('Clear Cache'),
            subtitle: const Text('Clear locally cached data'),
            trailing: const Icon(LucideIcons.chevronRight, size: 20),
            onTap: () {
              showDialog(
                context: context,
                builder: (context) => AlertDialog(
                  title: const Text('Clear Cache?'),
                  content: const Text(
                    'This will clear all locally cached data. Your tasks will be re-synced from connected providers.',
                  ),
                  actions: [
                    TextButton(
                      onPressed: () => Navigator.pop(context),
                      child: const Text('Cancel'),
                    ),
                    TextButton(
                      onPressed: () {
                        Navigator.pop(context);
                        // TODO: Implement cache clearing
                        ScaffoldMessenger.of(context).showSnackBar(
                          const SnackBar(content: Text('Cache cleared')),
                        );
                      },
                      child: const Text('Clear'),
                    ),
                  ],
                ),
              );
            },
          ),
          const Divider(),

          // Export data
          ListTile(
            leading: Container(
              padding: const EdgeInsets.all(8),
              decoration: BoxDecoration(
                color: AppTheme.successGreen.withValues(alpha: 0.1),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Icon(LucideIcons.download, size: 20, color: AppTheme.successGreen),
            ),
            title: const Text('Export Data'),
            subtitle: const Text('Export all your tasks to JSON'),
            trailing: const Icon(LucideIcons.chevronRight, size: 20),
            onTap: () {
              // TODO: Implement export
              ScaffoldMessenger.of(context).showSnackBar(
                const SnackBar(content: Text('Export coming soon')),
              );
            },
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
          Row(
            children: [
              const OpenzaLogo(size: 48),
              const SizedBox(width: 12),
              Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    'Openza',
                    style: Theme.of(context).textTheme.titleLarge?.copyWith(
                          fontWeight: FontWeight.bold,
                        ),
                  ),
                  Text(
                    'Unified Task Manager',
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AppTheme.gray500,
                        ),
                  ),
                ],
              ),
            ],
          ),
          const SizedBox(height: 24),

          _AboutRow(label: 'Version', value: '1.0.0 (Flutter)'),
          _AboutRow(label: 'License', value: 'MIT'),
          _AboutRow(label: 'Platform', value: 'Desktop (Windows, macOS, Linux)'),

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
                  launchUrl(Uri.parse('https://github.com/openza/openza-flutter'));
                },
                icon: const Icon(LucideIcons.github, size: 16),
                label: const Text('GitHub'),
              ),
              const SizedBox(width: 12),
              OutlinedButton.icon(
                onPressed: () {
                  launchUrl(Uri.parse('https://openza.dev'));
                },
                icon: const Icon(LucideIcons.externalLink, size: 16),
                label: const Text('Website'),
              ),
            ],
          ),
          const Spacer(),
          Text(
            'Made with Flutter',
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: AppTheme.gray400,
                ),
          ),
        ],
      ),
    );
  }
}

class _ProviderOption extends StatelessWidget {
  final String name;
  final IconData icon;
  final Color color;
  final bool isConnected;
  final bool isSelected;
  final VoidCallback onSelect;

  const _ProviderOption({
    required this.name,
    required this.icon,
    required this.color,
    required this.isConnected,
    required this.isSelected,
    required this.onSelect,
  });

  @override
  Widget build(BuildContext context) {
    return Material(
      color: isSelected
          ? color.withValues(alpha: 0.1)
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
                    color: isSelected ? color : AppTheme.gray400,
                    width: 2,
                  ),
                ),
                child: isSelected
                    ? Center(
                        child: Container(
                          width: 10,
                          height: 10,
                          decoration: BoxDecoration(
                            shape: BoxShape.circle,
                            color: color,
                          ),
                        ),
                      )
                    : null,
              ),
              const SizedBox(width: 12),
              Icon(icon, size: 20, color: color),
              const SizedBox(width: 8),
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
