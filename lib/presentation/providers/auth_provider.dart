import 'package:flutter_riverpod/flutter_riverpod.dart';
// ignore: deprecated_member_use
import 'package:flutter_riverpod/legacy.dart';

import '../../data/datasources/local/secure_storage.dart';
import '../../domain/entities/task.dart';

/// Authentication state
class AuthState {
  final bool isLoading;
  final TaskProvider? activeProvider;
  final bool todoistAuthenticated;
  final bool msToDoAuthenticated;
  final String? error;

  const AuthState({
    this.isLoading = false,
    this.activeProvider,
    this.todoistAuthenticated = false,
    this.msToDoAuthenticated = false,
    this.error,
  });

  bool get isAuthenticated => todoistAuthenticated || msToDoAuthenticated;

  AuthState copyWith({
    bool? isLoading,
    TaskProvider? activeProvider,
    bool? todoistAuthenticated,
    bool? msToDoAuthenticated,
    String? error,
  }) {
    return AuthState(
      isLoading: isLoading ?? this.isLoading,
      activeProvider: activeProvider ?? this.activeProvider,
      todoistAuthenticated: todoistAuthenticated ?? this.todoistAuthenticated,
      msToDoAuthenticated: msToDoAuthenticated ?? this.msToDoAuthenticated,
      error: error,
    );
  }
}

/// Authentication state notifier
class AuthNotifier extends StateNotifier<AuthState> {
  final SecureStorageService _storage;

  AuthNotifier(this._storage) : super(const AuthState(isLoading: true)) {
    _initialize();
  }

  Future<void> _initialize() async {
    try {
      final todoistToken = await _storage.getTodoistAccessToken();
      final msToDoToken = await _storage.getMsToDoAccessToken();
      final activeProviderStr = await _storage.getActiveProvider();

      TaskProvider? activeProvider;
      if (activeProviderStr == 'todoist') {
        activeProvider = TaskProvider.todoist;
      } else if (activeProviderStr == 'msToDo') {
        activeProvider = TaskProvider.msToDo;
      }

      // Auto-select active provider if not set
      if (activeProvider == null) {
        if (todoistToken != null) {
          activeProvider = TaskProvider.todoist;
        } else if (msToDoToken != null) {
          activeProvider = TaskProvider.msToDo;
        }
      }

      state = AuthState(
        isLoading: false,
        activeProvider: activeProvider,
        todoistAuthenticated: todoistToken != null,
        msToDoAuthenticated: msToDoToken != null,
      );
    } catch (e) {
      state = AuthState(
        isLoading: false,
        error: e.toString(),
      );
    }
  }

  /// Set Todoist as authenticated
  Future<void> setTodoistAuthenticated(String accessToken) async {
    await _storage.storeTodoistTokens(accessToken: accessToken);

    state = state.copyWith(
      todoistAuthenticated: true,
      activeProvider: state.activeProvider ?? TaskProvider.todoist,
    );

    if (state.activeProvider == TaskProvider.todoist) {
      await _storage.storeActiveProvider('todoist');
    }
  }

  /// Set MS To-Do as authenticated
  Future<void> setMsToDoAuthenticated(
    String accessToken, {
    String? refreshToken,
    DateTime? expiry,
  }) async {
    await _storage.storeMsToDoTokens(
      accessToken: accessToken,
      refreshToken: refreshToken,
      expiry: expiry,
    );

    state = state.copyWith(
      msToDoAuthenticated: true,
      activeProvider: state.activeProvider ?? TaskProvider.msToDo,
    );

    if (state.activeProvider == TaskProvider.msToDo) {
      await _storage.storeActiveProvider('msToDo');
    }
  }

  /// Set active provider
  Future<void> setActiveProvider(TaskProvider provider) async {
    state = state.copyWith(activeProvider: provider);

    final providerStr = provider == TaskProvider.todoist ? 'todoist' : 'msToDo';
    await _storage.storeActiveProvider(providerStr);
  }

  /// Sign out from Todoist
  Future<void> signOutTodoist() async {
    await _storage.clearTodoistTokens();

    TaskProvider? newActive = state.activeProvider;
    if (state.activeProvider == TaskProvider.todoist) {
      newActive = state.msToDoAuthenticated ? TaskProvider.msToDo : null;
    }

    state = state.copyWith(
      todoistAuthenticated: false,
      activeProvider: newActive,
    );
  }

  /// Sign out from MS To-Do
  Future<void> signOutMsToDo() async {
    await _storage.clearMsToDoTokens();

    TaskProvider? newActive = state.activeProvider;
    if (state.activeProvider == TaskProvider.msToDo) {
      newActive = state.todoistAuthenticated ? TaskProvider.todoist : null;
    }

    state = state.copyWith(
      msToDoAuthenticated: false,
      activeProvider: newActive,
    );
  }

  /// Sign out from all providers
  Future<void> signOutAll() async {
    await _storage.clearTodoistTokens();
    await _storage.clearMsToDoTokens();

    state = const AuthState(
      isLoading: false,
      todoistAuthenticated: false,
      msToDoAuthenticated: false,
    );
  }
}

/// Provider for secure storage
final secureStorageProvider = Provider<SecureStorageService>((ref) {
  return SecureStorageService.instance;
});

/// Provider for auth state
final authProvider = StateNotifierProvider<AuthNotifier, AuthState>((ref) {
  return AuthNotifier(ref.watch(secureStorageProvider));
});

/// Provider for Todoist access token
final todoistTokenProvider = FutureProvider<String?>((ref) async {
  return SecureStorageService.instance.getTodoistAccessToken();
});

/// Provider for MS To-Do access token
final msToDoTokenProvider = FutureProvider<String?>((ref) async {
  return SecureStorageService.instance.getMsToDoAccessToken();
});
