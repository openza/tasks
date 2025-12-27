# Changelog

All notable changes to Openza Tasks will be documented in this file.

## [0.3.0] - 2025-12-27

### Added
- **Automatic Backup System**: Configurable backup frequency (hourly/daily/weekly) with daily auto-backup enabled by default for new users
- **Manual Backup Controls**: Create, restore, download, and import backups from the settings modal
- **Markdown Export**: Export all tasks grouped by project with preview dialog before saving
- **Markdown Import**: Import tasks from markdown files or pasted text with GFM checkbox syntax support
- **4-Pane GTD Layout**: New dashboard layout with NavRail | ProjectsPane | TasksList | TaskDetails
- **Dark Theme**: Complete dark theme support with improved visual consistency across all screens
- **Settings Modal**: Reorganized settings as a modal dialog with separate Backup, Import, and Export pages
- **Project Search**: Search functionality in the projects pane

### Fixed
- Theme switch crash caused by TextStyle interpolation during animation (#15)
- Use `deleteSync()` instead of async `delete()` in isolate functions for proper cleanup

### Changed
- Projects promoted to first-class citizens with dedicated navigation pane
- Simplified Add Task modal optimized for desktop usage
- Improved task detail screen with better project display
- New tasks default to Inbox when no project is selected

## [0.2.1] - 2025-12-20

### Fixed
- **Add Task button** in sidebar now works correctly
- **Task completion** in Next Actions screen now properly updates state
- **Navigation** in profile screen uses GoRouter instead of deprecated Navigator API
- **Null callback crash** prevented in task detail close button
- **API error logging** added (previously failed silently)
- **Cache clear button** now shows "Coming soon" instead of fake success message
- **TextField reactivity** in settings - clear icon updates as user types
- **Platform detection** uses actual Platform API instead of hard-coded value
- **Error display** in Next Actions shows user-friendly messages
- **DateTime parsing** in API clients handles malformed dates gracefully

### Changed
- Release workflow now creates draft releases for manual testing before publish

## [0.2.0] - 2025-12-18

### Added
- **Rust-based sync engine** for Todoist integration with FFI bindings
- **Outbox pattern** for offline-first task completion and reopening
- **Sync status badges** showing task source and sync state
- **Completed tasks screen** for viewing finished tasks
- **Default integrations** seeded on first launch (Openza Tasks, Todoist, Microsoft To-Do)

### Changed
- **Database architecture redesign** with proper foreign key relationships
- Tasks, projects, and labels now use `integration_id` FK instead of ID prefixing
- Added `external_id` column to store provider's original IDs
- Renamed `integrations` JSON column to `provider_metadata`
- **App identity** changed from `com.openza.openza_flutter` to `com.openza.tasks`
- **Database location** moved to platform-appropriate Application Support directory:
  - Linux: `~/.local/share/com.openza.tasks/openza_tasks.db`
  - macOS: `~/Library/Application Support/com.openza.tasks/openza_tasks.db`
  - Windows: `%APPDATA%\com.openza.tasks\openza_tasks.db`

### Removed
- Deprecated task fields: `energyLevel`, `focusTime`, `estimatedDuration`, `actualDuration`, `context`
- `TaskProvider` and `TaskContext` enums (replaced with `integrationId` string)

### Migration
- Automatic schema migration from v1 to v2 for existing users
- Automatic database file location migration

## [0.1.4] - 2025-12-13

### Fixed
- Flatpak metadata and screenshots
- Flutter version compatibility in CI

## [0.1.3] - 2025-12-13

### Fixed
- Use latest stable Flutter in CI for Dart 3.7+ compatibility

## [0.1.0] - 2025-12-13

### Added
- Initial release
- Todoist integration via API key
- Microsoft To-Do integration via OAuth
- Local task storage with SQLite
- Unified task view across all providers
