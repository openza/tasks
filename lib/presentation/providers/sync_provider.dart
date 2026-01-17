import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
// ignore: deprecated_member_use
import 'package:flutter_riverpod/legacy.dart';

import '../../core/utils/logger.dart';
import '../../data/datasources/remote/todoist_api.dart';
import '../../data/sync/sync_engine.dart';
import 'task_provider.dart';

/// Sync status enum
enum SyncStatus {
  idle,
  syncing,
  success,
  error,
}

/// Sync state
class SyncState {
  final SyncStatus status;
  final DateTime? lastSyncTime;
  final String? error;
  final int pendingCompletions;
  final bool ffiAvailable;

  const SyncState({
    this.status = SyncStatus.idle,
    this.lastSyncTime,
    this.error,
    this.pendingCompletions = 0,
    this.ffiAvailable = false,
  });

  bool get isSyncing => status == SyncStatus.syncing;
  bool get hasError => status == SyncStatus.error;
  bool get hasPendingChanges => pendingCompletions > 0;

  SyncState copyWith({
    SyncStatus? status,
    DateTime? lastSyncTime,
    String? error,
    int? pendingCompletions,
    bool? ffiAvailable,
  }) {
    return SyncState(
      status: status ?? this.status,
      lastSyncTime: lastSyncTime ?? this.lastSyncTime,
      error: error,
      pendingCompletions: pendingCompletions ?? this.pendingCompletions,
      ffiAvailable: ffiAvailable ?? this.ffiAvailable,
    );
  }
}

/// Sync state notifier
class SyncNotifier extends StateNotifier<SyncState> {
  final Ref _ref;
  SyncEngine? _syncEngine;
  Timer? _syncTimer;

  static const _syncInterval = Duration(minutes: 5);

  SyncNotifier(this._ref) : super(const SyncState()) {
    _initialize();
  }

  Future<void> _initialize() async {
    // Initialize sync engine when Todoist API is available
    _ref.listen(todoistApiProvider, (previous, next) {
      next.whenData((api) {
        if (api != null) {
          _initSyncEngine(api);
        }
      });
    });

    // Also check if already available
    final apiAsync = _ref.read(todoistApiProvider);
    apiAsync.whenData((api) {
      if (api != null) {
        _initSyncEngine(api);
      }
    });
  }

  void _initSyncEngine(TodoistApi api) {
    if (_syncEngine != null) return; // Already initialized

    _syncEngine = SyncEngine(todoistApi: api);
    state = state.copyWith(ffiAvailable: _syncEngine?.isFfiAvailable ?? false);

    // Start periodic sync
    _startPeriodicSync();

    AppLogger.info('Sync engine initialized (FFI: ${_syncEngine?.isFfiAvailable})');

    // Trigger initial sync
    syncNow();
  }

  void _startPeriodicSync() {
    _syncTimer?.cancel();
    _syncTimer = Timer.periodic(_syncInterval, (_) {
      syncNow();
    });
  }

  @override
  void dispose() {
    _syncTimer?.cancel();
    super.dispose();
  }

  /// Perform sync now
  Future<void> syncNow() async {
    if (_syncEngine == null) {
      AppLogger.warning('Sync engine not initialized');
      return;
    }

    if (state.isSyncing) {
      AppLogger.info('Sync already in progress');
      return;
    }

    state = state.copyWith(status: SyncStatus.syncing, error: null);

    try {
      // First sync pending completions to provider
      final syncedCompletions = await _syncEngine!.syncPendingCompletions('todoist');
      if (syncedCompletions > 0) {
        AppLogger.info('Synced $syncedCompletions completions to Todoist');
      }

      // Fetch fresh data from Todoist
      final result = await _syncEngine!.fetchFromTodoist();

      if (!result.success) {
        state = state.copyWith(
          status: SyncStatus.error,
          error: result.error,
        );
        return;
      }

      // Get current sync token
      final syncToken = await _syncEngine!.getSyncToken('todoist');

      // Perform sync via Rust FFI
      SyncSummary summary;
      if (syncToken == null) {
        // Initial sync
        summary = await _syncEngine!.initialSync(
          provider: 'todoist',
          tasks: result.tasks,
          projects: result.projects,
          labels: result.labels,
        );
      } else {
        // Incremental sync
        summary = await _syncEngine!.incrementalSync(
          provider: 'todoist',
          tasks: result.tasks,
          projects: result.projects,
          labels: result.labels,
          syncToken: syncToken,
        );
      }

      if (summary.success) {
        // Refresh local providers in background - keeps old data visible until new data ready
        _ref.refresh(localTasksProvider);
        _ref.refresh(localProjectsProvider);
        _ref.refresh(localLabelsProvider);

        // Wait for base providers to complete
        await Future.wait([
          _ref.read(localTasksProvider.future),
          _ref.read(localProjectsProvider.future),
          _ref.read(localLabelsProvider.future),
        ]);

        // Refresh unified provider with fresh base data
        _ref.refresh(unifiedDataProvider);
        await _ref.read(unifiedDataProvider.future);

        state = state.copyWith(
          status: SyncStatus.success,
          lastSyncTime: DateTime.now(),
          pendingCompletions: 0,
        );

        AppLogger.info('Sync completed successfully');
      } else {
        state = state.copyWith(
          status: SyncStatus.error,
          error: summary.error,
        );
      }
    } catch (e, stack) {
      AppLogger.error('Sync failed', e, stack);
      state = state.copyWith(
        status: SyncStatus.error,
        error: e.toString(),
      );
    }
  }

  /// Queue a task completion for sync
  Future<void> queueTaskCompletion({
    required String taskId,
    required String providerTaskId,
    required bool completed,
  }) async {
    if (_syncEngine == null) return;

    final success = await _syncEngine!.queueCompletion(
      taskId: taskId,
      provider: 'todoist',
      providerTaskId: providerTaskId,
      completed: completed,
    );

    if (success) {
      state = state.copyWith(
        pendingCompletions: state.pendingCompletions + 1,
      );
    }
  }

  /// Force re-sync (clear and sync fresh)
  Future<void> forceResync() async {
    if (_syncEngine == null) return;

    state = state.copyWith(status: SyncStatus.syncing, error: null);

    try {
      // Clear existing provider data
      await _syncEngine!.clearProviderData('todoist');

      // Fetch fresh data
      final result = await _syncEngine!.fetchFromTodoist();

      if (!result.success) {
        state = state.copyWith(
          status: SyncStatus.error,
          error: result.error,
        );
        return;
      }

      // Perform initial sync
      final summary = await _syncEngine!.initialSync(
        provider: 'todoist',
        tasks: result.tasks,
        projects: result.projects,
        labels: result.labels,
      );

      if (summary.success) {
        // Refresh local providers in background - keeps old data visible until new data ready
        _ref.refresh(localTasksProvider);
        _ref.refresh(localProjectsProvider);
        _ref.refresh(localLabelsProvider);

        // Wait for base providers to complete
        await Future.wait([
          _ref.read(localTasksProvider.future),
          _ref.read(localProjectsProvider.future),
          _ref.read(localLabelsProvider.future),
        ]);

        // Refresh unified provider with fresh base data
        _ref.refresh(unifiedDataProvider);
        await _ref.read(unifiedDataProvider.future);

        state = state.copyWith(
          status: SyncStatus.success,
          lastSyncTime: DateTime.now(),
          pendingCompletions: 0,
        );
      } else {
        state = state.copyWith(
          status: SyncStatus.error,
          error: summary.error,
        );
      }
    } catch (e, stack) {
      AppLogger.error('Force resync failed', e, stack);
      state = state.copyWith(
        status: SyncStatus.error,
        error: e.toString(),
      );
    }
  }
}

/// Provider for sync state
final syncProvider = StateNotifierProvider<SyncNotifier, SyncState>((ref) {
  return SyncNotifier(ref);
});

/// Provider for checking if sync is in progress
final isSyncingProvider = Provider<bool>((ref) {
  return ref.watch(syncProvider).isSyncing;
});

/// Provider for last sync time
final lastSyncTimeProvider = Provider<DateTime?>((ref) {
  return ref.watch(syncProvider).lastSyncTime;
});
