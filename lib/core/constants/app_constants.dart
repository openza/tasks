/// Application-wide constants
class AppConstants {
  AppConstants._();

  // App info
  static const String appName = 'Openza Tasks';
  static const String appDescription =
      'Store tasks locally. Sync with your favorite services.';
  static const String githubUrl = 'https://github.com/openza/tasks';
  static const String websiteUrl = 'https://openza.github.io/tasks/';

  // OAuth configuration
  static const String oauthScheme = 'openza';
  static const String oauthRedirectPath = '/auth/callback';
  static const int oauthTimeoutMinutes = 3;

  // Todoist OAuth credentials (from environment)
  static String get todoistClientId =>
      const String.fromEnvironment('TODOIST_CLIENT_ID', defaultValue: '');
  static String get todoistClientSecret =>
      const String.fromEnvironment('TODOIST_CLIENT_SECRET', defaultValue: '');

  // Microsoft To-Do OAuth credentials (from environment)
  static String get msToDoClientId =>
      const String.fromEnvironment('MSTODO_CLIENT_ID', defaultValue: '');
  static String get msToDoTenantId =>
      const String.fromEnvironment('MSTODO_TENANT_ID', defaultValue: 'common');

  // Token refresh
  static const int tokenRefreshThresholdMinutes = 5;
  static const int maxTokenRefreshAttempts = 3;
  static const int tokenRefreshCooldownSeconds = 30;

  // API limits
  static const int todoistPageSize = 50;
  static const int msToDoPageSize = 100;

  // Cache durations
  static const Duration tasksCacheDuration = Duration(minutes: 5);
  static const Duration projectsCacheDuration = Duration(minutes: 10);

  // UI constants
  static const double sidebarWidth = 240;
  static const double sidebarWidthExpanded = 256;
  static const double minWindowWidth = 800;
  static const double minWindowHeight = 600;

  // Priorities (1 = highest, 4 = lowest for Todoist)
  static const Map<int, String> priorityLabels = {
    1: 'High',
    2: 'Medium',
    3: 'Normal',
    4: 'Low',
  };

  // Energy levels
  static const Map<int, String> energyLabels = {
    1: 'Low',
    2: 'Normal',
    3: 'Medium',
    4: 'High',
    5: 'Peak',
  };

  // Task contexts
  static const List<String> taskContexts = [
    'work',
    'personal',
    'errands',
    'home',
    'office',
  ];

  // Task statuses
  static const List<String> taskStatuses = [
    'pending',
    'in_progress',
    'completed',
    'cancelled',
  ];
}
