import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:toastification/toastification.dart';

import '../presentation/providers/task_provider.dart';
import '../presentation/providers/theme_provider.dart';
import 'app_router.dart';
import 'app_theme.dart';

class OpenzaApp extends ConsumerWidget {
  const OpenzaApp({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // Pre-load local data before showing main UI
    // This prevents flash of empty state on app start
    final localDataReady = ref.watch(unifiedDataProvider);

    return ToastificationWrapper(
      // Use hasValue to determine if app should be shown:
      // - hasValue=true: Show app (either has data, or is refreshing with previous data)
      // - hasValue=false + isLoading: First load, show splash
      // - hasValue=false + hasError: Error on first load, show app anyway
      // This ensures Navigator is preserved during sync refreshes.
      child: localDataReady.hasValue || localDataReady.hasError
          ? _buildApp(ref)
          : _buildSplash(),
    );
  }

  Widget _buildApp(WidgetRef ref) {
    final router = ref.watch(appRouterProvider);
    final themeMode = ref.watch(themeModeProvider);
    return MaterialApp.router(
      title: 'Openza Tasks',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.lightTheme,
      darkTheme: AppTheme.darkTheme,
      themeMode: themeMode,
      themeAnimationDuration: Duration.zero, // Disable theme transition to avoid TextStyle lerp errors
      routerConfig: router,
    );
  }

  Widget _buildSplash() {
    return MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: AppTheme.lightTheme,
      darkTheme: AppTheme.darkTheme,
      themeMode: ThemeMode.system,
      home: const Scaffold(
        body: Center(
          child: CircularProgressIndicator(),
        ),
      ),
    );
  }
}
