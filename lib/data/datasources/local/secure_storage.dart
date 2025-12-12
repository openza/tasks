import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../../../core/constants/storage_keys.dart';
import '../../../core/utils/logger.dart';

/// Secure storage service for sensitive data (tokens, credentials)
class SecureStorageService {
  static SecureStorageService? _instance;
  late final FlutterSecureStorage _storage;

  SecureStorageService._() {
    _storage = const FlutterSecureStorage(
      aOptions: AndroidOptions(
        encryptedSharedPreferences: true,
      ),
      lOptions: LinuxOptions(),
      wOptions: WindowsOptions(),
      mOptions: MacOsOptions(
        accessibility: KeychainAccessibility.first_unlock,
      ),
    );
  }

  /// Get singleton instance
  static SecureStorageService get instance {
    _instance ??= SecureStorageService._();
    return _instance!;
  }

  /// Write a value to secure storage
  Future<void> write({required String key, required String? value}) async {
    try {
      if (value == null) {
        await delete(key: key);
        return;
      }
      await _storage.write(key: key, value: value);
      AppLogger.debug('Wrote to secure storage: $key');
    } catch (e, stack) {
      AppLogger.error('Failed to write to secure storage: $key', e, stack);
      rethrow;
    }
  }

  /// Read a value from secure storage
  Future<String?> read({required String key}) async {
    try {
      final value = await _storage.read(key: key);
      return value;
    } catch (e, stack) {
      AppLogger.error('Failed to read from secure storage: $key', e, stack);
      return null;
    }
  }

  /// Delete a value from secure storage
  Future<void> delete({required String key}) async {
    try {
      await _storage.delete(key: key);
      AppLogger.debug('Deleted from secure storage: $key');
    } catch (e, stack) {
      AppLogger.error('Failed to delete from secure storage: $key', e, stack);
    }
  }

  /// Check if a key exists
  Future<bool> containsKey({required String key}) async {
    try {
      return await _storage.containsKey(key: key);
    } catch (e) {
      return false;
    }
  }

  /// Delete all stored values
  Future<void> deleteAll() async {
    try {
      await _storage.deleteAll();
      AppLogger.info('Cleared all secure storage');
    } catch (e, stack) {
      AppLogger.error('Failed to clear secure storage', e, stack);
    }
  }

  // ============ CONVENIENCE METHODS ============

  /// Store Todoist tokens
  Future<void> storeTodoistTokens({
    required String accessToken,
    String? refreshToken,
    DateTime? expiry,
  }) async {
    await write(key: StorageKeys.todoistAccessToken, value: accessToken);
    if (refreshToken != null) {
      await write(key: StorageKeys.todoistRefreshToken, value: refreshToken);
    }
    if (expiry != null) {
      await write(
        key: StorageKeys.todoistTokenExpiry,
        value: expiry.toIso8601String(),
      );
    }
  }

  /// Get Todoist access token
  Future<String?> getTodoistAccessToken() async {
    return read(key: StorageKeys.todoistAccessToken);
  }

  /// Clear Todoist tokens
  Future<void> clearTodoistTokens() async {
    await delete(key: StorageKeys.todoistAccessToken);
    await delete(key: StorageKeys.todoistRefreshToken);
    await delete(key: StorageKeys.todoistTokenExpiry);
  }

  /// Store MS To-Do tokens
  Future<void> storeMsToDoTokens({
    required String accessToken,
    String? refreshToken,
    DateTime? expiry,
  }) async {
    await write(key: StorageKeys.msToDoAccessToken, value: accessToken);
    if (refreshToken != null) {
      await write(key: StorageKeys.msToDoRefreshToken, value: refreshToken);
    }
    if (expiry != null) {
      await write(
        key: StorageKeys.msToDoTokenExpiry,
        value: expiry.toIso8601String(),
      );
    }
  }

  /// Get MS To-Do access token
  Future<String?> getMsToDoAccessToken() async {
    return read(key: StorageKeys.msToDoAccessToken);
  }

  /// Get MS To-Do refresh token
  Future<String?> getMsToDoRefreshToken() async {
    return read(key: StorageKeys.msToDoRefreshToken);
  }

  /// Clear MS To-Do tokens
  Future<void> clearMsToDoTokens() async {
    await delete(key: StorageKeys.msToDoAccessToken);
    await delete(key: StorageKeys.msToDoRefreshToken);
    await delete(key: StorageKeys.msToDoTokenExpiry);
  }

  /// Store active provider
  Future<void> storeActiveProvider(String provider) async {
    await write(key: StorageKeys.activeProvider, value: provider);
  }

  /// Get active provider
  Future<String?> getActiveProvider() async {
    return read(key: StorageKeys.activeProvider);
  }
}
