/// Storage keys for secure storage
class StorageKeys {
  StorageKeys._();

  // Auth tokens
  static const String todoistAccessToken = 'todoist_access_token';
  static const String todoistRefreshToken = 'todoist_refresh_token';
  static const String todoistTokenExpiry = 'todoist_token_expiry';

  static const String msToDoAccessToken = 'mstodo_access_token';
  static const String msToDoRefreshToken = 'mstodo_refresh_token';
  static const String msToDoTokenExpiry = 'mstodo_token_expiry';

  // Provider state
  static const String activeProvider = 'active_provider';
  static const String taskSource = 'task_source';

  // OAuth state
  static const String oauthState = 'oauth_state';
  static const String oauthCodeVerifier = 'oauth_code_verifier';

  // Obsidian
  static const String obsidianVaultPath = 'obsidian_vault_path';

  // User preferences
  static const String themeMode = 'theme_mode';
  static const String sidebarCollapsed = 'sidebar_collapsed';
  static const String defaultProject = 'default_project';

  // Backup preferences
  static const String backupAutoEnabled = 'backup_auto_enabled';
  static const String backupFrequency = 'backup_frequency';
  static const String lastBackupTime = 'last_backup_time';
}
