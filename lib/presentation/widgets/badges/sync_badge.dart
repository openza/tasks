import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../providers/sync_provider.dart';

/// Badge showing sync status indicator
class SyncBadge extends ConsumerWidget {
  final bool showLabel;
  final VoidCallback? onTap;

  const SyncBadge({
    super.key,
    this.showLabel = false,
    this.onTap,
  });

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final syncState = ref.watch(syncProvider);

    return Tooltip(
      message: _getTooltipMessage(syncState),
      child: InkWell(
        onTap: onTap ?? () => ref.read(syncProvider.notifier).syncNow(),
        borderRadius: BorderRadius.circular(8),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
          decoration: BoxDecoration(
            color: _getBackgroundColor(syncState).withValues(alpha: 0.1),
            borderRadius: BorderRadius.circular(8),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              _buildIcon(syncState),
              if (showLabel) ...[
                const SizedBox(width: 6),
                Text(
                  _getLabel(syncState),
                  style: TextStyle(
                    fontSize: 12,
                    fontWeight: FontWeight.w500,
                    color: _getIconColor(syncState),
                  ),
                ),
              ],
              if (syncState.hasPendingChanges && !syncState.isSyncing) ...[
                const SizedBox(width: 4),
                Container(
                  width: 6,
                  height: 6,
                  decoration: BoxDecoration(
                    color: AppTheme.warningOrange,
                    shape: BoxShape.circle,
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildIcon(SyncState state) {
    if (state.isSyncing) {
      return SizedBox(
        width: 16,
        height: 16,
        child: CircularProgressIndicator(
          strokeWidth: 2,
          valueColor: AlwaysStoppedAnimation<Color>(_getIconColor(state)),
        ),
      );
    }

    return Icon(
      _getIcon(state),
      size: 16,
      color: _getIconColor(state),
    );
  }

  IconData _getIcon(SyncState state) {
    switch (state.status) {
      case SyncStatus.idle:
        return LucideIcons.refreshCw;
      case SyncStatus.syncing:
        return LucideIcons.loader2;
      case SyncStatus.success:
        return LucideIcons.checkCircle;
      case SyncStatus.error:
        return LucideIcons.alertCircle;
    }
  }

  Color _getIconColor(SyncState state) {
    switch (state.status) {
      case SyncStatus.idle:
        return AppTheme.gray500;
      case SyncStatus.syncing:
        return AppTheme.primaryBlue;
      case SyncStatus.success:
        return AppTheme.successGreen;
      case SyncStatus.error:
        return AppTheme.errorRed;
    }
  }

  Color _getBackgroundColor(SyncState state) {
    switch (state.status) {
      case SyncStatus.idle:
        return AppTheme.gray200;
      case SyncStatus.syncing:
        return AppTheme.primaryBlue;
      case SyncStatus.success:
        return AppTheme.successGreen;
      case SyncStatus.error:
        return AppTheme.errorRed;
    }
  }

  String _getLabel(SyncState state) {
    switch (state.status) {
      case SyncStatus.idle:
        return 'Sync';
      case SyncStatus.syncing:
        return 'Syncing...';
      case SyncStatus.success:
        return 'Synced';
      case SyncStatus.error:
        return 'Error';
    }
  }

  String _getTooltipMessage(SyncState state) {
    final buffer = StringBuffer();

    switch (state.status) {
      case SyncStatus.idle:
        buffer.write('Click to sync');
        break;
      case SyncStatus.syncing:
        buffer.write('Syncing with Todoist...');
        break;
      case SyncStatus.success:
        buffer.write('Synced successfully');
        break;
      case SyncStatus.error:
        buffer.write('Sync failed');
        if (state.error != null) {
          buffer.write(': ${state.error}');
        }
        break;
    }

    if (state.lastSyncTime != null) {
      buffer.write('\nLast sync: ${_formatTime(state.lastSyncTime!)}');
    }

    if (state.hasPendingChanges) {
      buffer.write('\n${state.pendingCompletions} pending changes');
    }

    if (!state.ffiAvailable) {
      buffer.write('\nRust FFI not available');
    }

    return buffer.toString();
  }

  String _formatTime(DateTime time) {
    final now = DateTime.now();
    final diff = now.difference(time);

    if (diff.inMinutes < 1) {
      return 'Just now';
    } else if (diff.inMinutes < 60) {
      return '${diff.inMinutes}m ago';
    } else if (diff.inHours < 24) {
      return '${diff.inHours}h ago';
    } else {
      return '${diff.inDays}d ago';
    }
  }
}

/// Compact sync indicator (icon only)
class SyncIndicator extends ConsumerWidget {
  final double size;

  const SyncIndicator({
    super.key,
    this.size = 20,
  });

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final syncState = ref.watch(syncProvider);

    if (syncState.isSyncing) {
      return SizedBox(
        width: size,
        height: size,
        child: CircularProgressIndicator(
          strokeWidth: 2,
          valueColor: AlwaysStoppedAnimation<Color>(AppTheme.primaryBlue),
        ),
      );
    }

    if (syncState.hasError) {
      return Icon(
        LucideIcons.alertCircle,
        size: size,
        color: AppTheme.errorRed,
      );
    }

    if (syncState.hasPendingChanges) {
      return Stack(
        children: [
          Icon(
            LucideIcons.refreshCw,
            size: size,
            color: AppTheme.gray400,
          ),
          Positioned(
            right: 0,
            top: 0,
            child: Container(
              width: 8,
              height: 8,
              decoration: BoxDecoration(
                color: AppTheme.warningOrange,
                shape: BoxShape.circle,
                border: Border.all(
                  color: Colors.white,
                  width: 1,
                ),
              ),
            ),
          ),
        ],
      );
    }

    return const SizedBox.shrink();
  }
}
