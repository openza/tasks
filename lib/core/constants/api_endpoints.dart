/// API endpoints for external services
class ApiEndpoints {
  ApiEndpoints._();

  // Todoist API
  static const String todoistBaseUrl = 'https://api.todoist.com/rest/v2';
  static const String todoistSyncUrl = 'https://api.todoist.com/sync/v9';
  static const String todoistAuthUrl = 'https://todoist.com/oauth/authorize';
  static const String todoistTokenUrl = 'https://todoist.com/oauth/access_token';

  // Todoist endpoints
  static const String todoistTasks = '/tasks';
  static const String todoistProjects = '/projects';
  static const String todoistLabels = '/labels';
  static const String todoistSections = '/sections';
  static const String todoistComments = '/comments';

  // Microsoft Graph API (To-Do)
  static const String msGraphBaseUrl = 'https://graph.microsoft.com/v1.0';
  static const String msAuthUrl = 'https://login.microsoftonline.com';

  // Microsoft To-Do endpoints
  static const String msToDoLists = '/me/todo/lists';
  static const String msToDoTasks = '/tasks'; // Appended to list path

  // OAuth scopes
  static const List<String> todoistScopes = [
    'data:read_write',
    'data:delete',
    'project:delete',
  ];

  static const List<String> msToDoScopes = [
    'https://graph.microsoft.com/Tasks.ReadWrite',
    'https://graph.microsoft.com/User.Read',
    'offline_access',
  ];
}
