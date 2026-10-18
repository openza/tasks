# Openza Tasks

Local-first task manager. Avalonia is the implementation reference for active desktop development, with Windows the major target and Linux supported. WinUI remains the current Store host and receives necessary maintenance until deliberate retirement.

`AGENTS.md` contains the authoritative repository instructions. Lightweight [behavior contracts](docs/behavior-contracts.md) are the product source of truth; ordinary desktop interactions use Avalonia as the reference. The online-first web companion shares semantics without requiring identical screens or desktop offline guarantees.

## Tech Stack

.NET 10, Avalonia 12, Windows App SDK 2.0.x, WinUI 3, Microsoft.Data.Sqlite, xUnit.

## Essential Commands

```powershell
dotnet restore Openza.Tasks.slnx
dotnet test src\Openza.Tasks.Tests\Openza.Tasks.Tests.csproj -c Release
dotnet build src\Openza.Tasks\Openza.Tasks.csproj -c Release --no-restore
```

```bash
./dev.sh
```

## Critical Rules

- Never commit secrets, `.env` files, generated certificates, MSIX packages, or Store-private data.
- Run `gitleaks detect --source . --verbose` before publishing.
- Keep provider tokens behind `ICredentialStore`; do not log token values.
- Store V1 is a fresh-start WinUI app. Do not auto-migrate legacy Flutter databases; consider explicit import tooling later if the product decision changes.
- XAML changes must compile through the app project.

## Architecture

```text
src/Openza.Tasks        WinUI app, Windows Credential Locker, app settings
src/Openza.Tasks.Desktop Avalonia implementation reference and desktop services
src/Openza.Tasks.Application UI-independent operations and runtime coordination
src/Openza.Tasks.Core   Data, migration, backup, Markdown import/export, C# sync
src/Openza.Tasks.Tests  Unit and migration tests
```

## Migration Notes

Follow [the Windows transition plan](docs/windows-transition.md) before replacing the Store host. Preserve channel identities and live-app safeguards in `AGENTS.md`. Current Avalonia/Web Windows evidence does not prove MSIX upgrade, settings, credentials or pairing continuity. Statuses remain fixed; Inbox means newly captured, not yet organized, independently of completion.

Flutter/Rust-FFI builds are legacy. The WinUI app does not ship the old Rust DLL; sync logic now lives in C# so it can later move into an Openza Sync service or CLI.
