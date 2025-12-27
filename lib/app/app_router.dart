import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../presentation/screens/today/today_screen.dart';
import '../presentation/screens/overdue/overdue_screen.dart';
import '../presentation/screens/next_actions/next_actions_screen.dart';
import '../presentation/screens/tasks/tasks_screen.dart';
import '../presentation/screens/completed/completed_screen.dart';
import '../presentation/screens/profile/profile_screen.dart';
import '../presentation/screens/auth/login_screen.dart';
import '../presentation/screens/settings/settings_screen.dart';
import '../presentation/widgets/layout/dashboard_layout.dart';

// Route names
class AppRoutes {
  static const String login = '/login';
  static const String today = '/today';
  static const String overdue = '/overdue';
  static const String nextActions = '/next-actions';
  static const String tasks = '/tasks';
  static const String completed = '/completed';
  static const String profile = '/profile';
  static const String settings = '/settings';
}

// Router provider
final appRouterProvider = Provider<GoRouter>((ref) {
  return GoRouter(
    initialLocation: AppRoutes.nextActions,
    debugLogDiagnostics: true,
    redirect: (context, state) {
      // TODO: Add authentication check
      // final isLoggedIn = ref.read(authProvider).isAuthenticated;
      // final isLoginRoute = state.uri.path == AppRoutes.login;
      //
      // if (!isLoggedIn && !isLoginRoute) {
      //   return AppRoutes.login;
      // }
      // if (isLoggedIn && isLoginRoute) {
      //   return AppRoutes.nextActions;
      // }
      return null;
    },
    routes: [
      // Login route (outside shell)
      GoRoute(
        path: AppRoutes.login,
        name: 'login',
        builder: (context, state) => const LoginScreen(),
      ),

      // Main app routes with DashboardLayout shell
      ShellRoute(
        builder: (context, state, child) => DashboardLayout(child: child),
        routes: [
          GoRoute(
            path: '/',
            redirect: (context, state) => AppRoutes.nextActions,
          ),
          GoRoute(
            path: AppRoutes.today,
            name: 'today',
            pageBuilder: (context, state) => NoTransitionPage(
              key: state.pageKey,
              child: const TodayScreen(),
            ),
          ),
          GoRoute(
            path: AppRoutes.overdue,
            name: 'overdue',
            pageBuilder: (context, state) => NoTransitionPage(
              key: state.pageKey,
              child: const OverdueScreen(),
            ),
          ),
          GoRoute(
            path: AppRoutes.nextActions,
            name: 'next-actions',
            pageBuilder: (context, state) => NoTransitionPage(
              key: state.pageKey,
              child: const NextActionsScreen(),
            ),
          ),
          GoRoute(
            path: AppRoutes.tasks,
            name: 'tasks',
            pageBuilder: (context, state) {
              final projectId = state.uri.queryParameters['projectId'];
              return NoTransitionPage(
                key: state.pageKey,
                child: TasksScreen(projectId: projectId),
              );
            },
          ),
          GoRoute(
            path: AppRoutes.completed,
            name: 'completed',
            pageBuilder: (context, state) => NoTransitionPage(
              key: state.pageKey,
              child: const CompletedScreen(),
            ),
          ),
          GoRoute(
            path: AppRoutes.profile,
            name: 'profile',
            pageBuilder: (context, state) => NoTransitionPage(
              key: state.pageKey,
              child: const ProfileScreen(),
            ),
          ),
          GoRoute(
            path: AppRoutes.settings,
            name: 'settings',
            pageBuilder: (context, state) => NoTransitionPage(
              key: state.pageKey,
              child: const SettingsScreen(),
            ),
          ),
        ],
      ),
    ],
    errorBuilder: (context, state) => Scaffold(
      body: Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const Icon(Icons.error_outline, size: 64, color: Colors.red),
            const SizedBox(height: 16),
            Text(
              'Page not found',
              style: Theme.of(context).textTheme.headlineSmall,
            ),
            const SizedBox(height: 8),
            Text(
              state.uri.path,
              style: Theme.of(context).textTheme.bodyMedium,
            ),
            const SizedBox(height: 24),
            ElevatedButton(
              onPressed: () => context.go(AppRoutes.nextActions),
              child: const Text('Go to Next Actions'),
            ),
          ],
        ),
      ),
    ),
  );
});
