# Openza Tasks

**A simple local-first task app that grows with you, with an optional cloud you control (preview).**

Openza Tasks supports fast local capture and completion without requiring projects or a planning method. Avalonia is the cross-platform desktop host; Linux Snap and Windows (the Avalonia MSIX replacing the WinUI Store app) are both active development and delivery targets. The WinUI 3 edition is frozen: its last Microsoft Store release is final, with no further releases or maintenance. Existing Store users remain on that release until the Avalonia replacement passes the [Windows continuity requirements](docs/windows-transition.md); installing and submitting the Avalonia Store release always need the maintainer's explicit approval. Both projects retain the shared SQLite core. macOS is deferred.

Openza Tasks is maintained by Deependra Solanky as a personal open-source project. Microsoft sign-in may show the Openza app name with the `solanky.dev` publisher/contact identity.

User guide: [openza.org/tasks](https://openza.org/tasks/)

## Features

- **Avalonia desktop app** - cross-platform code, with Linux Snap and Windows MSIX both under active development
- **Existing Windows Store app** - final frozen WinUI 3 release, retained for current users until the Avalonia replacement
- **Local-first storage** - tasks, projects, labels, backups, and imports live on your device
- **Provider sync** - optional Todoist and Microsoft To Do reconnect/sync
- **Optional Personal Cloud (preview)** - user-owned Cloudflare deployment for sync and an online-first web companion, with daily-quota protection; task data is access-controlled, not end-to-end encrypted, and provider tokens stay on desktop.
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

Use `stable` for normal installations. The
[Snap Store page](https://snapcraft.io/openza-tasks) shows the current version of
each channel; check it before choosing a different channel:

```bash
snap info openza-tasks
```

**Personal Cloud remains an optional preview feature; that does not require the
Snap `edge` channel.** Desktop releases
support setup and updates from separately published, signed
[Personal Cloud releases](https://github.com/openza/personal-cloud/releases).
Start in desktop Settings → Personal Cloud. Feature maturity, the Snap release
channel, and the version deployed in your own Cloudflare account are separate.

See the [release plan](docs/snap-release.md). Existing local sideload users should
read the [Store adoption plan](docs/snap-release.md#sideload-to-store-transition)
before changing their installation.

Each [GitHub release](https://github.com/openza/tasks/releases)
provides a local-file fallback. Download its `.snap` and matching
`.sha256` asset, verify the download, then install only if that fallback is wanted:

```bash
sha256sum --check openza-tasks_<version>_amd64.snap.sha256
sudo snap install ./openza-tasks_<version>_amd64.snap --dangerous
```

Snap uses `--dangerous` for a local package installed without a Snap Store
assertion. Openza Tasks remains strictly confined. A local sideload does not
automatically track Store updates; use the documented sideload-to-Store process.

Legacy Flutter-era packages remain available in older GitHub releases. Store V1 starts with a clean workspace and does not automatically import those stores. Guarded WinUI-to-Avalonia continuity stays behind its Dev gate. Keep legacy imports excluded; Windows replacement follows the transition requirements.

## Building From Source

### Linux desktop development

Install the .NET 10 SDK, then run:

```bash
./dev.sh
```

Source launches always use the isolated maintainer-only Dev directory. For disposable testing, `OPENZA_TASKS_DEV_DATA_DIR` may override that Dev path; Production builds ignore it.

Run the matching Dev CLI with `./dev-cli.sh`. See [docs/cli.md](docs/cli.md) for its command contract.

The Linux app currently includes smart lists, Spaces, projects, search, sorting, filtering, grouping, task details, labels, dates and deadlines, subtasks, Markdown import/export, restore points, connected-task intake, Todoist and Microsoft To Do synchronization, optional OneDrive backup, and GitHub issue creation and linking. The Snap bundles Secret Service tooling and uses the Secret portal on desktops that provide it. Otherwise, if `password-manager-service` is not connected, grant keyring access with `sudo snap connect openza-tasks:password-manager-service` and try again. A running keyring (such as GNOME Keyring or KWallet) is required; source runs also require the host `libsecret-tools` package before connecting an account. See [Snap credential guidance](docs/snap-release.md#desktop-keyring-access).

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

### Windows Avalonia

Windows is an active target again. Packaging, MSIX, native testing and Store
replacement proceed under the [Windows continuity requirements](docs/windows-transition.md);
installing and submitting a Store release always need explicit
maintainer approval. The following commands validate the source:

```powershell
cd src/Openza.Tasks.Desktop
dotnet restore ../Openza.Tasks.Tests/Openza.Tasks.Tests.csproj
dotnet test ../Openza.Tasks.Tests/Openza.Tasks.Tests.csproj -c Release --no-restore
dotnet build Openza.Tasks.Desktop.csproj -c Release --no-restore
```

CI requires Linux build/tests and Snap validation. The optional Windows job builds
Avalonia and runs smoke tests on code PRs and pushes to main; it runs the full suite
weekly and on manual dispatch. It is nonblocking and does not build WinUI. Retain
the existing continuity Dev gate and the package/vault/sign-in
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

The last Flutter-era mainline is preserved on the [`legacy-flutter-app`](https://github.com/openza/tasks/tree/legacy-flutter-app) branch. Current development targets Avalonia for Linux Snap and Windows MSIX in `src/Openza.Tasks.Desktop/`, keeping shared code cross-platform. The frozen WinUI project in `src/Openza.Tasks/` remains as the final Store baseline and continuity reference.

See [docs/architecture.md](docs/architecture.md) for architecture and the task model, [behavior contracts](docs/behavior-contracts.md) for product semantics, and [docs/TODO.md](docs/TODO.md) for the ordered follow-up work.
See [docs/design-guidelines.md](docs/design-guidelines.md) for the built-in Avalonia Fluent baseline and historical WinUI design reference.

Microsoft To Do and OneDrive backup use Openza's built-in public Microsoft app registration. Developers can override the public client ID at build time for testing, but normal users do not need to configure Azure app registrations or client IDs.

## Privacy

Openza Tasks does not add telemetry or analytics. Windows uses Credential Locker and a protected MSAL cache. The Linux desktop uses libsecret for provider tokens, the Microsoft token cache, and backup passphrases. The Snap prefers the Secret portal where available and otherwise needs the manual keyring connection described above. Account identifiers and preferences remain in local settings. Provider sync, GitHub issue creation, and OneDrive backup send data to the selected service when used. See [PRIVACY.md](PRIVACY.md).

## License

The source code and documentation are available under the [MIT License](LICENSE). Openza names, logos, and official app icons are reserved brand assets; see [BRAND.md](BRAND.md). Third-party dependencies remain subject to their respective licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
