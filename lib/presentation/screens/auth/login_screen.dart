import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_router.dart';
import '../../../app/app_theme.dart';

class LoginScreen extends ConsumerWidget {
  const LoginScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
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
                    onPressed: () {
                      // TODO: Implement Todoist OAuth
                      context.go(AppRoutes.dashboard);
                    },
                  ),
                  const SizedBox(height: 12),

                  // Microsoft Button
                  _ProviderButton(
                    icon: LucideIcons.layoutGrid,
                    iconColor: const Color(0xFF00A4EF), // Microsoft blue
                    label: 'Continue with Microsoft To-Do',
                    onPressed: () {
                      // TODO: Implement MS To-Do OAuth
                      context.go(AppRoutes.dashboard);
                    },
                  ),

                  const SizedBox(height: 24),
                  const Divider(),
                  const SizedBox(height: 24),

                  // Skip for now (local only)
                  TextButton(
                    onPressed: () {
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

  const _ProviderButton({
    required this.icon,
    required this.iconColor,
    required this.label,
    required this.onPressed,
  });

  @override
  Widget build(BuildContext context) {
    return SizedBox(
      width: double.infinity,
      child: OutlinedButton(
        onPressed: onPressed,
        style: OutlinedButton.styleFrom(
          padding: const EdgeInsets.symmetric(vertical: 16),
        ),
        child: Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(icon, color: iconColor, size: 20),
            const SizedBox(width: 12),
            Text(label),
          ],
        ),
      ),
    );
  }
}
