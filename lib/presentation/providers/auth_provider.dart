import 'package:flutter_riverpod/flutter_riverpod.dart';
// ignore: deprecated_member_use
import 'package:flutter_riverpod/legacy.dart';

import '../../core/constants/app_constants.dart';
import '../../data/datasources/local/secure_storage.dart';
import '../../data/datasources/remote/auth/oauth_service.dart';
import '../../data/datasources/remote/auth/token_manager.dart';

/// Authentication state
class AuthState {
  final bool isLoading;
  final String? activeIntegrationId;
  final bool todoistAuthenticated;
  final bool msToDoAuthenticated;
  final String? error;

  const AuthState({
    this.isLoading = false,
    this.activeIntegrationId,
    this.todoistAuthenticated = false,
    this.msToDoAuthenticated = false,
    this.error,
  });

  bool get isAuthenticated => todoistAuthenticated || msToDoAuthenticated;

  AuthState copyWith({
    bool? isLoading,
    String? activeIntegrationId,
    bool clearActiveIntegrationId = false,
    bool? todoistAuthenticated,
    bool? msToDoAuthenticated,
    String? error,
  }) {
    return AuthState(
      isLoading: isLoading ?? this.isLoading,
      activeIntegrationId: clearActiveIntegrationId ? null : (activeIntegrationId ?? this.activeIntegrationId),
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
      final activeIntegrationId = await _storage.getActiveProvider();

      // Check if MS To-Do is properly configured (client ID must be set for token refresh)
      final msToDoConfigured = AppConstants.msToDoClientId.isNotEmpty;
      final msToDoValid = msToDoToken != null && msToDoConfigured;

      // If MS To-Do token exists but client ID is not configured, clear the invalid tokens
      if (msToDoToken != null && !msToDoConfigured) {
        await _storage.clearMsToDoTokens();
      }

      String? effectiveIntegrationId = activeIntegrationId;

      // Validate active integration
      if (effectiveIntegrationId == 'todoist' && todoistToken == null) {
        effectiveIntegrationId = null;
      } else if (effectiveIntegrationId == 'msToDo' && !msToDoValid) {
        effectiveIntegrationId = null;
      }

      // Auto-select active integration if not set
      if (effectiveIntegrationId == null) {
        if (todoistToken != null) {
          effectiveIntegrationId = 'todoist';
        } else if (msToDoValid) {
          effectiveIntegrationId = 'msToDo';
        }
      }

      state = AuthState(
        isLoading: false,
        activeIntegrationId: effectiveIntegrationId,
        todoistAuthenticated: todoistToken != null,
        msToDoAuthenticated: msToDoValid,
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
      activeIntegrationId: state.activeIntegrationId ?? 'todoist',
    );

    if (state.activeIntegrationId == 'todoist') {
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
      activeIntegrationId: state.activeIntegrationId ?? 'msToDo',
    );

    if (state.activeIntegrationId == 'msToDo') {
      await _storage.storeActiveProvider('msToDo');
    }
  }

  /// Set active integration by ID
  Future<void> setActiveIntegration(String integrationId) async {
    state = state.copyWith(activeIntegrationId: integrationId);
    await _storage.storeActiveProvider(integrationId);
  }

  /// Sign out from Todoist
  Future<void> signOutTodoist() async {
    await _storage.clearTodoistTokens();

    String? newActiveId = state.activeIntegrationId;
    if (state.activeIntegrationId == 'todoist') {
      newActiveId = state.msToDoAuthenticated ? 'msToDo' : null;
    }

    state = state.copyWith(
      todoistAuthenticated: false,
      activeIntegrationId: newActiveId,
      clearActiveIntegrationId: newActiveId == null,
    );
  }

  /// Sign out from MS To-Do
  Future<void> signOutMsToDo() async {
    await _storage.clearMsToDoTokens();

    String? newActiveId = state.activeIntegrationId;
    if (state.activeIntegrationId == 'msToDo') {
      newActiveId = state.todoistAuthenticated ? 'todoist' : null;
    }

    state = state.copyWith(
      msToDoAuthenticated: false,
      activeIntegrationId: newActiveId,
      clearActiveIntegrationId: newActiveId == null,
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

/// Provider for OAuth service
final oauthServiceProvider = Provider<OAuthService>((ref) {
  return OAuthService();
});

/// Provider for Token Manager
final tokenManagerProvider = Provider<TokenManager>((ref) {
  return TokenManager.instance;
});

/// Provider for initiating Todoist OAuth
final todoistOAuthProvider = FutureProvider.family<String?, void>((ref, _) async {
  final oauth = ref.read(oauthServiceProvider);

  final clientId = AppConstants.todoistClientId;
  final clientSecret = AppConstants.todoistClientSecret;

  if (clientId.isEmpty || clientSecret.isEmpty) {
    throw Exception('Todoist OAuth credentials not configured. '
        'Set TODOIST_CLIENT_ID and TODOIST_CLIENT_SECRET environment variables.');
  }

  return oauth.authenticateTodoist(
    clientId: clientId,
    clientSecret: clientSecret,
  );
});

/// Provider for initiating MS To-Do OAuth
final msToDoOAuthProvider = FutureProvider.family<String?, void>((ref, _) async {
  final oauth = ref.read(oauthServiceProvider);

  final clientId = AppConstants.msToDoClientId;
  final tenantId = AppConstants.msToDoTenantId;

  if (clientId.isEmpty) {
    throw Exception('MS To-Do OAuth credentials not configured. '
        'Set MSTODO_CLIENT_ID environment variable.');
  }

  return oauth.authenticateMsToDo(
    clientId: clientId,
    tenantId: tenantId,
  );
});
