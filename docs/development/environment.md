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
dotnet build src\Openza.Tasks\Openza.Tasks.csproj -c Release --no-restore
```

## Debug And Daily Testing

Visual Studio Debug/F5 uses the `Openza.OpenzaTasks.Dev` package identity and appears as **Openza Tasks Dev**. This keeps its LocalState, database, and package registration separate from the daily installed MSIX.

Release builds keep the production package identity, `Openza.OpenzaTasks`, and appear as **Openza Tasks**. Use Release/MSIX for daily workflow testing when you want behavior close to the Store build.
