import 'dart:io';

import 'package:file_selector/file_selector.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';
import 'package:toastification/toastification.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../../app/app_theme.dart';
import '../../../core/constants/app_constants.dart';
import '../../../data/datasources/remote/todoist_api.dart';
import '../../../domain/entities/backup.dart';
import '../../providers/app_info_provider.dart';
import '../../providers/auth_provider.dart';
import '../../providers/backup_provider.dart';
import '../../providers/task_provider.dart';
import '../../providers/theme_provider.dart';
import '../../widgets/common/openza_logo.dart';
import '../../widgets/dialogs/export_markdown_dialog.dart';

class SettingsScreen extends ConsumerStatefulWidget {
  const SettingsScreen({super.key});

  /// Show the settings modal dialog
  static Future<void> show(BuildContext context) {
    return showDialog(
      context: context,
      barrierDismissible: true,
      builder: (context) => const SettingsScreen(),
    );
  }

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
    {'id': 'backup', 'label': 'Backup', 'icon': LucideIcons.hardDrive},
    {'id': 'import', 'label': 'Import', 'icon': LucideIcons.folderUp},
    {'id': 'export', 'label': 'Export', 'icon': LucideIcons.fileUp},
    {'id': 'about', 'label': 'About', 'icon': LucideIcons.info},
  ];

  @override
  Widget build(BuildContext context) {
    return Dialog(
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      clipBehavior: Clip.antiAlias,
      child: SizedBox(
        width: 850,
        height: 600,
        child: Row(
          children: [
            // Sidebar
            Container(
              width: 200,
              decoration: BoxDecoration(
                color: Theme.of(context).colorScheme.surfaceContainerLow,
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
              child: Column(
                children: [
                  // Close button header
                  Container(
                    padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
                    decoration: BoxDecoration(
                      border: Border(
                        bottom: BorderSide(color: Theme.of(context).dividerColor),
                      ),
                    ),
                    child: Row(
                      children: [
                        const Spacer(),
                        IconButton(
                          icon: Icon(LucideIcons.x, size: 20, color: AppTheme.gray500),
                          onPressed: () => Navigator.of(context).pop(),
                          tooltip: 'Close',
                        ),
                      ],
                    ),
                  ),
                  // Content area
                  Expanded(
                    child: _buildContent(context),
                  ),
                ],
              ),
            ),
          ],
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
      case 'backup':
        return _buildBackupContent(context);
      case 'import':
        return _buildImportContent(context);
      case 'export':
        return _buildExportContent(context);
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
                    description: 'Tasks, projects, labels sync to Openza Tasks',
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
                  description: 'Tasks and lists sync to Openza Tasks',
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

  Widget _buildBackupContent(BuildContext context) {
    final backupState = ref.watch(backupProvider);
    final isBackingUp = backupState.status == BackupStatus.backingUp;

    return Padding(
      padding: const EdgeInsets.all(24),
      child: SingleChildScrollView(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Backup',
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: 8),
            Text(
              'Automatically backup your data and restore from previous backups.',
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AppTheme.gray500,
                  ),
            ),
            const SizedBox(height: 24),

            // Auto backup toggle
            SwitchListTile(
              secondary: Container(
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: AppTheme.primaryBlue.withValues(alpha: 0.1),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Icon(LucideIcons.clock, size: 20, color: AppTheme.primaryBlue),
              ),
              title: const Text('Automatic Backup'),
              subtitle: Text(
                backupState.autoBackupEnabled
                    ? 'Backing up ${backupState.frequency.displayName.toLowerCase()}'
                    : 'Disabled',
              ),
              value: backupState.autoBackupEnabled,
              onChanged: (value) {
                ref.read(backupProvider.notifier).setAutoBackupEnabled(value);
              },
            ),

            // Backup frequency (only show when auto-backup is enabled)
            if (backupState.autoBackupEnabled) ...[
              Padding(
                padding: const EdgeInsets.only(left: 72, right: 16, bottom: 8),
                child: Row(
                  children: [
                    Text(
                      'Frequency:',
                      style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                            color: AppTheme.gray600,
                          ),
                    ),
                    const SizedBox(width: 12),
                    DropdownButton<BackupFrequency>(
                      value: backupState.frequency,
                      underline: const SizedBox(),
                      items: BackupFrequency.values.map((freq) {
                        return DropdownMenuItem(
                          value: freq,
                          child: Text(freq.displayName),
                        );
                      }).toList(),
                      onChanged: (freq) {
                        if (freq != null) {
                          ref.read(backupProvider.notifier).setBackupFrequency(freq);
                        }
                      },
                    ),
                  ],
                ),
              ),
            ],

            // Last backup info
            if (backupState.lastBackupTime != null)
              Padding(
                padding: const EdgeInsets.only(left: 72, right: 16, bottom: 8),
                child: Text(
                  'Last backup: ${_formatBackupTime(backupState.lastBackupTime!)}',
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: AppTheme.gray500,
                      ),
                ),
              ),

            const Divider(),

            // Backup now
            ListTile(
              leading: Container(
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: AppTheme.successGreen.withValues(alpha: 0.1),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: isBackingUp
                    ? SizedBox(
                        width: 20,
                        height: 20,
                        child: CircularProgressIndicator(
                          strokeWidth: 2,
                          color: AppTheme.successGreen,
                        ),
                      )
                    : Icon(LucideIcons.save, size: 20, color: AppTheme.successGreen),
              ),
              title: Text(isBackingUp ? 'Backing up...' : 'Backup Now'),
              subtitle: const Text('Create a backup of your database'),
              trailing: const Icon(LucideIcons.chevronRight, size: 20),
              onTap: isBackingUp
                  ? null
                  : () async {
                      final success = await ref.read(backupProvider.notifier).backupNow();
                      if (mounted) {
                        toastification.show(
                          context: context,
                          type: success
                              ? ToastificationType.success
                              : ToastificationType.error,
                          style: ToastificationStyle.fillColored,
                          title: Text(success ? 'Backup Created' : 'Backup Failed'),
                          description: Text(success
                              ? 'Your data has been backed up successfully'
                              : backupState.error ?? 'Unknown error'),
                          autoCloseDuration: const Duration(seconds: 3),
                        );
                      }
                    },
            ),

            const SizedBox(height: 16),

            // Available Backups Section
            Text(
              'Available Backups',
              style: Theme.of(context).textTheme.titleSmall?.copyWith(
                    fontWeight: FontWeight.w600,
                    color: AppTheme.gray600,
                  ),
            ),
            const SizedBox(height: 12),

            if (backupState.availableBackups.isNotEmpty)
              ...backupState.availableBackups.map((backup) => _BackupListItem(
                    backup: backup,
                    onDownload: () => _downloadBackup(context, backup),
                    onRestore: () => _confirmAndRestoreFromSettings(context, backup),
                  ))
            else
              Container(
                padding: const EdgeInsets.all(16),
                decoration: BoxDecoration(
                  color: AppTheme.gray100,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Row(
                  children: [
                    Icon(LucideIcons.info, size: 18, color: AppTheme.gray400),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Text(
                        'No backups yet. Create one using "Backup Now" above.',
                        style: Theme.of(context).textTheme.bodySmall?.copyWith(
                              color: AppTheme.gray500,
                            ),
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

  Widget _buildImportContent(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Import',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 8),
          Text(
            'Import a backup file from another device or location.',
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: AppTheme.gray500,
                ),
          ),
          const SizedBox(height: 24),

          // Import backup from file
          Container(
            padding: const EdgeInsets.all(20),
            decoration: BoxDecoration(
              color: AppTheme.primaryBlue.withValues(alpha: 0.05),
              borderRadius: BorderRadius.circular(12),
              border: Border.all(color: AppTheme.primaryBlue.withValues(alpha: 0.2)),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Container(
                      padding: const EdgeInsets.all(10),
                      decoration: BoxDecoration(
                        color: AppTheme.primaryBlue.withValues(alpha: 0.1),
                        borderRadius: BorderRadius.circular(10),
                      ),
                      child: Icon(LucideIcons.folderUp, size: 24, color: AppTheme.primaryBlue),
                    ),
                    const SizedBox(width: 16),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            'Import Backup File',
                            style: Theme.of(context).textTheme.titleMedium?.copyWith(
                                  fontWeight: FontWeight.w600,
                                ),
                          ),
                          const SizedBox(height: 4),
                          Text(
                            'Select a .db backup file to import',
                            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                                  color: AppTheme.gray500,
                                ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 16),
                SizedBox(
                  width: double.infinity,
                  child: OutlinedButton.icon(
                    onPressed: () => _importBackupFromFileDirectly(context),
                    icon: const Icon(LucideIcons.upload, size: 18),
                    label: const Text('Choose File...'),
                    style: OutlinedButton.styleFrom(
                      padding: const EdgeInsets.symmetric(vertical: 12),
                    ),
                  ),
                ),
              ],
            ),
          ),

          const SizedBox(height: 24),

          // Info box
          Container(
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(
              color: AppTheme.gray100,
              borderRadius: BorderRadius.circular(8),
            ),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Icon(LucideIcons.info, size: 16, color: AppTheme.gray500),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    'After importing, the backup will appear in the Backup section where you can restore from it.',
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AppTheme.gray600,
                        ),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildExportContent(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Export',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 8),
          Text(
            'Export your data in various formats for use in other applications.',
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: AppTheme.gray500,
                ),
          ),
          const SizedBox(height: 24),

          // Export to markdown
          Container(
            padding: const EdgeInsets.all(20),
            decoration: BoxDecoration(
              color: AppTheme.primaryBlue.withValues(alpha: 0.05),
              borderRadius: BorderRadius.circular(12),
              border: Border.all(color: AppTheme.primaryBlue.withValues(alpha: 0.2)),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Container(
                      padding: const EdgeInsets.all(10),
                      decoration: BoxDecoration(
                        color: AppTheme.primaryBlue.withValues(alpha: 0.1),
                        borderRadius: BorderRadius.circular(10),
                      ),
                      child: Icon(LucideIcons.fileText, size: 24, color: AppTheme.primaryBlue),
                    ),
                    const SizedBox(width: 16),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            'Export to Markdown',
                            style: Theme.of(context).textTheme.titleMedium?.copyWith(
                                  fontWeight: FontWeight.w600,
                                ),
                          ),
                          const SizedBox(height: 4),
                          Text(
                            'Export all tasks and projects as a markdown file',
                            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                                  color: AppTheme.gray500,
                                ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 16),
                SizedBox(
                  width: double.infinity,
                  child: FilledButton.icon(
                    onPressed: () async {
                      final exported = await ExportMarkdownDialog.show(context);
                      if (exported == true && mounted) {
                        toastification.show(
                          context: context,
                          type: ToastificationType.success,
                          style: ToastificationStyle.fillColored,
                          title: const Text('Export Complete'),
                          description: const Text('Your data has been exported to markdown'),
                          autoCloseDuration: const Duration(seconds: 3),
                        );
                      }
                    },
                    icon: const Icon(LucideIcons.download, size: 18),
                    label: const Text('Export'),
                    style: FilledButton.styleFrom(
                      padding: const EdgeInsets.symmetric(vertical: 12),
                    ),
                  ),
                ),
              ],
            ),
          ),

          const SizedBox(height: 24),

          // What's included info
          Text(
            'What\'s included:',
            style: Theme.of(context).textTheme.titleSmall?.copyWith(
                  fontWeight: FontWeight.w600,
                ),
          ),
          const SizedBox(height: 12),
          _ExportFeatureRow(
            icon: LucideIcons.checkSquare,
            label: 'All tasks',
            description: 'Grouped by project with status, priority, and due dates',
          ),
          const SizedBox(height: 8),
          _ExportFeatureRow(
            icon: LucideIcons.folder,
            label: 'Projects',
            description: 'With their associated tasks',
          ),
          const SizedBox(height: 8),
          _ExportFeatureRow(
            icon: LucideIcons.tag,
            label: 'Labels',
            description: 'Complete label list with colors',
          ),
        ],
      ),
    );
  }

  Future<void> _importBackupFromFileDirectly(BuildContext ctx) async {
    // Use file_selector which properly uses XDG Desktop Portals on Linux
    // This works inside Flatpak sandbox unlike file_picker which needs zenity
    final XFile? file = await openFile(
      acceptedTypeGroups: [
        const XTypeGroup(
          label: 'Database files',
          extensions: ['db'],
        ),
      ],
    );

    if (file == null) return;

    final filePath = file.path;

    final success = await ref.read(backupProvider.notifier).importBackup(filePath);

    if (mounted) {
      if (success) {
        toastification.show(
          context: context,
          type: ToastificationType.success,
          style: ToastificationStyle.fillColored,
          title: const Text('Backup Imported'),
          description: const Text('Backup file imported successfully. Go to Backup to restore from it.'),
          autoCloseDuration: const Duration(seconds: 4),
        );
      } else {
        toastification.show(
          context: context,
          type: ToastificationType.error,
          style: ToastificationStyle.fillColored,
          title: const Text('Import Failed'),
          description: Text(ref.read(backupProvider).error ?? 'Invalid or corrupted backup file'),
          autoCloseDuration: const Duration(seconds: 5),
        );
      }
    }
  }

  String _formatBackupTime(DateTime time) {
    final now = DateTime.now();
    final diff = now.difference(time);

    if (diff.inMinutes < 1) {
      return 'Just now';
    } else if (diff.inHours < 1) {
      return '${diff.inMinutes} minute(s) ago';
    } else if (diff.inDays < 1) {
      return '${diff.inHours} hour(s) ago';
    } else if (diff.inDays < 7) {
      return '${diff.inDays} day(s) ago';
    } else {
      return '${time.day}/${time.month}/${time.year}';
    }
  }

  Future<void> _downloadBackup(BuildContext ctx, BackupInfo backup) async {
    // Generate default filename
    final defaultFileName = 'openza_${backup.fileName}';

    // Use file_selector which properly uses XDG Desktop Portals on Linux
    // This works inside Flatpak sandbox unlike file_picker which needs zenity
    final FileSaveLocation? saveLocation = await getSaveLocation(
      suggestedName: defaultFileName,
      acceptedTypeGroups: [
        const XTypeGroup(
          label: 'Database files',
          extensions: ['db'],
        ),
      ],
    );

    if (saveLocation == null) return; // User cancelled

    final result = await ref.read(backupProvider.notifier).exportBackup(
      backup.filePath,
      saveLocation.path,
    );

    if (mounted) {
      toastification.show(
        context: context,
        type: result != null ? ToastificationType.success : ToastificationType.error,
        style: ToastificationStyle.fillColored,
        title: Text(result != null ? 'Backup Downloaded' : 'Download Failed'),
        description: Text(result != null
            ? 'Backup saved successfully'
            : 'Failed to save backup file'),
        autoCloseDuration: const Duration(seconds: 3),
      );
    }
  }

  Future<void> _confirmAndRestoreFromSettings(BuildContext ctx, BackupInfo backup) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Restore Backup'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: AppTheme.warningOrange.withValues(alpha: 0.1),
                borderRadius: BorderRadius.circular(8),
                border: Border.all(
                  color: AppTheme.warningOrange.withValues(alpha: 0.3),
                ),
              ),
              child: Row(
                children: [
                  Icon(LucideIcons.alertTriangle, size: 20, color: AppTheme.warningOrange),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Text(
                      'This will replace your current data. The app will need to restart after restore.',
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                            color: AppTheme.gray700,
                          ),
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(height: 16),
            Text(
              'Restore from backup created on ${_formatBackupDate(backup.createdAt)}?',
              style: Theme.of(context).textTheme.bodyMedium,
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            style: FilledButton.styleFrom(
              backgroundColor: AppTheme.warningOrange,
            ),
            child: const Text('Restore'),
          ),
        ],
      ),
    );

    if (confirmed == true && mounted) {
      final success = await ref
          .read(backupProvider.notifier)
          .restoreFromBackup(backup.filePath);

      if (mounted) {
        if (success) {
          showDialog(
            context: context,
            barrierDismissible: false,
            builder: (context) => AlertDialog(
              title: const Text('Restore Complete'),
              content: const Text(
                'Database restored successfully. Please restart the app to apply changes.',
              ),
              actions: [
                FilledButton(
                  onPressed: () => Navigator.of(context).pop(),
                  child: const Text('OK'),
                ),
              ],
            ),
          );
        } else {
          toastification.show(
            context: context,
            type: ToastificationType.error,
            style: ToastificationStyle.fillColored,
            title: const Text('Restore Failed'),
            description: Text(ref.read(backupProvider).error ?? 'Unknown error'),
            autoCloseDuration: const Duration(seconds: 5),
          );
        }
      }
    }
  }

  String _formatBackupDate(DateTime time) {
    final months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    final hour = time.hour > 12 ? time.hour - 12 : (time.hour == 0 ? 12 : time.hour);
    final amPm = time.hour >= 12 ? 'pm' : 'am';
    return '${months[time.month - 1]} ${time.day}, ${time.year} at ${hour.toString().padLeft(2, '0')}:${time.minute.toString().padLeft(2, '0')} $amPm';
  }

  String _getPlatformName() {
    if (Platform.isWindows) return 'Windows';
    if (Platform.isLinux) return 'Linux';
    if (Platform.isMacOS) return 'macOS';
    return 'Desktop';
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
                    'Local First. Open Source.',
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AppTheme.gray500,
                        ),
                  ),
                ],
              ),
            ],
          ),
          const SizedBox(height: 24),

          _AboutRow(label: 'Version', value: ref.watch(appVersionProvider)),
          _AboutRow(label: 'License', value: 'MIT'),
          _AboutRow(label: 'Platform', value: _getPlatformName()),

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

/// Feature row for export page
class _ExportFeatureRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String description;

  const _ExportFeatureRow({
    required this.icon,
    required this.label,
    required this.description,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Container(
          padding: const EdgeInsets.all(6),
          decoration: BoxDecoration(
            color: AppTheme.primaryBlue.withValues(alpha: 0.1),
            borderRadius: BorderRadius.circular(6),
          ),
          child: Icon(icon, size: 14, color: AppTheme.primaryBlue),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                label,
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      fontWeight: FontWeight.w500,
                    ),
              ),
              Text(
                description,
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: AppTheme.gray500,
                    ),
              ),
            ],
          ),
        ),
      ],
    );
  }
}

/// Calendar-style backup list item widget
class _BackupListItem extends StatelessWidget {
  final BackupInfo backup;
  final VoidCallback onDownload;
  final VoidCallback onRestore;

  const _BackupListItem({
    required this.backup,
    required this.onDownload,
    required this.onRestore,
  });

  String _getOrdinalSuffix(int day) {
    if (day >= 11 && day <= 13) return 'th';
    switch (day % 10) {
      case 1:
        return 'st';
      case 2:
        return 'nd';
      case 3:
        return 'rd';
      default:
        return 'th';
    }
  }

  String _formatDate(DateTime time) {
    final months = [
      'January', 'February', 'March', 'April', 'May', 'June',
      'July', 'August', 'September', 'October', 'November', 'December'
    ];
    final suffix = _getOrdinalSuffix(time.day);
    return '${months[time.month - 1]} ${time.day}$suffix, ${time.year}';
  }

  String _formatTime(DateTime time) {
    final hour = time.hour > 12 ? time.hour - 12 : (time.hour == 0 ? 12 : time.hour);
    final amPm = time.hour >= 12 ? 'pm' : 'am';
    return '${hour.toString().padLeft(2, '0')}:${time.minute.toString().padLeft(2, '0')} $amPm';
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.only(bottom: 8),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
      decoration: BoxDecoration(
        color: AppTheme.gray50,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: AppTheme.gray200),
      ),
      child: Row(
        children: [
          // Calendar day badge
          Container(
            width: 44,
            height: 44,
            decoration: BoxDecoration(
              color: Colors.white,
              borderRadius: BorderRadius.circular(8),
              border: Border.all(color: AppTheme.primaryBlue.withValues(alpha: 0.3)),
            ),
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Icon(LucideIcons.calendar, size: 12, color: AppTheme.primaryBlue),
                Text(
                  '${backup.createdAt.day}',
                  style: TextStyle(
                    fontSize: 16,
                    fontWeight: FontWeight.bold,
                    color: AppTheme.primaryBlue,
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(width: 12),
          // Date and time
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  _formatDate(backup.createdAt),
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        fontWeight: FontWeight.w500,
                      ),
                ),
                Row(
                  children: [
                    Text(
                      _formatTime(backup.createdAt),
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                            color: AppTheme.gray500,
                          ),
                    ),
                    const SizedBox(width: 8),
                    Text(
                      '·',
                      style: TextStyle(color: AppTheme.gray400),
                    ),
                    const SizedBox(width: 8),
                    Text(
                      backup.formattedSize,
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                            color: AppTheme.gray500,
                          ),
                    ),
                  ],
                ),
              ],
            ),
          ),
          // Action buttons
          OutlinedButton.icon(
            onPressed: onDownload,
            icon: const Icon(LucideIcons.download, size: 14),
            label: const Text('Download'),
            style: OutlinedButton.styleFrom(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
              textStyle: const TextStyle(fontSize: 12),
            ),
          ),
          const SizedBox(width: 8),
          IconButton(
            onPressed: onRestore,
            icon: Icon(LucideIcons.rotateCcw, size: 18, color: AppTheme.warningOrange),
            tooltip: 'Restore this backup',
            style: IconButton.styleFrom(
              backgroundColor: AppTheme.warningOrange.withValues(alpha: 0.1),
            ),
          ),
        ],
      ),
    );
  }
}
