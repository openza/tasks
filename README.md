# Openza Tasks

**A simple local-first task app that grows with you, with an optional cloud you control.**

Openza Tasks supports fast local capture and completion without requiring projects or a planning method. Avalonia is the desktop implementation reference, with Windows as the major target and Linux supported. The current Windows Store edition uses WinUI 3 and receives necessary maintenance. Windows transition execution is deferred until its [acceptance gates](docs/windows-transition.md) and user decisions are satisfied. Both hosts use the shared SQLite core. macOS is deferred.

Openza Tasks is maintained by Deependra Solanky as a personal open-source project. Microsoft sign-in may show the Openza app name with the `solanky.dev` publisher/contact identity.

User guide: [solanky.dev/openza/tasks](https://solanky.dev/openza/tasks/)

## Features

- **Avalonia desktop app** - active desktop development for Windows and Linux
- **Current Windows Store app** - WinUI 3, MSIX packaging, Mica where available during transition
- **Local-first storage** - tasks, projects, labels, backups, and imports live on your device
- **Provider sync** - optional Todoist and Microsoft To Do reconnect/sync
- **Optional Personal Cloud** - user-owned Cloudflare deployment for sync and an online-first web companion; task data is access-controlled, not end-to-end encrypted, and provider tokens stay on desktop
- **Local restore points** - automatic and manual SQLite restore points support rollback inside the app package state
- **Optional durable backup** - restorable backup snapshots can be uploaded to the app's OneDrive folder or exported by the user
- **Focused task layout** - navigation, projects, task list, and details in one productive surface
- **Markdown import/export** - import GFM checkboxes and export tasks grouped by project
- **Clean local database** - Store V1 starts fresh with planner-ready local data

## Download

Openza Tasks V1 is live on the Microsoft Store:

- [Install from Microsoft Store](https://apps.microsoft.com/detail/9NQGDKXGRGF8)
- [Open in the Store app](ms-windows-store://pdp/?ProductId=9NQGDKXGRGF8)

Openza Tasks 1.1.0 for Linux is available as a strict Snap from the
[GitHub release](https://github.com/openza/tasks/releases/tag/v1.1.0) while
Snap Store publication is pending. Download the `.snap` and matching
`.sha256` asset, verify the download, then install it:

```bash
sha256sum --check openza-tasks_1.1.0_amd64.snap.sha256
sudo snap install ./openza-tasks_1.1.0_amd64.snap --dangerous
```

Snap uses `--dangerous` for a local package installed without a Snap Store
assertion. Openza Tasks remains strictly confined. The release page explains
how to switch to Store-signed automatic updates when they become available.

Legacy Flutter-era packages remain available in older GitHub releases. Store V1 starts with a clean workspace and does not automatically import those stores. A separately authorized, guarded WinUI-to-Avalonia upgrade is a different transition, currently deferred; it does not enable broad legacy imports.

## Building From Source

### Linux desktop development

Install the .NET 10 SDK, then run:

```bash
./dev.sh
```

Source launches always use the isolated maintainer-only Dev directory. For disposable testing, `OPENZA_TASKS_DEV_DATA_DIR` may override that Dev path; Production builds ignore it.

Run the matching Dev CLI with `./dev-cli.sh`. See [docs/cli.md](docs/cli.md) for its command contract.

The Linux app currently includes smart lists, Spaces, projects, search, sorting, filtering, grouping, task details, labels, dates and deadlines, subtasks, Markdown import/export, restore points, connected-task intake, Todoist and Microsoft To Do synchronization, optional OneDrive backup, and GitHub issue creation and linking. The Snap bundles Secret Service tooling and accesses the desktop Secret Portal; source runs require the host `libsecret-tools` package before connecting an account.

Build the strict Linux Snap from the repository root:

```bash
bash packaging/snap/build-local-snap.sh artifacts/snap/openza-tasks.snap
```

The Snap contains both the `openza-tasks` desktop command and the namespaced
`openza-tasks.openza` CLI. The short `openza` command is requested as a Snap
Store automatic alias before public release. Generated packages are written
under the ignored `artifacts/snap/` directory.

Maintainer registration, edge testing, and same-revision promotion are
documented in [docs/snap-release.md](docs/snap-release.md).
Integration configuration and remaining native acceptance checks are documented
in [docs/avalonia-parity.md](docs/avalonia-parity.md).

### Current WinUI Windows host prerequisites

Reference for necessary maintenance only. Windows transition, native validation and packaging work remain deferred.

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
  Openza.Tasks.Desktop/ Avalonia desktop app
  Openza.Tasks.Application/ UI-independent operations and runtime coordination
  Openza.Tasks.Core/   SQLite data, backups, import/export, provider sync
  Openza.Tasks.Tests/  Unit, repository, backup, and sync tests
```

The sync engine is C# in `Openza.Tasks.Core`. Historical Rust FFI work is preserved in git history and legacy releases; it is not shipped with the WinUI app.

## Legacy Flutter App

The last Flutter-era mainline is preserved on the [`legacy-flutter-app`](https://github.com/openza/tasks/tree/legacy-flutter-app) branch. Active desktop development targets Avalonia in `src/Openza.Tasks.Desktop/`; WinUI in `src/Openza.Tasks/` receives necessary maintenance until the transition is accepted.

See [docs/architecture.md](docs/architecture.md) for architecture and the task model, [behavior contracts](docs/behavior-contracts.md) for product semantics, and [docs/TODO.md](docs/TODO.md) for the ordered follow-up work.
See [docs/design-guidelines.md](docs/design-guidelines.md) for the built-in Avalonia Fluent baseline and the separate WinUI maintenance reference.

Microsoft To Do and OneDrive backup use Openza's built-in public Microsoft app registration. Developers can override the public client ID at build time for testing, but normal users do not need to configure Azure app registrations or client IDs.

## Privacy

Openza Tasks does not add telemetry or analytics. Windows uses Credential Locker and a protected MSAL cache. The Linux desktop uses Secret Service, accessed through the Secret Portal in the Snap, for provider tokens, the Microsoft token cache, and backup passphrases. Account identifiers and preferences remain in local settings. Provider sync, GitHub issue creation, and OneDrive backup send data to the selected service when used. See [PRIVACY.md](PRIVACY.md).

## License

The source code and documentation are available under the [MIT License](LICENSE). Openza names, logos, and official app icons are reserved brand assets; see [BRAND.md](BRAND.md). Third-party dependencies remain subject to their respective licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
