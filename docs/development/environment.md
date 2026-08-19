# Development Environment

Openza Tasks targets Windows 10 22H2+ and Windows 11.

## Required

- .NET 10 SDK
- Visual Studio with .NET desktop, UWP, and Windows App SDK C# workloads
- Git

Install the recommended WinUI toolchain:

```powershell
winget configure -f https://aka.ms/winui-config
```

## Verify

```powershell
dotnet restore Openza.Tasks.slnx
dotnet test src\Openza.Tasks.Tests\Openza.Tasks.Tests.csproj -c Release
dotnet build src\Openza.Tasks\Openza.Tasks.csproj -c Release -p:Platform=x64 --no-restore
```

## Debug, Preview, And Production Channels

Preview and Dev are maintainer-only channels. Production is the only public channel.

Visual Studio Debug/F5 uses the `Openza.OpenzaTasks.Dev` package identity and appears as **Openza Tasks Dev**. This keeps its LocalState, database, and package registration separate from the installed production MSIX.

Release builds keep the production package identity, `Openza.OpenzaTasks`, and appear as **Openza Tasks** unless `PackageChannel=Preview` is set. Preview builds use `Openza.OpenzaTasks.Preview` and appear as **Openza Tasks Preview**.

| Channel | Package identity | Display name | Version lane |
| --- | --- | --- | --- |
| Dev | `Openza.OpenzaTasks.Dev` | Openza Tasks Dev | `0.0.N.0` |
| Preview | `Openza.OpenzaTasks.Preview` | Openza Tasks Preview | `0.N.B.0` |
| Production | `Openza.OpenzaTasks` | Openza Tasks | `1.N.P.0` |

Current Preview version: `0.2.3.0`.

### Linux channel isolation

Linux hosts use independent data, settings, restore points, locks, and Secret Service namespaces:

| Channel | Data directory | Launch policy |
| --- | --- | --- |
| Production (Snap) | `$SNAP_USER_COMMON/tasks` | Published Snap only |
| Production (unconfined legacy build) | `~/.local/share/Openza/Tasks` | No longer a public Linux package lane |
| Preview | `~/.local/share/Openza/Tasks Preview` | Explicit maintainer build |
| Dev | `~/.local/share/Openza/Tasks Dev` | `./dev.sh` and `./dev-cli.sh` |

Source runs default to Dev. `OPENZA_TASKS_DEV_DATA_DIR` may override only the Dev directory for isolated tests. There is intentionally no runtime channel selector. Passing `-p:OpenzaChannel=Production` or `Preview` to an ordinary source build is rejected. Non-Dev channels are selected only when the packaging scripts publish the dedicated `*.Package.csproj` entry projects with a fixed packaging profile. The normal app projects reject packaging profiles even if internal MSBuild properties are supplied on the command line. Runtime also requires the matching `.openza-channel` marker copied only to genuine publish output; a packaging-project build has Production/Preview metadata but still runs as Dev without that marker. Packaging profiles use separate build-output directories so a later source run cannot reuse a published Production or Preview assembly.

Linux packaging is Snap-only. The strict Production `openza-tasks` Snap contains
the Avalonia desktop app and CLI, and both use `$SNAP_USER_COMMON/tasks` so data,
settings, and restore points survive automatic Snap refreshes. Both hosts derive
the same coordination locks from that shared data path. Provider credentials use
the desktop Secret Portal and remain isolated by the Snap security domain.
Source runs and maintainer Preview/Dev environments remain outside the Snap and
retain their existing channel isolation.

The Snap initially supports amd64. Its CLI is available as
`openza-tasks.openza`; request the `openza` automatic alias from the Snap Store
before public release. There is no runtime channel selector and no DEB, RPM, or
AppImage public package lane.

To create a sync-disabled, independent Dev or Preview snapshot from the current Production database:

```bash
./scripts/seed-linux-data.sh dev
./scripts/seed-linux-data.sh preview
```

Add `--replace` only when intentionally replacing existing channel data; the script then requires typing the target channel name. It never copies Secret Service credentials and never writes to Production.

Build Preview with:

```powershell
dotnet build src\Openza.Tasks\Openza.Tasks.csproj -c Release -p:Platform=x64 -p:PackageChannel=Preview --no-restore
```

See `docs/backup-and-release-channels.md` for the backup, restore point, and channel isolation policy.
