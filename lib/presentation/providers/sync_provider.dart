import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
// ignore: deprecated_member_use
import 'package:flutter_riverpod/legacy.dart';

import '../../core/utils/logger.dart';
import '../../data/datasources/local/secure_storage.dart';
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
    // Initialize sync engine (even without Todoist)
    _ref.listen(todoistApiProvider, (previous, next) {
      next.whenData((api) {
        if (_syncEngine == null) {
          _initSyncEngine(api: api);
          return;
        }
        final hadApi = _syncEngine!.hasTodoistApi;
        _syncEngine!.setTodoistApi(api);
        if (!hadApi && api != null) {
          syncNow();
        }
      });
    });

    // Also check if already available (null is valid for Obsidian-only)
    final apiAsync = _ref.read(todoistApiProvider);
    apiAsync.whenData((api) {
      _initSyncEngine(api: api);
    });
  }

  void _initSyncEngine({TodoistApi? api}) {
    if (_syncEngine != null) {
      _syncEngine!.setTodoistApi(api);
      return;
    }

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
      // Sync Todoist
      final todoistError = await _syncTodoist();

      // Extract from Obsidian (if configured)
      await _extractFromObsidian();

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

      if (todoistError != null) {
        state = state.copyWith(
          status: SyncStatus.error,
          error: todoistError,
          pendingCompletions: state.pendingCompletions,
        );
        return;
      }

      state = state.copyWith(
        status: SyncStatus.success,
        lastSyncTime: DateTime.now(),
        pendingCompletions: 0,
      );

      AppLogger.info('Sync completed successfully');
    } catch (e, stack) {
      AppLogger.error('Sync failed', e, stack);
      state = state.copyWith(
        status: SyncStatus.error,
        error: e.toString(),
      );
    }
  }

  /// Sync with Todoist. Returns an error message on failure.
  Future<String?> _syncTodoist() async {
    if (_syncEngine == null || !_syncEngine!.hasTodoistApi) return null;

    try {
      // First sync pending completions to provider
      final syncedCompletions = await _syncEngine!.syncPendingCompletions('todoist');
      if (syncedCompletions > 0) {
        AppLogger.info('Synced $syncedCompletions completions to Todoist');
      }

      // Fetch fresh data from Todoist
      final result = await _syncEngine!.fetchFromTodoist();

      if (!result.success) {
        final message = result.error ?? 'Todoist sync failed';
        AppLogger.warning('Todoist sync failed: $message');
        return message;
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
      // Incremental sync (wrapper pattern: keep local tasks even if remote omits them)
      summary = await _syncEngine!.incrementalSync(
        provider: 'todoist',
        tasks: result.tasks,
        projects: result.projects,
        labels: result.labels,
        syncToken: syncToken,
        deleteOrphans: false,
      );
      }

      if (!summary.success) {
        final message = summary.error ?? 'Todoist sync failed';
        AppLogger.warning('Todoist sync failed: $message');
        return message;
      }
    } catch (e, stack) {
      AppLogger.error('Todoist sync failed', e, stack);
      return e.toString();
    }

    return null;
  }

  /// Extract tasks from Obsidian vault
  Future<void> _extractFromObsidian() async {
    if (_syncEngine == null) return;

    // Get Obsidian vault path from secure storage
    final vaultPath = await SecureStorageService.instance.getObsidianVaultPath();
    if (vaultPath == null || vaultPath.isEmpty) {
      // Obsidian not configured, skip
      return;
    }

    // Check if vault is accessible
    if (!await _syncEngine!.isObsidianVaultAccessible(vaultPath)) {
      AppLogger.warning('Obsidian vault not accessible: $vaultPath');
      return;
    }

    // Extract tasks from vault
    final result = await _syncEngine!.extractFromObsidian(vaultPath);

    if (!result.success) {
      AppLogger.warning('Obsidian extraction failed: ${result.error}');
      return;
    }

    if (result.tasks.isEmpty) {
      AppLogger.info('No tasks found in Obsidian vault');
      return;
    }

    // Uses Rust FFI for insert/update ONLY (no deletions)
    // Key: deleteOrphans=false - app owns existence after extraction
    final summary = await _syncEngine!.incrementalSync(
      provider: 'obsidian',
      tasks: result.tasks,
      projects: [],
      labels: [],
      deleteOrphans: false, // CRITICAL: App owns existence
    );

    if (summary.success) {
      AppLogger.info('Obsidian extraction: +${summary.tasksAdded} added, ~${summary.tasksUpdated} updated');
    } else {
      AppLogger.warning('Obsidian extraction failed: ${summary.error}');
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
