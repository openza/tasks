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
| Production | `~/.local/share/Openza/Tasks` | Published packages only |
| Preview | `~/.local/share/Openza/Tasks Preview` | Explicit maintainer build |
| Dev | `~/.local/share/Openza/Tasks Dev` | `./dev.sh` and `./dev-cli.sh` |

Source runs default to Dev. `OPENZA_TASKS_DEV_DATA_DIR` may override only the Dev directory for isolated tests. There is intentionally no runtime channel selector. Passing `-p:OpenzaChannel=Production` or `Preview` to an ordinary source build is rejected. Non-Dev channels are selected only when the packaging scripts publish the dedicated `*.Package.csproj` entry projects with a fixed packaging profile. The normal app projects reject packaging profiles even if internal MSBuild properties are supplied on the command line. Runtime also requires the matching `.openza-channel` marker copied only to genuine publish output; a packaging-project build has Production/Preview metadata but still runs as Dev without that marker. Packaging profiles use separate build-output directories so a later source run cannot reuse a published Production or Preview assembly.

Linux packaging defaults to Production. Maintainers can explicitly select Preview by setting `OPENZA_PACKAGE_CHANNEL=Preview` when invoking a packaging script. Do not use that setting for ordinary source runs.

Preview Linux packages are independently named and install alongside Production: Debian package `openza-tasks-preview`, app launcher `openza-tasks-preview`, CLI launcher `openza-preview`, desktop ID `com.openza.Tasks.Preview`, and display name **Openza Tasks Preview**. Preview AppImages use the `Openza_Tasks_Preview` artifact and AppDir names. Production retains `openza-tasks`, `openza`, `com.openza.Tasks`, and `Openza_Tasks`.

The DEB packages the desktop app and the host CLI together. The GUI AppImage is standalone and does not install or advertise a host `openza` command; use the DEB when both commands are required.

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
