# Repository Guidelines

Also follow the shared Openza guidance in `../AGENTS.md`. Keep this file limited to Tasks-specific constraints and commands.

## Project Structure & Module Organization
- `src/Openza.Tasks/` holds the active WinUI 3 app. Keep `MainWindow` as a thin host; put app shell behavior under `Shell/`, reusable UI in `Controls/`, settings/pages in `Pages/`, and Windows-only helpers in `Services/`.
- `src/Openza.Tasks.Core/` holds SQLite data access, migration, import/export, provider sync, credentials abstractions, and testable business logic.
- `src/Openza.Tasks.Tests/` holds xUnit tests for the WinUI migration.
- The legacy Flutter app is preserved on the `legacy-flutter-app` branch only; `main` is the active WinUI codebase.
- `legacy/rust-sync-reference/` contains the old Rust sync engine reference. Do not ship a Rust DLL in the WinUI app.
- `assets/` stores icons, images, and bundled resources used at runtime.
- `docs/`, `website/`, and `scripts/` provide supporting documentation and tooling.

## Build, Test, and Development Commands
- `dotnet restore Openza.Tasks.slnx` restores the active WinUI solution.
- `dotnet test src\Openza.Tasks.Tests\Openza.Tasks.Tests.csproj -c Release` runs unit and migration tests.
- `dotnet build src\Openza.Tasks\Openza.Tasks.csproj -c Release --no-restore` verifies the packaged WinUI app compiles.
- `pnpm --dir website build` verifies the docs site; if `pnpm` is not on PATH locally, use `$env:ASTRO_TELEMETRY_DISABLED='1'; npm --prefix website run build`.

## Coding Style & Naming Conventions
- Follow existing C# naming and nullable annotations.
- Prefer native WinUI controls and patterns described in `../AGENTS.md`.
- Keep sync provider code in `Openza.Tasks.Core`; keep UI code out of provider adapters and repositories.
- For syncable tasks, update only local-enhancement fields unless the sync engine explicitly owns the provider field.

## Product Principles
- Keep Openza Tasks flexible, not opinionated. The app may expose factual fields, counts, labels, statuses, dates, sources, and filters, but it should not interpret those facts for the user with judgmental workflow labels such as "needs action".
- Prefer user-defined labels, filters, and saved views over hardcoded concepts such as context, area, energy, person, or review ritual.
- Do not add a separate methodology-specific page or signal when the same outcome can be achieved through transparent data, project selection, labels, filters, and custom views.
- Treat Spaces as user-owned visibility scopes, not provider-specific concepts. Do not hardcode Work/Personal or any user's routing logic in app code; put one-off routing in explicit import/setup scripts or future user-configurable rules.
- Store V1 is a fresh-start WinUI product. Do not auto-migrate old local app stores unless the user explicitly changes that product decision.

## Testing Guidelines
- Prefer unit tests for repositories, migration, backup/restore, import/export, provider mappers, and sync jobs.
- Run the app project build after XAML changes; library tests alone are not enough.

## Commit & Pull Request Guidelines
- Follow conventional commits (e.g., `feat: add project filter`, `fix: handle sync timeout`).
- Include release/changelog updates when shipping versions.
- PRs should describe changes, reference issues, and include screenshots for UI changes.

## Security & Configuration
- This is a public open-source repo. Do not commit local SQLite databases, personal task data, provider cache snapshots, screenshots with private tasks, generated certificates, MSIX/AppPackages outputs, Store-private metadata, or machine-specific files.
- Never commit secrets; use `.env.example` for templates and keep `.env.local` gitignored.
- Use Windows Credential Locker for credentials and avoid logging sensitive data.
- Before committing, run `gitleaks detect --source . --verbose`.
- Provide SQL migration scripts instead of executing migrations directly in development.

## Agent-Specific Instructions
- Never work on `main`/`master`; always use a feature/fix branch.
- Obsidian is out of scope for the WinUI V1 unless explicitly brought back into scope.
- Do not reinstall, update, or relaunch the user's actual daily app package (`Openza.OpenzaTasks`) unless explicitly asked. Test builds, installs, and launches against the dev package (`Openza.OpenzaTasks.Dev`) first by default.
