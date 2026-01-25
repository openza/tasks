# Contributing to Openza Tasks

Thank you for your interest in contributing! This guide covers the development workflow and PR review process.

## Development Setup

1. Install Linux dependencies:
   ```bash
   sudo apt-get install -y clang cmake ninja-build pkg-config libgtk-3-dev liblzma-dev libstdc++-12-dev
   ```

2. Configure OAuth credentials:
   ```bash
   cp .env.example .env.local
   # Edit .env.local with your credentials from Todoist/Microsoft developer portals
   ```

3. Install and generate:
   ```bash
   flutter pub get
   flutter pub run build_runner build --delete-conflicting-outputs
   ```

4. Run: `./dev.sh`

See `docs/development/environment.md` for toolchain versions and optional local hooks.

## Making Changes

1. Create a feature branch:
   ```bash
   git checkout -b feature/your-feature-name
   ```

2. Make your changes following the patterns in [CLAUDE.md](CLAUDE.md)

3. Regenerate code if you modified models/providers:
   ```bash
   flutter pub run build_runner build --delete-conflicting-outputs
   ```

4. Verify your changes:
   ```bash
   flutter analyze
   flutter test
   ```

5. Optional local hooks (recommended):
   ```bash
   git config core.hooksPath .githooks
   ```

6. Run secret scanning before committing:
   ```bash
   gitleaks detect --source . --verbose
   ```

7. Commit using conventional commits:
   ```bash
   git commit -m "feat: add new feature"
   # Types: feat, fix, chore, docs, refactor, test
   ```

## PR Review Workflow

### Before Starting Review

```bash
# Fetch and checkout the PR branch
git fetch origin pull/<PR_NUMBER>/head:pr-<PR_NUMBER>
git checkout pr-<PR_NUMBER>

# Install and generate
flutter pub get
flutter pub run build_runner build --delete-conflicting-outputs
```

### Code Quality Checklist

- [ ] Follows Clean Architecture (domain/data/presentation layers)
- [ ] Uses existing patterns (Riverpod, Freezed, GoRouter)
- [ ] Naming conventions followed (PascalCase classes, snake_case files)
- [ ] No unnecessary code duplication
- [ ] Error handling with user feedback (toasts)

### Security Checklist

- [ ] No hardcoded secrets, API keys, or credentials
- [ ] No sensitive data in logs or error messages
- [ ] Input validation on user-provided data
- [ ] API errors handled gracefully
- [ ] OAuth tokens use secure storage

### Build Verification

```bash
flutter analyze
flutter test
flutter build linux
```

### Approval Criteria

- **Approve:** All checklists pass, tests pass
- **Request Changes:** Security issues, broken tests, architectural violations
- **Block:** Committed secrets, malicious code, license violations

## Code Style

- **Dart:** Follow standard Dart conventions (PascalCase classes, snake_case files)
- **Models:** Use `@freezed` for entities, `@JsonSerializable()` for serialization
- **State:** Use Riverpod with `ConsumerWidget` and `ref.watch()`
- **Errors:** Use `ApiErrorHandler` and show user feedback via toasts

## Questions?

Open an issue or reach out to [@solankydev](https://github.com/solankydev).
