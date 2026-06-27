# Openza Tasks

**Windows Native. Local First. Open Source.**

Openza Tasks is a Windows-native task manager for people who want fast local task capture with optional provider sync. The active app is built with WinUI 3 and stores data locally in SQLite.

Openza Tasks is maintained by Deependra Solanky as a personal open-source project. Microsoft sign-in may show the Openza app name with the `solanky.dev` publisher/contact identity.

## Features

- **Native Windows app** - WinUI 3, MSIX packaging, Mica where available
- **Local-first storage** - tasks, projects, labels, backups, and imports live on your device
- **Provider sync** - optional Todoist and Microsoft To Do reconnect/sync
- **Local restore points** - automatic and manual SQLite restore points support rollback inside the app package state
- **Optional durable backup** - restorable backup snapshots can be uploaded to the app's OneDrive folder or exported by the user
- **Focused task layout** - navigation, projects, task list, and details in one productive surface
- **Markdown import/export** - import GFM checkboxes and export tasks grouped by project
- **Clean local database** - Store V1 starts fresh with planner-ready local data

## Download

Openza Tasks V1 is live on the Microsoft Store:

- [Install from Microsoft Store](https://apps.microsoft.com/detail/9NQGDKXGRGF8)
- [Open in the Store app](ms-windows-store://pdp/?ProductId=9NQGDKXGRGF8)

Legacy Flutter/Linux packages remain available in older GitHub releases, but they are no longer the active product line. Store V1 starts with a clean WinUI workspace and can reconnect integrations.

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
dotnet build src\Openza.Tasks\Openza.Tasks.csproj -c Release -p:Platform=x64 --no-restore
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
  Openza.Tasks.Core/   SQLite data, backups, import/export, provider sync
  Openza.Tasks.Tests/  Unit, repository, backup, and sync tests
```

The sync engine is C# in `Openza.Tasks.Core`. Historical Rust FFI work is preserved in git history and legacy releases; it is not shipped with the WinUI app.

## Legacy Flutter App

The last Flutter-era mainline is preserved on the [`legacy-flutter-app`](https://github.com/openza/tasks/tree/legacy-flutter-app) branch. New development, issues, and releases target the Windows-native WinUI codebase in `src/Openza.Tasks/`.

See [docs/architecture.md](docs/architecture.md) for the V3 task model, provider wrapper pattern, and future sync-route design.
See [docs/design-guidelines.md](docs/design-guidelines.md) for the Openza Calm Fluent production UI language.

Microsoft To Do and OneDrive backup use Openza's built-in public Microsoft app registration. Developers can override the public client ID at build time for testing, but normal users do not need to configure Azure app registrations or client IDs.

## Privacy

Openza Tasks does not add telemetry or analytics. Todoist tokens and OneDrive backup passphrases are stored locally using Windows Credential Locker. Microsoft sign-in uses the local MSAL cache encrypted for the current Windows user. See [PRIVACY.md](PRIVACY.md).

## License

MIT License - see [LICENSE](LICENSE).
