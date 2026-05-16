# Openza Tasks

**Windows Native. Local First. Open Source.**

Openza Tasks is a Windows-native task manager for people who want fast local task capture with optional provider sync. The active app is built with WinUI 3 and stores data locally in SQLite.

## Features

- **Native Windows app** - WinUI 3, MSIX packaging, Mica where available
- **Local-first storage** - tasks, projects, labels, backups, and imports live on your device
- **Provider sync** - optional Todoist and Microsoft To Do reconnect/sync
- **Focused task layout** - navigation, projects, task list, and details in one productive surface
- **Markdown import/export** - import GFM checkboxes and export tasks grouped by project
- **Clean local database** - Store V1 starts fresh with planner-ready local data

## Download

V1 is Store-first. Until the Microsoft Store listing is public, install and test from Visual Studio or an MSIX package created locally.

Legacy Flutter/Linux packages remain available in older GitHub releases, but they are no longer the active product line. Store V1 does not auto-migrate legacy Flutter databases; users start with a clean WinUI workspace and can reconnect integrations.

## Building From Source

### Prerequisites

- Windows 10 22H2 or Windows 11
- .NET 10 SDK
- Visual Studio 2026 Community or newer with:
  - .NET desktop development
  - Universal Windows Platform development
  - Windows App SDK C# components

Microsoft's WinUI setup can install the required workloads:

```powershell
winget configure -f https://aka.ms/winui-config
```

### Build And Test

```powershell
dotnet restore Openza.Tasks.slnx
dotnet test src\Openza.Tasks.Tests\Openza.Tasks.Tests.csproj -c Release
dotnet build src\Openza.Tasks\Openza.Tasks.csproj -c Release --no-restore
```

### Run In Visual Studio

1. Open `Openza.Tasks.slnx`.
2. Select the `Openza Tasks` launch profile.
3. Use `x64`.
4. In Configuration Manager, enable **Build** and **Deploy** for `Openza.Tasks`.
5. Press F5.

## Architecture

```text
src/
  Openza.Tasks/        WinUI 3 packaged app
  Openza.Tasks.Core/   SQLite data, migration, import/export, provider sync
  Openza.Tasks.Tests/  Unit and migration tests
```

The sync engine is C# in `Openza.Tasks.Core`. The previous Rust FFI engine is not shipped with the WinUI app.

See [docs/architecture.md](docs/architecture.md) for the V3 task model, provider wrapper pattern, and future sync-route design.
See [docs/design-guidelines.md](docs/design-guidelines.md) for the Openza Calm Fluent production UI language.

For Microsoft To Do source builds, provide a public Azure app registration client ID via `OPENZA_TASKS_MSTODO_CLIENT_ID`, the Settings page, or `-p:MicrosoftToDoClientId=...` during build. The Azure app registration must allow public client flows and include the WAM redirect URI `ms-appx-web://microsoft.aad.brokerplugin/{client_id}`. Do not commit client secrets.

## Privacy

Openza Tasks does not add telemetry or analytics. Provider tokens are stored locally using Windows Credential Locker. See [PRIVACY.md](PRIVACY.md).

## License

MIT License - see [LICENSE](LICENSE).
