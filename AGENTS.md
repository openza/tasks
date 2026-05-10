# Repository Guidelines

Also follow the shared Openza guidance in `../AGENTS.md`. Keep this file limited to Tasks-specific constraints and commands.

## Project Structure & Module Organization
- `src/Openza.Tasks/` holds the active WinUI 3 app. Keep `MainWindow` as a thin host; put app shell behavior under `Shell/`, reusable UI in `Controls/`, settings/pages in `Pages/`, and Windows-only helpers in `Services/`.
- `src/Openza.Tasks.Core/` holds SQLite data access, migration, import/export, provider sync, credentials abstractions, and testable business logic.
- `src/Openza.Tasks.Tests/` holds xUnit tests for the WinUI migration.
- `lib/` holds the legacy Flutter app for history/reference only.
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

## Testing Guidelines
- Prefer unit tests for repositories, migration, backup/restore, import/export, provider mappers, and sync jobs.
- Run the app project build after XAML changes; library tests alone are not enough.

## Commit & Pull Request Guidelines
- Follow conventional commits (e.g., `feat: add project filter`, `fix: handle sync timeout`).
- Include release/changelog updates when shipping versions.
- PRs should describe changes, reference issues, and include screenshots for UI changes.

## Security & Configuration
- Never commit secrets; use `.env.example` for templates and keep `.env.local` gitignored.
- Use Windows Credential Locker for credentials and avoid logging sensitive data.
- Before committing, run `gitleaks detect --source . --verbose`.
- Provide SQL migration scripts instead of executing migrations directly in development.

## Agent-Specific Instructions
- Never work on `main`/`master`; always use a feature/fix branch.
- Obsidian is out of scope for the WinUI V1 unless explicitly brought back into scope.
