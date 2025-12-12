import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:lucide_icons/lucide_icons.dart';
import 'package:toastification/toastification.dart';

import '../../../app/app_router.dart';
import '../../../app/app_theme.dart';
import '../../../core/constants/app_constants.dart';
import '../../providers/auth_provider.dart';

class LoginScreen extends ConsumerStatefulWidget {
  const LoginScreen({super.key});

  @override
  ConsumerState<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends ConsumerState<LoginScreen> {
  bool _isLoading = false;
  String? _loadingProvider;

  Future<void> _authenticateTodoist() async {
    final clientId = AppConstants.todoistClientId;
    final clientSecret = AppConstants.todoistClientSecret;

    if (clientId.isEmpty || clientSecret.isEmpty) {
      _showError('Todoist OAuth credentials not configured.\n'
          'Run with: flutter run --dart-define=TODOIST_CLIENT_ID=xxx --dart-define=TODOIST_CLIENT_SECRET=xxx');
      return;
    }

    setState(() {
      _isLoading = true;
      _loadingProvider = 'todoist';
    });

    try {
      final oauth = ref.read(oauthServiceProvider);
      final token = await oauth.authenticateTodoist(
        clientId: clientId,
        clientSecret: clientSecret,
      );

      if (token != null) {
        await ref.read(authProvider.notifier).setTodoistAuthenticated(token);
        _showSuccess('Connected to Todoist successfully!');
        if (mounted) {
          context.go(AppRoutes.dashboard);
        }
      } else {
        _showError('Failed to authenticate with Todoist. Please try again.');
      }
    } catch (e) {
      _showError('Error: ${e.toString()}');
    } finally {
      if (mounted) {
        setState(() {
          _isLoading = false;
          _loadingProvider = null;
        });
      }
    }
  }

  Future<void> _authenticateMsToDo() async {
    final clientId = AppConstants.msToDoClientId;
    final tenantId = AppConstants.msToDoTenantId;

    if (clientId.isEmpty) {
      _showError('MS To-Do OAuth credentials not configured.\n'
          'Run with: flutter run --dart-define=MSTODO_CLIENT_ID=xxx');
      return;
    }

    setState(() {
      _isLoading = true;
      _loadingProvider = 'mstodo';
    });

    try {
      final oauth = ref.read(oauthServiceProvider);
      final token = await oauth.authenticateMsToDo(
        clientId: clientId,
        tenantId: tenantId,
      );

      if (token != null) {
        await ref.read(authProvider.notifier).setMsToDoAuthenticated(token);
        _showSuccess('Connected to Microsoft To-Do successfully!');
        if (mounted) {
          context.go(AppRoutes.dashboard);
        }
      } else {
        _showError('Failed to authenticate with Microsoft To-Do. Please try again.');
      }
    } catch (e) {
      _showError('Error: ${e.toString()}');
    } finally {
      if (mounted) {
        setState(() {
          _isLoading = false;
          _loadingProvider = null;
        });
      }
    }
  }

  void _showError(String message) {
    toastification.show(
      context: context,
      type: ToastificationType.error,
      style: ToastificationStyle.fillColored,
      title: const Text('Authentication Error'),
      description: Text(message),
      autoCloseDuration: const Duration(seconds: 5),
    );
  }

  void _showSuccess(String message) {
    toastification.show(
      context: context,
      type: ToastificationType.success,
      style: ToastificationStyle.fillColored,
      title: const Text('Success'),
      description: Text(message),
      autoCloseDuration: const Duration(seconds: 3),
    );
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: Container(
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
        child: Center(
          child: Card(
            margin: const EdgeInsets.all(24),
            child: Container(
              constraints: const BoxConstraints(maxWidth: 400),
              padding: const EdgeInsets.all(32),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  // Logo
                  Container(
                    width: 64,
                    height: 64,
                    decoration: BoxDecoration(
                      gradient: const LinearGradient(
                        colors: [AppTheme.primaryBlue, AppTheme.accentPink],
                      ),
                      borderRadius: BorderRadius.circular(16),
                    ),
                    child: const Icon(
                      LucideIcons.checkSquare,
                      color: Colors.white,
                      size: 32,
                    ),
                  ),
                  const SizedBox(height: 24),

                  // Title
                  Text(
                    'Welcome to Openza',
                    style: Theme.of(context).textTheme.headlineSmall,
                  ),
                  const SizedBox(height: 8),
                  Text(
                    'Connect your task manager to get started',
                    textAlign: TextAlign.center,
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AppTheme.gray500,
                        ),
                  ),
                  const SizedBox(height: 32),

                  // Todoist Button
                  _ProviderButton(
                    icon: LucideIcons.checkCircle,
                    iconColor: const Color(0xFFE44332), // Todoist red
                    label: 'Continue with Todoist',
                    isLoading: _isLoading && _loadingProvider == 'todoist',
                    enabled: !_isLoading,
                    onPressed: _authenticateTodoist,
                  ),
                  const SizedBox(height: 12),

                  // Microsoft Button
                  _ProviderButton(
                    icon: LucideIcons.layoutGrid,
                    iconColor: const Color(0xFF00A4EF), // Microsoft blue
                    label: 'Continue with Microsoft To-Do',
                    isLoading: _isLoading && _loadingProvider == 'mstodo',
                    enabled: !_isLoading,
                    onPressed: _authenticateMsToDo,
                  ),

                  const SizedBox(height: 24),
                  const Divider(),
                  const SizedBox(height: 24),

                  // Skip for now (local only)
                  TextButton(
                    onPressed: _isLoading
                        ? null
                        : () {
                            // Use local database only
                            context.go(AppRoutes.dashboard);
                          },
                    child: const Text('Skip for now (use local tasks only)'),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _ProviderButton extends StatelessWidget {
  final IconData icon;
  final Color iconColor;
  final String label;
  final VoidCallback onPressed;
  final bool isLoading;
  final bool enabled;

  const _ProviderButton({
    required this.icon,
    required this.iconColor,
    required this.label,
    required this.onPressed,
    this.isLoading = false,
    this.enabled = true,
  });

  @override
  Widget build(BuildContext context) {
    return SizedBox(
      width: double.infinity,
      child: OutlinedButton(
        onPressed: enabled ? onPressed : null,
        style: OutlinedButton.styleFrom(
          padding: const EdgeInsets.symmetric(vertical: 16),
        ),
        child: Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            if (isLoading)
              const SizedBox(
                width: 20,
                height: 20,
                child: CircularProgressIndicator(strokeWidth: 2),
              )
            else
              Icon(icon, color: enabled ? iconColor : Colors.grey, size: 20),
            const SizedBox(width: 12),
            Text(isLoading ? 'Connecting...' : label),
          ],
        ),
      ),
    );
  }
}
