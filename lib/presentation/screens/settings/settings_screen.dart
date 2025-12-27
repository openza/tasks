import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../../app/app_theme.dart';
import '../../../core/constants/app_constants.dart';
import '../../../data/datasources/remote/todoist_api.dart';
import '../../providers/auth_provider.dart';
import '../../providers/task_provider.dart';
import '../../providers/theme_provider.dart';
import '../../widgets/common/openza_logo.dart';

class SettingsScreen extends ConsumerStatefulWidget {
  const SettingsScreen({super.key});

  @override
  ConsumerState<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends ConsumerState<SettingsScreen> {
  String _selectedCategory = 'appearance';
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
    {'id': 'appearance', 'label': 'Appearance', 'icon': LucideIcons.palette},
    {'id': 'provider', 'label': 'Providers', 'icon': LucideIcons.layers},
    {'id': 'todoist', 'label': 'Todoist', 'icon': LucideIcons.checkCircle},
    {'id': 'mstodo', 'label': 'Microsoft To-Do', 'icon': LucideIcons.layoutGrid},
    {'id': 'data', 'label': 'Data & Sync', 'icon': LucideIcons.refreshCw},
    {'id': 'about', 'label': 'About', 'icon': LucideIcons.info},
  ];

  @override
  Widget build(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;

    return Container(
      color: isDark ? AppTheme.gray900 : Colors.white,
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
      case 'appearance':
        return _buildAppearanceContent(context);
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

  Widget _buildAppearanceContent(BuildContext context) {
    final currentTheme = ref.watch(themeModeProvider);

    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Appearance',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 8),
          Text(
            'Customize how Openza Tasks looks on your device.',
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: AppTheme.gray500,
                ),
          ),
          const SizedBox(height: 24),

          // Theme selection
          Text(
            'Theme',
            style: Theme.of(context).textTheme.titleSmall?.copyWith(
                  fontWeight: FontWeight.w600,
                ),
          ),
          const SizedBox(height: 12),

          // Theme options
          _ThemeOption(
            icon: LucideIcons.monitor,
            label: 'System',
            description: 'Follow system settings',
            isSelected: currentTheme == ThemeMode.system,
            onTap: () => ref.read(themeModeProvider.notifier).setThemeMode(ThemeMode.system),
          ),
          const SizedBox(height: 8),
          _ThemeOption(
            icon: LucideIcons.sun,
            label: 'Light',
            description: 'Always use light theme',
            isSelected: currentTheme == ThemeMode.light,
            onTap: () => ref.read(themeModeProvider.notifier).setThemeMode(ThemeMode.light),
          ),
          const SizedBox(height: 8),
          _ThemeOption(
            icon: LucideIcons.moon,
            label: 'Dark',
            description: 'Always use dark theme',
            isSelected: currentTheme == ThemeMode.dark,
            onTap: () => ref.read(themeModeProvider.notifier).setThemeMode(ThemeMode.dark),
          ),
        ],
      ),
    );
  }

  Widget _buildProviderContent(BuildContext context) {
    final authState = ref.watch(authProvider);

    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Connected Providers',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 8),
          Text(
            'Tasks from all connected providers are unified in the task list.',
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: AppTheme.gray500,
                ),
          ),
          const SizedBox(height: 24),

          _ProviderStatus(
            name: 'Todoist',
            icon: LucideIcons.checkCircle,
            color: const Color(0xFFE44332),
            isConnected: authState.todoistAuthenticated,
            onConfigure: () => setState(() => _selectedCategory = 'todoist'),
          ),
          const SizedBox(height: 12),
          _ProviderStatus(
            name: 'Microsoft To-Do',
            icon: LucideIcons.layoutGrid,
            color: const Color(0xFF00A4EF),
            isConnected: authState.msToDoAuthenticated,
            onConfigure: () => setState(() => _selectedCategory = 'mstodo'),
          ),
        ],
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

            // Sync behavior info - always visible
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
                      Icon(LucideIcons.refreshCw, size: 16, color: AppTheme.primaryBlue),
                      const SizedBox(width: 8),
                      Text(
                        'Sync Behavior',
                        style: Theme.of(context).textTheme.titleSmall?.copyWith(
                              fontWeight: FontWeight.w600,
                              color: AppTheme.primaryBlue,
                            ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 12),
                  _SyncBehaviorRow(
                    icon: LucideIcons.arrowDown,
                    label: 'From Todoist',
                    description: 'Tasks, projects, labels sync to Openza',
                  ),
                  const SizedBox(height: 8),
                  _SyncBehaviorRow(
                    icon: LucideIcons.arrowUp,
                    label: 'To Todoist',
                    description: 'Only task completion status syncs back',
                  ),
                  const SizedBox(height: 8),
                  _SyncBehaviorRow(
                    icon: LucideIcons.edit3,
                    label: 'Edits',
                    description: 'Edit tasks in Todoist app, changes sync on refresh',
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
                onChanged: (_) => setState(() {
                  _tokenError = null;
                }),
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

          // Sync behavior info - always visible
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
                    Icon(LucideIcons.refreshCw, size: 16, color: AppTheme.primaryBlue),
                    const SizedBox(width: 8),
                    Text(
                      'Sync Behavior',
                      style: Theme.of(context).textTheme.titleSmall?.copyWith(
                            fontWeight: FontWeight.w600,
                            color: AppTheme.primaryBlue,
                          ),
                    ),
                  ],
                ),
                const SizedBox(height: 12),
                _SyncBehaviorRow(
                  icon: LucideIcons.arrowDown,
                  label: 'From MS To-Do',
                  description: 'Tasks and lists sync to Openza',
                ),
                const SizedBox(height: 8),
                _SyncBehaviorRow(
                  icon: LucideIcons.arrowUp,
                  label: 'To MS To-Do',
                  description: 'Only task completion status syncs back',
                ),
                const SizedBox(height: 8),
                _SyncBehaviorRow(
                  icon: LucideIcons.edit3,
                  label: 'Edits',
                  description: 'Edit tasks in MS To-Do app, changes sync on refresh',
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
          ] else if (AppConstants.msToDoClientId.isNotEmpty) ...[
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
          ] else ...[
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: AppTheme.gray100,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Row(
                children: [
                  Icon(LucideIcons.info, size: 16, color: AppTheme.gray500),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      'Microsoft To-Do integration requires Azure AD credentials to be configured in the app build.',
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                            color: AppTheme.gray500,
                          ),
                    ),
                  ),
                ],
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

          // Clear cache - Coming Soon
          Opacity(
            opacity: 0.5,
            child: ListTile(
              leading: Container(
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: AppTheme.warningOrange.withValues(alpha: 0.1),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Icon(LucideIcons.trash2, size: 20, color: AppTheme.warningOrange),
              ),
              title: const Text('Clear Cache'),
              subtitle: const Text('Coming soon'),
              trailing: Container(
                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                decoration: BoxDecoration(
                  color: AppTheme.gray200,
                  borderRadius: BorderRadius.circular(4),
                ),
                child: Text(
                  'Soon',
                  style: TextStyle(fontSize: 11, color: AppTheme.gray500),
                ),
              ),
            ),
          ),
          const Divider(),

          // Export data - Coming Soon
          Opacity(
            opacity: 0.5,
            child: ListTile(
              leading: Container(
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: AppTheme.successGreen.withValues(alpha: 0.1),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Icon(LucideIcons.download, size: 20, color: AppTheme.successGreen),
              ),
              title: const Text('Export Data'),
              subtitle: const Text('Coming soon'),
              trailing: Container(
                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                decoration: BoxDecoration(
                  color: AppTheme.gray200,
                  borderRadius: BorderRadius.circular(4),
                ),
                child: Text(
                  'Soon',
                  style: TextStyle(fontSize: 11, color: AppTheme.gray500),
                ),
              ),
            ),
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
                    'Openza Tasks',
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

          _AboutRow(label: 'Version', value: '${AppConstants.appVersion} (Flutter + Rust)'),
          _AboutRow(label: 'License', value: 'MIT'),
          _AboutRow(label: 'Platform', value: 'Linux, Windows, macOS'),

          const SizedBox(height: 16),
          Text(
            AppConstants.appDescription,
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: AppTheme.gray600,
                ),
          ),
          const SizedBox(height: 24),
          Row(
            children: [
              OutlinedButton.icon(
                onPressed: () {
                  launchUrl(Uri.parse(AppConstants.githubUrl));
                },
                icon: const Icon(LucideIcons.github, size: 16),
                label: const Text('GitHub'),
              ),
              const SizedBox(width: 12),
              OutlinedButton.icon(
                onPressed: () {
                  launchUrl(Uri.parse(AppConstants.websiteUrl));
                },
                icon: const Icon(LucideIcons.externalLink, size: 16),
                label: const Text('Website'),
              ),
            ],
          ),
          const Spacer(),
          Text(
            'Made with Flutter & Rust',
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: AppTheme.gray400,
                ),
          ),
        ],
      ),
    );
  }
}

class _ProviderStatus extends StatelessWidget {
  final String name;
  final IconData icon;
  final Color color;
  final bool isConnected;
  final VoidCallback onConfigure;

  const _ProviderStatus({
    required this.name,
    required this.icon,
    required this.color,
    required this.isConnected,
    required this.onConfigure,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: isConnected ? color.withValues(alpha: 0.05) : AppTheme.gray100,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(
          color: isConnected ? color.withValues(alpha: 0.2) : AppTheme.gray200,
        ),
      ),
      child: Row(
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
                  style: Theme.of(context).textTheme.titleSmall,
                ),
                const SizedBox(height: 2),
                Row(
                  children: [
                    Container(
                      width: 8,
                      height: 8,
                      decoration: BoxDecoration(
                        shape: BoxShape.circle,
                        color: isConnected ? AppTheme.successGreen : AppTheme.gray400,
                      ),
                    ),
                    const SizedBox(width: 6),
                    Text(
                      isConnected ? 'Connected' : 'Not connected',
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                            color: isConnected ? AppTheme.successGreen : AppTheme.gray400,
                          ),
                    ),
                  ],
                ),
              ],
            ),
          ),
          TextButton(
            onPressed: onConfigure,
            child: Text(isConnected ? 'Manage' : 'Connect'),
          ),
        ],
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

class _SyncBehaviorRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String description;

  const _SyncBehaviorRow({
    required this.icon,
    required this.label,
    required this.description,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(icon, size: 14, color: AppTheme.gray500),
        const SizedBox(width: 8),
        Expanded(
          child: RichText(
            text: TextSpan(
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AppTheme.gray600,
                    height: 1.4,
                  ),
              children: [
                TextSpan(
                  text: '$label: ',
                  style: const TextStyle(fontWeight: FontWeight.w600),
                ),
                TextSpan(text: description),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

class _ThemeOption extends StatelessWidget {
  final IconData icon;
  final String label;
  final String description;
  final bool isSelected;
  final VoidCallback onTap;

  const _ThemeOption({
    required this.icon,
    required this.label,
    required this.description,
    required this.isSelected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Material(
      color: isSelected
          ? AppTheme.gray100
          : Colors.transparent,
      borderRadius: BorderRadius.circular(12),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Container(
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(12),
            border: Border.all(
              color: isSelected ? AppTheme.gray400 : AppTheme.gray200,
              width: isSelected ? 2 : 1,
            ),
          ),
          child: Row(
            children: [
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: isSelected
                      ? AppTheme.gray200
                      : AppTheme.gray100,
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Icon(
                  icon,
                  size: 20,
                  color: isSelected ? AppTheme.gray700 : AppTheme.gray500,
                ),
              ),
              const SizedBox(width: 16),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      label,
                      style: Theme.of(context).textTheme.titleSmall?.copyWith(
                            fontWeight: isSelected ? FontWeight.w600 : FontWeight.w500,
                            color: isSelected ? AppTheme.gray900 : AppTheme.gray700,
                          ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      description,
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                            color: AppTheme.gray500,
                          ),
                    ),
                  ],
                ),
              ),
              if (isSelected)
                Icon(
                  LucideIcons.check,
                  size: 20,
                  color: AppTheme.gray700,
                ),
            ],
          ),
        ),
      ),
    );
  }
}
