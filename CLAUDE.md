# Openza Tasks

Unified task manager for Linux integrating Todoist and Microsoft To-Do. Flutter frontend + Rust sync engine.

## Tech Stack

Flutter 3.7+, Rust 2024, Riverpod 3.0, Drift (SQLite), Freezed, GoRouter, OAuth 2.0

## Essential Commands

```bash
# Code generation (REQUIRED after modifying models/providers)
flutter pub run build_runner build --delete-conflicting-outputs

# Development
./dev.sh

# Verify before committing
flutter analyze && flutter test
```

## Critical Rules

**Git:** Never work on main branch. Always create feature branches.

**Database:** Never execute SQL migrations directly during development. Provide migration scripts for the developer to review and run manually. (Note: This does not apply to Drift's automatic runtime migrations for end users.)

**Sync Engine:** For synced tasks, only update local-enhancement fields to avoid conflicts with Rust sync.

**Code Generation:** Always regenerate after modifying `@freezed` entities or Riverpod providers.

## Security

**Never commit:** API keys, `.env` files, OAuth credentials, tokens

**Environment files:**
- `.env.example` - Template (committed)
- `.env.local` - Real credentials (gitignored)

**Before committing:** Run `gitleaks detect --source . --verbose`

**If secrets leak:** Rotate immediately, clean git history, notify maintainers

**In code:** Use `flutter_secure_storage` for credentials, never log sensitive data

## Architecture

```
lib/domain/     → Entities, repository interfaces
lib/data/       → Repositories, API clients, database
lib/presentation/ → Screens, providers, widgets
rust/           → Sync engine (FFI bridge)
```

## Patterns

- **State:** Riverpod (`ref.watch()`, `ConsumerWidget`, `FutureProvider`)
- **Models:** Freezed for immutability, JsonSerializable for serialization
- **Errors:** `ApiErrorHandler` + toastification for user feedback
- **Database:** Drift ORM, WAL mode, offline-first

## Links

- [Repository](https://github.com/openza/tasks)
- [Contributing Guide](CONTRIBUTING.md)
- Maintainer: [@solankydev](https://github.com/solankydev)
