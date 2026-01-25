# Repository Guidelines

## Project Structure & Module Organization
- `lib/` holds the Flutter app: `lib/domain/` (entities, interfaces), `lib/data/` (repositories, APIs, database), `lib/presentation/` (screens, providers, widgets).
- `rust/` contains the Rust sync engine and FFI bridge used by Flutter.
- `assets/` stores icons, images, and bundled resources used at runtime.
- `test/` contains Flutter unit/widget tests; Rust tests live alongside code in `rust/`.
- `docs/`, `website/`, and `scripts/` provide supporting documentation and tooling.

## Build, Test, and Development Commands
- `./dev.sh` starts the local development workflow (preferred entry point).
- `flutter pub run build_runner build --delete-conflicting-outputs` regenerates Freezed/JsonSerializable/Riverpod code after model/provider edits.
- `flutter analyze` runs static analysis against `analysis_options.yaml`.
- `flutter test` executes Flutter tests in `test/`.
- `cargo test` (from `rust/`) runs Rust sync engine tests.

## Coding Style & Naming Conventions
- Use Dart standard formatting (`dart format .`) and keep 2-space indentation.
- Keep Riverpod providers and Freezed models in their feature folders; regenerate code after edits.
- Prefer descriptive file names (`*_provider.dart`, `*_repository.dart`, `*_screen.dart`).
- For syncable tasks, update only local-enhancement fields to avoid Rust sync conflicts.

## Testing Guidelines
- Prefer unit tests for pure logic and widget tests for UI behavior.
- Name tests descriptively (e.g., `task_repository_test.dart`).
- Run `flutter test` before opening a PR; add Rust tests for sync logic changes.

## Commit & Pull Request Guidelines
- Follow conventional commits (e.g., `feat: add project filter`, `fix: handle sync timeout`).
- Include release/changelog updates when shipping versions.
- PRs should describe changes, reference issues, and include screenshots for UI changes.

## Security & Configuration
- Never commit secrets; use `.env.example` for templates and keep `.env.local` gitignored.
- Use `flutter_secure_storage` for credentials and avoid logging sensitive data.
- Before committing, run `gitleaks detect --source . --verbose`.
- Provide SQL migration scripts instead of executing migrations directly in development.

## Agent-Specific Instructions
- Never work on `main`/`master`; always use a feature/fix branch.
- Run code generation after editing Freezed entities or Riverpod providers.
