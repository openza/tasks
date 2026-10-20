# Openza Tasks

**A simple local-first task app that grows with you, with an optional cloud you control.**

Openza Tasks supports fast local capture and completion without requiring projects or a planning method. Avalonia is the cross-platform desktop host, with Linux Snap as the sole current focus until Linux is stable. All Windows work is parked; shared code remains Windows-compatible. The WinUI 3 edition is frozen: its last Microsoft Store release is final, with no further releases or maintenance. Existing Store users remain on that release until a future Avalonia replacement passes the [Windows continuity requirements](docs/windows-transition.md). Both projects retain the shared SQLite core. macOS is deferred.

Openza Tasks is maintained by Deependra Solanky as a personal open-source project. Microsoft sign-in may show the Openza app name with the `solanky.dev` publisher/contact identity.

User guide: [solanky.dev/openza/tasks](https://solanky.dev/openza/tasks/)

## Features

- **Avalonia desktop app** - cross-platform code with current development focused on Linux Snap; Windows work is parked
- **Existing Windows Store app** - final frozen WinUI 3 release, retained for current users until a future Avalonia replacement
- **Local-first storage** - tasks, projects, labels, backups, and imports live on your device
- **Provider sync** - optional Todoist and Microsoft To Do reconnect/sync
- **Optional Personal Cloud (unreleased development)** - user-owned Cloudflare deployment for sync and an online-first web companion; task data is access-controlled, not end-to-end encrypted, and provider tokens stay on desktop. This is not included in public version 1.1.0.
- **Local restore points** - automatic and manual SQLite restore points support rollback inside the app package state
- **Optional durable backup** - restorable backup snapshots can be uploaded to the app's OneDrive folder or exported by the user
- **Focused task layout** - navigation, projects, task list, and details in one productive surface
- **Markdown import/export** - import GFM checkboxes and export tasks grouped by project
- **Clean local database** - Store V1 starts fresh with planner-ready local data

## Download

Openza Tasks V1 is live on the Microsoft Store:

- [Install from Microsoft Store](https://apps.microsoft.com/detail/9NQGDKXGRGF8)
- [Open in the Store app](ms-windows-store://pdp/?ProductId=9NQGDKXGRGF8)

The primary Linux download is the strict Snap from the
[Snap Store](https://snapcraft.io/openza-tasks):

```bash
sudo snap install openza-tasks
```

The public Store version is **1.1.0**, without Personal Cloud.
It does not contain the unreleased quota/compatibility candidate in this branch.
[Release provenance and the proposed 1.2.0 plan](docs/snap-release.md) distinguish
the public package from the source candidate. Existing local sideload users should
read the [Store adoption plan](docs/snap-release.md#sideload-to-store-transition)
before changing their installation.

The [GitHub v1.1.0 release](https://github.com/openza/tasks/releases/tag/v1.1.0)
remains an explicit local-file fallback. Download its `.snap` and matching
`.sha256` asset, verify the download, then install only if that fallback is wanted:

```bash
sha256sum --check openza-tasks_1.1.0_amd64.snap.sha256
sudo snap install ./openza-tasks_1.1.0_amd64.snap --dangerous
```

Snap uses `--dangerous` for a local package installed without a Snap Store
assertion. Openza Tasks remains strictly confined. A local sideload does not
automatically track Store updates; use the documented sideload-to-Store process.

Legacy Flutter-era packages remain available in older GitHub releases. Store V1 starts with a clean workspace and does not automatically import those stores. Guarded WinUI-to-Avalonia continuity is retained as parked reference behind a Dev gate. Keep legacy imports excluded; Windows replacement is future work under the transition requirements.

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

### Parked Windows Avalonia reference

All Windows work, including packaging, MSIX, native testing, Store replacement and
continuity policy decisions, is parked until Linux is stable. Resume only on an
explicit maintainer request. The following commands are retained as future source
validation reference, not a current Windows task:

```powershell
cd src/Openza.Tasks.Desktop
dotnet restore ../Openza.Tasks.Tests/Openza.Tasks.Tests.csproj
dotnet test ../Openza.Tasks.Tests/Openza.Tasks.Tests.csproj -c Release --no-restore
dotnet build Openza.Tasks.Desktop.csproj -c Release --no-restore
```

CI requires Linux build/tests and Snap validation. The optional Windows Avalonia
build/shared-suite job is a nonblocking early warning only; do not block Linux work
on it or act on its results unless the maintainer explicitly asks. It does not build
WinUI. Retain the existing continuity Dev gate and future package/vault/sign-in
acceptance requirements. Keep Core, Application and Desktop code cross-platform,
with OS-specific behavior behind platform services.

## Architecture

```text
src/
  Openza.Tasks/        Frozen final WinUI Store host and continuity reference
  Openza.Tasks.Desktop/ Avalonia desktop app
  Openza.Tasks.Application/ UI-independent operations and runtime coordination
  Openza.Tasks.Core/   SQLite data, backups, import/export, provider sync
  Openza.Tasks.Tests/  Unit, repository, backup, and sync tests
```

The sync engine is C# in `Openza.Tasks.Core`. Historical Rust FFI work is preserved in git history and legacy releases; it is not shipped with the WinUI app.

## Legacy Flutter App

The last Flutter-era mainline is preserved on the [`legacy-flutter-app`](https://github.com/openza/tasks/tree/legacy-flutter-app) branch. Current development targets Avalonia for Linux Snap in `src/Openza.Tasks.Desktop/`, while keeping shared code cross-platform; all Windows work is parked. The frozen WinUI project in `src/Openza.Tasks/` remains as the final Store baseline and continuity reference.

See [docs/architecture.md](docs/architecture.md) for architecture and the task model, [behavior contracts](docs/behavior-contracts.md) for product semantics, and [docs/TODO.md](docs/TODO.md) for the ordered follow-up work.
See [docs/design-guidelines.md](docs/design-guidelines.md) for the built-in Avalonia Fluent baseline and historical WinUI design reference.

Microsoft To Do and OneDrive backup use Openza's built-in public Microsoft app registration. Developers can override the public client ID at build time for testing, but normal users do not need to configure Azure app registrations or client IDs.

## Privacy

Openza Tasks does not add telemetry or analytics. Windows uses Credential Locker and a protected MSAL cache. The Linux desktop uses Secret Service, accessed through the Secret Portal in the Snap, for provider tokens, the Microsoft token cache, and backup passphrases. Account identifiers and preferences remain in local settings. Provider sync, GitHub issue creation, and OneDrive backup send data to the selected service when used. See [PRIVACY.md](PRIVACY.md).

## License

The source code and documentation are available under the [MIT License](LICENSE). Openza names, logos, and official app icons are reserved brand assets; see [BRAND.md](BRAND.md). Third-party dependencies remain subject to their respective licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
