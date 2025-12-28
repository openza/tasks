import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:dio/dio.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../../../core/constants/api_endpoints.dart';
import '../../../../core/constants/app_constants.dart';
import '../../../../core/utils/logger.dart';
import '../../../datasources/local/secure_storage.dart';

/// OAuth service for handling authentication with external providers
class OAuthService {
  final Dio _dio;
  final SecureStorageService _storage;
  HttpServer? _redirectServer;

  OAuthService({Dio? dio})
      : _dio = dio ?? Dio(),
        _storage = SecureStorageService.instance;

  /// Generate a random state for CSRF protection
  String _generateState() {
    final random = Random.secure();
    final values = List<int>.generate(32, (i) => random.nextInt(256));
    return base64Url.encode(values);
  }

  /// Start OAuth flow for Todoist
  Future<String?> authenticateTodoist({
    required String clientId,
    required String clientSecret,
    String redirectUri = 'http://localhost:8585/callback',
  }) async {
    final state = _generateState();
    final scopes = ApiEndpoints.todoistScopes.join(',');

    // Build authorization URL
    final authUrl = Uri.parse(ApiEndpoints.todoistAuthUrl).replace(
      queryParameters: {
        'client_id': clientId,
        'scope': scopes,
        'state': state,
        'redirect_uri': redirectUri,
      },
    );

    // Start local server to receive callback
    final code = await _startCallbackServer(
      port: 8585,
      expectedState: state,
      authUrl: authUrl,
    );

    if (code == null) return null;

    // Exchange code for token
    try {
      final response = await _dio.post(
        ApiEndpoints.todoistTokenUrl,
        data: {
          'client_id': clientId,
          'client_secret': clientSecret,
          'code': code,
          'redirect_uri': redirectUri,
        },
        options: Options(
          contentType: Headers.formUrlEncodedContentType,
        ),
      );

      if (response.statusCode == 200) {
        final accessToken = response.data['access_token'] as String;
        await _storage.storeTodoistTokens(accessToken: accessToken);
        AppLogger.info('Todoist OAuth: Authentication successful');
        return accessToken;
      }
    } catch (e, stack) {
      AppLogger.error('Todoist OAuth: Error exchanging code for token', e, stack);
    }

    return null;
  }

  /// Start OAuth flow for Microsoft To-Do
  Future<String?> authenticateMsToDo({
    required String clientId,
    required String tenantId,
    String redirectUri = 'http://localhost:8586/callback',
  }) async {
    final state = _generateState();
    final scopes = ApiEndpoints.msToDoScopes.join(' ');

    // Build authorization URL for Microsoft identity platform
    final authUrl = Uri.parse(
      '${ApiEndpoints.msAuthUrl}/$tenantId/oauth2/v2.0/authorize',
    ).replace(
      queryParameters: {
        'client_id': clientId,
        'response_type': 'code',
        'redirect_uri': redirectUri,
        'response_mode': 'query',
        'scope': scopes,
        'state': state,
      },
    );

    // Start local server to receive callback
    final code = await _startCallbackServer(
      port: 8586,
      expectedState: state,
      authUrl: authUrl,
    );

    if (code == null) return null;

    // Exchange code for token
    try {
      final tokenUrl = '${ApiEndpoints.msAuthUrl}/$tenantId/oauth2/v2.0/token';
      final response = await _dio.post(
        tokenUrl,
        data: {
          'client_id': clientId,
          'scope': scopes,
          'code': code,
          'redirect_uri': redirectUri,
          'grant_type': 'authorization_code',
        },
        options: Options(
          contentType: Headers.formUrlEncodedContentType,
        ),
      );

      if (response.statusCode == 200) {
        final accessToken = response.data['access_token'] as String;
        final refreshToken = response.data['refresh_token'] as String?;
        final expiresIn = response.data['expires_in'] as int?;

        DateTime? expiry;
        if (expiresIn != null) {
          expiry = DateTime.now().add(Duration(seconds: expiresIn));
        }

        await _storage.storeMsToDoTokens(
          accessToken: accessToken,
          refreshToken: refreshToken,
          expiry: expiry,
        );
        AppLogger.info('MS To-Do OAuth: Authentication successful');
        return accessToken;
      }
    } catch (e, stack) {
      AppLogger.error('MS To-Do OAuth: Error exchanging code for token', e, stack);
    }

    return null;
  }

  /// Start a local HTTP server to receive OAuth callback
  Future<String?> _startCallbackServer({
    required int port,
    required String expectedState,
    Uri? authUrl,
  }) async {
    try {
      _redirectServer = await HttpServer.bind(InternetAddress.loopbackIPv4, port);
      AppLogger.debug('OAuth callback server started on port $port');

      // Launch browser with auth URL
      if (authUrl != null) {
        final launched = await launchUrl(authUrl, mode: LaunchMode.externalApplication);
        if (!launched) {
          AppLogger.error('Failed to launch browser for OAuth');
          await _redirectServer?.close();
          return null;
        }
      }

      // Wait for callback
      final completer = Completer<String?>();

      _redirectServer!.listen((request) async {
        final uri = request.uri;

        if (uri.path == '/callback') {
          final code = uri.queryParameters['code'];
          final state = uri.queryParameters['state'];
          final error = uri.queryParameters['error'];

          // Send response to browser
          request.response
            ..statusCode = 200
            ..headers.contentType = ContentType.html
            ..write(_getCallbackHtml(error == null && code != null));
          await request.response.close();

          // Validate state and return code
          if (error != null) {
            AppLogger.warning('OAuth error: $error');
            completer.complete(null);
          } else if (state != expectedState) {
            AppLogger.warning('OAuth state mismatch');
            completer.complete(null);
          } else {
            completer.complete(code);
          }

          // Close server
          await _redirectServer?.close();
        }
      });

      // Timeout after configured minutes
      return completer.future.timeout(
        Duration(minutes: AppConstants.oauthTimeoutMinutes),
        onTimeout: () async {
          AppLogger.warning('OAuth timeout');
          await _redirectServer?.close();
          return null;
        },
      );
    } catch (e, stack) {
      AppLogger.error('Error starting OAuth callback server', e, stack);
      return null;
    }
  }

  String _getCallbackHtml(bool success) {
    return '''
<!DOCTYPE html>
<html>
<head>
  <title>Openza Tasks - Authentication</title>
  <style>
    body {
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
      display: flex;
      justify-content: center;
      align-items: center;
      height: 100vh;
      margin: 0;
      background: linear-gradient(135deg, #EFF6FF, #FDF2F8, #F3E8FF);
    }
    .container {
      text-align: center;
      padding: 40px;
      background: white;
      border-radius: 16px;
      box-shadow: 0 4px 6px rgba(0, 0, 0, 0.1);
    }
    .icon { font-size: 64px; margin-bottom: 20px; }
    h1 { color: #1a1a1a; margin-bottom: 10px; }
    p { color: #666; }
  </style>
</head>
<body>
  <div class="container">
    <div class="icon">${success ? '✅' : '❌'}</div>
    <h1>${success ? 'Authentication Successful!' : 'Authentication Failed'}</h1>
    <p>${success ? 'You can close this window and return to Openza Tasks.' : 'Please try again.'}</p>
  </div>
</body>
</html>
''';
  }

  /// Refresh MS To-Do access token
  Future<String?> refreshMsToDoToken({
    required String clientId,
    required String tenantId,
  }) async {
    final refreshToken = await _storage.getMsToDoRefreshToken();
    if (refreshToken == null) return null;

    try {
      final tokenUrl = '${ApiEndpoints.msAuthUrl}/$tenantId/oauth2/v2.0/token';
      final response = await _dio.post(
        tokenUrl,
        data: {
          'client_id': clientId,
          'scope': ApiEndpoints.msToDoScopes.join(' '),
          'refresh_token': refreshToken,
          'grant_type': 'refresh_token',
        },
        options: Options(
          contentType: Headers.formUrlEncodedContentType,
        ),
      );

      if (response.statusCode == 200) {
        final accessToken = response.data['access_token'] as String;
        final newRefreshToken = response.data['refresh_token'] as String?;
        final expiresIn = response.data['expires_in'] as int?;

        DateTime? expiry;
        if (expiresIn != null) {
          expiry = DateTime.now().add(Duration(seconds: expiresIn));
        }

        await _storage.storeMsToDoTokens(
          accessToken: accessToken,
          refreshToken: newRefreshToken ?? refreshToken,
          expiry: expiry,
        );
        AppLogger.info('MS To-Do: Token refreshed successfully');
        return accessToken;
      }
    } catch (e, stack) {
      AppLogger.error('MS To-Do: Error refreshing token', e, stack);
    }

    return null;
  }

  /// Logout from Todoist
  Future<void> logoutTodoist() async {
    await _storage.clearTodoistTokens();
    AppLogger.info('Logged out from Todoist');
  }

  /// Logout from MS To-Do
  Future<void> logoutMsToDo() async {
    await _storage.clearMsToDoTokens();
    AppLogger.info('Logged out from MS To-Do');
  }

  /// Logout from all providers
  Future<void> logoutAll() async {
    await logoutTodoist();
    await logoutMsToDo();
  }

  /// Check if authenticated with Todoist
  Future<bool> isTodoistAuthenticated() async {
    final token = await _storage.getTodoistAccessToken();
    return token != null && token.isNotEmpty;
  }

  /// Check if authenticated with MS To-Do
  Future<bool> isMsToDoAuthenticated() async {
    final token = await _storage.getMsToDoAccessToken();
    return token != null && token.isNotEmpty;
  }

  /// Get Todoist access token
  Future<String?> getTodoistToken() async {
    return _storage.getTodoistAccessToken();
  }

  /// Get MS To-Do access token
  Future<String?> getMsToDoToken() async {
    return _storage.getMsToDoAccessToken();
  }
}
