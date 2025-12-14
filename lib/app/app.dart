import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:toastification/toastification.dart';

import '../presentation/providers/task_provider.dart';
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
      child: localDataReady.when(
        skipLoadingOnRefresh: true,
        data: (_) => _buildApp(ref),
        loading: () => _buildSplash(),
        error: (_, __) => _buildApp(ref), // Show app even on error
      ),
    );
  }

  Widget _buildApp(WidgetRef ref) {
    final router = ref.watch(appRouterProvider);
    return MaterialApp.router(
      title: 'Openza',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.lightTheme,
      darkTheme: AppTheme.darkTheme,
      themeMode: ThemeMode.system,
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
