# Contributing to Openza Tasks

Thank you for helping with Openza Tasks. The active app is the cross-platform Avalonia desktop app in `src/Openza.Tasks.Desktop`, delivered as a Linux Snap and a Windows MSIX. The WinUI 3 app in `src/Openza.Tasks` is frozen; do not change it. Read [AGENTS.md](AGENTS.md) for the repository rules.

## Development Setup

Install the .NET 10 SDK.

**Linux:**

```bash
cd src/Openza.Tasks.Desktop
dotnet restore
dotnet test ../Openza.Tasks.Tests/Openza.Tasks.Tests.csproj -c Release
dotnet build -c Release --no-restore
cd ../.. && ./dev.sh   # launches the app on the isolated Dev channel
```

**Windows:**

```powershell
cd src/Openza.Tasks.Desktop
dotnet restore ../Openza.Tasks.Tests/Openza.Tasks.Tests.csproj
dotnet test ../Openza.Tasks.Tests/Openza.Tasks.Tests.csproj -c Release --no-restore
dotnet build Openza.Tasks.Desktop.csproj -c Release --no-restore
```

Source launches always use the Dev channel; never point them at Production data.

## Making Changes

1. Create a feature branch.
2. Keep changes scoped and use existing Avalonia/Core patterns.
3. Add tests for data migration, sync, backup, import/export, and provider mapping changes.
4. Run secret scanning before committing:

   ```powershell
   gitleaks detect --source . --verbose
   ```

## Quality Checklist

- The Avalonia app builds directly, not only the core library.
- `.axaml` compiles in Release.
- No certificates, tokens, package outputs, or Store-private data are committed.
- SQLite migrations are backward compatible with legacy Flutter databases.
- Provider tokens go through the credential-store abstraction.
- User-visible errors use Avalonia's built-in controls and existing patterns.

## Style

- C#: nullable enabled, implicit usings enabled, concise records/models where useful.
- UI: Avalonia built-in controls and `FluentTheme`; no WinUI-specific APIs and no additional theme library.
- Keep Core, Application and Desktop code cross-platform and Windows-compatible; put OS-specific behavior behind platform services.
- Sync: keep provider adapters separate from sync orchestration and local data writes.

Questions are welcome through GitHub issues or discussions.
