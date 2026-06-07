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

Visual Studio Debug/F5 uses the `Openza.OpenzaTasks.Dev` package identity and appears as **Openza Tasks Dev**. This keeps its LocalState, database, and package registration separate from the installed production MSIX.

Release builds keep the production package identity, `Openza.OpenzaTasks`, and appear as **Openza Tasks** unless `PackageChannel=Preview` is set. Preview builds use `Openza.OpenzaTasks.Preview` and appear as **Openza Tasks Preview**.

| Channel | Package identity | Display name | Version lane |
| --- | --- | --- | --- |
| Dev | `Openza.OpenzaTasks.Dev` | Openza Tasks Dev | `0.0.N.0` |
| Preview | `Openza.OpenzaTasks.Preview` | Openza Tasks Preview | `0.N.B.0` |
| Production | `Openza.OpenzaTasks` | Openza Tasks | `1.N.P.0` |

Current Preview version: `0.2.3.0`.

Build Preview with:

```powershell
dotnet build src\Openza.Tasks\Openza.Tasks.csproj -c Release -p:Platform=x64 -p:PackageChannel=Preview --no-restore
```

See `docs/backup-and-release-channels.md` for the backup, restore point, and channel isolation policy.
