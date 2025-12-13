import 'dart:async';
import 'dart:math';

import '../../../../core/constants/app_constants.dart';
import '../../../../core/utils/logger.dart';
import '../../local/secure_storage.dart';
import 'oauth_service.dart';

/// Manages token lifecycle including automatic refresh
class TokenManager {
  static TokenManager? _instance;
  static TokenManager get instance => _instance ??= TokenManager._();

  TokenManager._();

  final SecureStorageService _storage = SecureStorageService.instance;
  final OAuthService _oauth = OAuthService();

  // Refresh state tracking
  bool _isRefreshing = false;
  DateTime? _lastRefreshAttempt;
  int _refreshAttempts = 0;
  Completer<String?>? _refreshCompleter;

  /// Get a valid MS To-Do access token, refreshing if necessary
  Future<String?> getValidMsToDoToken() async {
    final accessToken = await _storage.getMsToDoAccessToken();
    if (accessToken == null) return null;

    final expiry = await _storage.getMsToDoTokenExpiry();

    // Check if token needs refresh
    if (expiry != null) {
      final timeUntilExpiry = expiry.difference(DateTime.now());
      final threshold = Duration(minutes: AppConstants.tokenRefreshThresholdMinutes);

      if (timeUntilExpiry <= threshold) {
        AppLogger.debug(
          'MS To-Do token expiring in ${timeUntilExpiry.inMinutes} minutes, refreshing...',
        );
        return _refreshMsToDoToken();
      }
    }

    return accessToken;
  }

  /// Get a valid Todoist access token
  /// Todoist tokens don't expire, so this just returns the stored token
  Future<String?> getValidTodoistToken() async {
    return _storage.getTodoistAccessToken();
  }

  /// Refresh MS To-Do token with deduplication and backoff
  Future<String?> _refreshMsToDoToken() async {
    // If already refreshing, wait for that to complete
    if (_isRefreshing && _refreshCompleter != null) {
      AppLogger.debug('Token refresh already in progress, waiting...');
      return _refreshCompleter!.future;
    }

    // Check cooldown - return null to prevent retry loops with stale tokens
    if (_lastRefreshAttempt != null) {
      final timeSinceLastAttempt = DateTime.now().difference(_lastRefreshAttempt!);
      final cooldown = Duration(seconds: AppConstants.tokenRefreshCooldownSeconds);
      if (timeSinceLastAttempt < cooldown) {
        AppLogger.debug(
          'Token refresh on cooldown, ${cooldown.inSeconds - timeSinceLastAttempt.inSeconds}s remaining',
        );
        return null; // Return null to signal no fresh token available
      }
    }

    // Check max attempts
    if (_refreshAttempts >= AppConstants.maxTokenRefreshAttempts) {
      AppLogger.warning(
        'Max token refresh attempts reached ($AppConstants.maxTokenRefreshAttempts)',
      );
      return null;
    }

    _isRefreshing = true;
    _refreshCompleter = Completer<String?>();
    _lastRefreshAttempt = DateTime.now();
    _refreshAttempts++;

    try {
      final newToken = await _refreshWithBackoff();
      _refreshAttempts = 0; // Reset on success
      _refreshCompleter!.complete(newToken);
      return newToken;
    } catch (e, stack) {
      AppLogger.error('Token refresh failed', e, stack);
      _refreshCompleter!.complete(null);
      return null;
    } finally {
      _isRefreshing = false;
      _refreshCompleter = null;
    }
  }

  /// Refresh token with exponential backoff
  Future<String?> _refreshWithBackoff() async {
    final clientId = AppConstants.msToDoClientId;
    final tenantId = AppConstants.msToDoTenantId;

    if (clientId.isEmpty) {
      AppLogger.warning('MS To-Do client ID not configured');
      return null;
    }

    int attempt = 0;
    const maxAttempts = 3;
    int delayMs = 1000; // Start with 1 second

    while (attempt < maxAttempts) {
      try {
        final newToken = await _oauth.refreshMsToDoToken(
          clientId: clientId,
          tenantId: tenantId,
        );

        if (newToken != null) {
          AppLogger.info('MS To-Do token refreshed successfully');
          return newToken;
        }
      } catch (e) {
        AppLogger.warning('Token refresh attempt ${attempt + 1} failed: $e');
      }

      attempt++;
      if (attempt < maxAttempts) {
        // Exponential backoff with jitter
        final jitter = Random().nextInt(500);
        final waitTime = Duration(milliseconds: delayMs + jitter);
        AppLogger.debug('Retrying token refresh in ${waitTime.inMilliseconds}ms');
        await Future.delayed(waitTime);
        delayMs = min(delayMs * 2, 30000); // Cap at 30 seconds
      }
    }

    return null;
  }

  /// Force refresh MS To-Do token (e.g., after 401 response)
  Future<String?> forceRefreshMsToDoToken() async {
    _refreshAttempts = 0; // Reset attempts for forced refresh
    return _refreshMsToDoToken();
  }

  /// Check if MS To-Do token is expired or about to expire
  Future<bool> isMsToDoTokenExpired() async {
    final expiry = await _storage.getMsToDoTokenExpiry();
    if (expiry == null) return false;

    final threshold = Duration(minutes: AppConstants.tokenRefreshThresholdMinutes);
    return DateTime.now().isAfter(expiry.subtract(threshold));
  }

  /// Reset refresh state (e.g., after re-authentication)
  void resetRefreshState() {
    _refreshAttempts = 0;
    _lastRefreshAttempt = null;
    _isRefreshing = false;
    _refreshCompleter = null;
  }

  /// Clear all tokens and reset state
  Future<void> clearAll() async {
    await _storage.clearTodoistTokens();
    await _storage.clearMsToDoTokens();
    resetRefreshState();
  }
}
