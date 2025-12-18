# Changelog

All notable changes to Openza Tasks will be documented in this file.

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
