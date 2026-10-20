# Repository Guidelines

Also follow the shared Openza guidance in `../AGENTS.md`. Keep this file limited to Tasks-specific constraints and commands.

## Current Platform Focus
- Focus only on Linux Snap for the coming weeks/months, until Linux is stable.
- All Windows work is parked, including Avalonia Windows packaging, MSIX, native testing, Store replacement and continuity policy decisions. Do not start Windows tasks unless the maintainer explicitly asks.
- The optional Windows CI job is an early warning only. Do not block Linux work on it or investigate/act on its results unless the maintainer explicitly asks.
- Keep shared Core, Application and Desktop code cross-platform and Windows-compatible. Do not add Linux-only assumptions; keep OS-specific behavior behind platform services.

## Project Structure & Module Organization
- `src/Openza.Tasks/` retains the frozen Windows-native WinUI 3 app as the existing Store host and continuity reference. Its last Microsoft Store release is final; do not maintain, release or remove this project.
- `src/Openza.Tasks.Desktop/` holds the cross-platform Avalonia desktop app. Current development and delivery focus on Linux Snap; Windows work is parked under the policy above. Keep platform services under `Services/`, window composition under `Shell/`, and testable presentation state under `ViewModels/`.
- `src/Openza.Tasks.Application/` holds UI-independent application operations and runtime coordination over Core.
- `src/Openza.Tasks.Core/` holds SQLite data access, migration, import/export, provider sync, credentials abstractions, and testable business logic.
- `src/Openza.Tasks.Tests/` holds xUnit tests for Core and both application hosts.
- Existing Microsoft Store users remain on the final WinUI release until a future Avalonia replacement passes the Windows transition gates. Keep both projects; the frozen Store app receives no further releases or maintenance. The legacy Flutter app remains on the `legacy-flutter-app` branch only.
- Historical Rust sync work is preserved in git history and older releases. Do not add its binary to either active host.
- `assets/` stores icons, images, and bundled resources used at runtime.
- `docs/` and `scripts/` provide developer documentation and tooling. The user guide lives in the [openza/website](https://github.com/openza/website) repository and is published at [openza.org/tasks](https://openza.org/tasks/).

## Build, Test, and Development Commands
- `dotnet test src/Openza.Tasks.Tests/Openza.Tasks.Tests.csproj -c Release` runs the shared test suite.
- From `src/Openza.Tasks.Desktop`, `dotnet restore` and `dotnet build -c Release --no-restore` validate Avalonia on Linux with its desktop SDK selection. Restore and run the shared tests from that directory as well.
- CI requires `build-test-linux` and `build-snap`. The optional, nonblocking Windows `build-test` job builds Avalonia and runs the shared suite as an early warning only; it does not authorize Windows work or prove package acceptance.
- On Linux, `./dev.sh` launches the Avalonia app.
- `./dev-cli.sh` runs the CLI against the isolated Dev channel; never override it to use Production during development.
- `snapcraft pack` builds the strict Production Snap containing the Avalonia app and CLI. Snap artifacts belong under ignored `artifacts/snap/`; do not publish or install them without explicit approval.

## Coding Style & Naming Conventions
- Follow existing C# naming and nullable annotations.
- Use Avalonia's built-in controls and `FluentTheme` for active Desktop work. Follow the official [themes](https://docs.avaloniaui.net/docs/styling/themes) and [styling guidance](https://docs.avaloniaui.net/docs/styling/style-best-practices), existing Avalonia bindings/commands and accessibility patterns. Do not copy WinUI-specific control/resource APIs into Avalonia or add another theme library without approval. See `docs/design-guidelines.md` for scope.
- Keep WinUI frozen. Use its source only as historical and continuity reference; implement active UI work in Avalonia.
- Keep Core, Application and Desktop code cross-platform and Windows-compatible. Shared code must not assume Linux paths, services or process behavior; put OS-specific work behind platform services. Retain the existing Dev-gated Windows continuity implementation as parked reference.
- Keep sync provider code in `Openza.Tasks.Core`; keep UI code out of provider adapters and repositories.
- Keep shared models, storage, sync, import/export, and backup behavior in Core; do not couple Core to either UI framework.
- For syncable tasks, update only local-enhancement fields unless the sync engine explicitly owns the provider field.

## Product Principles
- Keep Openza Tasks flexible, not opinionated. The app may expose factual fields, counts, labels, statuses, dates, sources, and filters, but it should not interpret those facts for the user with judgmental workflow labels such as "needs action".
- Prefer user-defined labels, filters, and saved views over hardcoded concepts such as context, area, energy, person, or review ritual.
- Do not add a separate methodology-specific page or signal when the same outcome can be achieved through transparent data, project selection, labels, filters, and custom views.
- Treat Spaces as user-owned visibility scopes, not provider-specific concepts. Do not hardcode Work/Personal or any user's routing logic in app code; put one-off routing in explicit import/setup scripts or future user-configurable rules.
- Lightweight documented behavior contracts are the source of truth; Avalonia is the implementation reference for ordinary desktop interactions. Follow `docs/behavior-contracts.md` and `docs/architecture.md`; resolve contract discrepancies before changing behavior. WinUI is a useful historical/transition reference, not a requirement to mirror every screen. Web is an online-first companion with shared product semantics, not identical desktop UI or offline guarantees.
- Keep workflow statuses fixed (`none`, `inbox`, `next`, `waiting`, `someday`). Project and workflow status are independent: assigning a project never changes status; choosing a status never changes project. Inbox is the default; None explicitly means no workflow status. The Inbox view shows open Inbox-status tasks without a project. A project task can intentionally remain Inbox-status (unprocessed within that project), and its Inbox badge is correct. This is a decided contract, not an unresolved discrepancy; preserve it during cleanup. Completion is separate from workflow status.
- Linux Snap is the sole current focus until stable. All Windows implementation, packaging, native testing, Store replacement and continuity policy decisions are parked unless the maintainer explicitly asks. Keep `docs/windows-transition.md` and its Dev gate as future reference. Existing Store users stay on the final WinUI release; reconnect and initial sync-off decisions remain unresolved and parked. C# compilation does not prove native package, Microsoft sign-in or pairing continuity.
- The fresh-start rule excludes automatic import of legacy Flutter-era stores. A separately authorized, guarded WinUI-to-Avalonia package transition is a different scope; it does not authorize searching for or importing other old app stores. Continuity remains Dev-only; other channels and installed apps must not be replaced without explicit authorization.
- Package-local SQLite copies are restore points, not durable backups. Durable backup means OneDrive app-folder backup or explicit user export.
- Do not fight MSIX AppData virtualization to place automatic restore points under `%LOCALAPPDATA%\Openza`; use package `LocalState` for restore points.
- Preserve channel identities and version lanes: Dev `Openza.OpenzaTasks.Dev` / `0.0.N.0`, Preview `Openza.OpenzaTasks.Preview` / `0.N.B.0`, Production `Openza.OpenzaTasks` / `1.N.P.0`.

## Testing Guidelines
- Prefer unit tests for repositories, migration, backup/restore, import/export, provider mappers, and sync jobs.
- Build Avalonia directly after `.axaml` changes; library tests alone are not enough. The WinUI project remains frozen.
- Future continuity validation uses the frozen WinUI baseline and Avalonia with equivalent isolated data copies. Never point both at the same writable live database.

## Commit & Pull Request Guidelines
- Follow conventional commits (e.g., `feat: add project filter`, `fix: handle sync timeout`).
- Include release/changelog updates when shipping versions.
- PRs should describe changes, reference issues, and include screenshots for UI changes.

## Security & Configuration
- This is a public open-source repo. Do not commit local SQLite databases, personal task data, provider cache snapshots, screenshots with private tasks, generated certificates, MSIX/AppPackages outputs, Store-private metadata, or machine-specific files.
- Never commit secrets; use `.env.example` for templates and keep `.env.local` gitignored.
- Use Windows Credential Locker in WinUI and Secret Service through `secret-tool` in Avalonia; never store provider credentials as plaintext or log them.
- Before committing, run `gitleaks detect --source . --verbose`.
- Provide SQL migration scripts instead of executing migrations directly in development.

## Agent-Specific Instructions
- Never work on `main`/`master`; always use a feature/fix branch.
- Obsidian is out of scope unless explicitly brought back into scope.
- Do not change WinUI settings or controls; retain the project for the final Store baseline and future continuity validation.
- Treat both Production (`Openza.OpenzaTasks`) and Preview (`Openza.OpenzaTasks.Preview`) as the user's live daily apps. Do not close, stop, build over, install, update, relaunch, or otherwise disrupt either package unless the user explicitly confirms that specific action.
- Treat the installed Linux app and its real data as a live daily driver. Source launches must remain on the built-in Dev channel; use `OPENZA_TASKS_DEV_DATA_DIR` only for disposable Dev isolation, and do not install, upload, or release Snap packages unless explicitly requested.
- Test builds, installs, and launches against the dev package (`Openza.OpenzaTasks.Dev`) first by default. If Preview validation is needed and the app is running or may be in use, stop and ask before proceeding.
- For local Preview MSIX signing, reuse the existing Openza Reader temporary certificate when present: `..\reader\src\Openza.Reader\Openza.Reader_TemporaryKey.pfx`. Do not create a new local signing certificate unless the user explicitly asks.
