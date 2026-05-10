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
