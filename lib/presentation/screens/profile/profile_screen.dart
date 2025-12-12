import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';

class ProfileScreen extends ConsumerWidget {
  const ProfileScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Container(
      color: AppTheme.gray50,
      child: Center(
        child: Card(
          margin: const EdgeInsets.all(24),
          child: Container(
            constraints: const BoxConstraints(maxWidth: 500),
            padding: const EdgeInsets.all(32),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  padding: const EdgeInsets.all(20),
                  decoration: BoxDecoration(
                    color: AppTheme.primaryBlue.withValues(alpha: 0.1),
                    shape: BoxShape.circle,
                  ),
                  child: const Icon(
                    LucideIcons.user,
                    size: 48,
                    color: AppTheme.primaryBlue,
                  ),
                ),
                const SizedBox(height: 24),
                Text(
                  'Profile',
                  style: Theme.of(context).textTheme.headlineSmall,
                ),
                const SizedBox(height: 8),
                Text(
                  'Coming Soon',
                  style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                        color: AppTheme.gray500,
                      ),
                ),
                const SizedBox(height: 24),
                Text(
                  'User profile and account settings will be available here in a future update.',
                  textAlign: TextAlign.center,
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: AppTheme.gray500,
                      ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
