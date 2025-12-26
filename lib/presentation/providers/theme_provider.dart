import 'package:flutter/material.dart';
// ignore: deprecated_member_use
import 'package:flutter_riverpod/legacy.dart';

import '../../core/constants/storage_keys.dart';
import '../../data/datasources/local/secure_storage.dart';

/// Theme mode notifier for managing app theme with persistence
class ThemeModeNotifier extends StateNotifier<ThemeMode> {
  ThemeModeNotifier() : super(ThemeMode.system) {
    _loadSavedTheme();
  }

  final _storage = SecureStorageService.instance;

  /// Load saved theme from storage
  Future<void> _loadSavedTheme() async {
    final savedTheme = await _storage.read(key: StorageKeys.themeMode);
    if (savedTheme != null) {
      state = _themeModeFromString(savedTheme);
    }
  }

  /// Set theme mode and persist
  Future<void> setThemeMode(ThemeMode mode) async {
    state = mode;
    await _storage.write(
      key: StorageKeys.themeMode,
      value: _themeModeToString(mode),
    );
  }

  /// Convert string to ThemeMode
  ThemeMode _themeModeFromString(String value) {
    switch (value) {
      case 'light':
        return ThemeMode.light;
      case 'dark':
        return ThemeMode.dark;
      default:
        return ThemeMode.system;
    }
  }

  /// Convert ThemeMode to string
  String _themeModeToString(ThemeMode mode) {
    switch (mode) {
      case ThemeMode.light:
        return 'light';
      case ThemeMode.dark:
        return 'dark';
      case ThemeMode.system:
        return 'system';
    }
  }
}

/// Provider for theme mode state
final themeModeProvider =
    StateNotifierProvider<ThemeModeNotifier, ThemeMode>((ref) {
  return ThemeModeNotifier();
});
