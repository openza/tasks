# Openza Tasks

Windows-native local-first task manager built with WinUI 3 and .NET.

## Tech Stack

.NET 10, Windows App SDK 2.0.x, WinUI 3, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, xUnit.

## Essential Commands

```powershell
dotnet restore Openza.Tasks.slnx
dotnet test src\Openza.Tasks.Tests\Openza.Tasks.Tests.csproj -c Release
dotnet build src\Openza.Tasks\Openza.Tasks.csproj -c Release --no-restore
```

## Critical Rules

- Never commit secrets, `.env` files, generated certificates, MSIX packages, or Store-private data.
- Run `gitleaks detect --source . --verbose` before publishing.
- Keep provider tokens behind `ICredentialStore`; do not log token values.
- Keep SQLite migration compatible with the legacy Flutter database at `%APPDATA%\com.openza.tasks\openza_tasks.db`.
- XAML changes must compile through the app project.

## Architecture

```text
src/Openza.Tasks        WinUI app, Windows Credential Locker, app settings
src/Openza.Tasks.Core   Data, migration, backup, Markdown import/export, C# sync
src/Openza.Tasks.Tests  Unit and migration tests
```

## Migration Notes

Flutter/Linux/Rust-FFI builds are legacy. The WinUI app does not ship the old Rust DLL; sync logic now lives in C# so it can later move into an Openza Sync service or CLI.
