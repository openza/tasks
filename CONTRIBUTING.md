# Contributing to Openza Tasks

Thank you for helping with Openza Tasks. The active app is a Windows-native WinUI 3 application.

## Development Setup

1. Install the Windows/WinUI toolchain:

   ```powershell
   winget configure -f https://aka.ms/winui-config
   ```

2. Restore, test, and build:

   ```powershell
   dotnet restore Openza.Tasks.slnx
   dotnet test src\Openza.Tasks.Tests\Openza.Tasks.Tests.csproj -c Release
   dotnet build src\Openza.Tasks\Openza.Tasks.csproj -c Release --no-restore
   ```

3. Run from Visual Studio using the `Openza Tasks` MSIX launch profile with `x64` selected and deploy enabled.

## Making Changes

1. Create a feature branch.
2. Keep changes scoped and use existing WinUI/Core patterns.
3. Add tests for data migration, sync, backup, import/export, and provider mapping changes.
4. Run secret scanning before committing:

   ```powershell
   gitleaks detect --source . --verbose
   ```

## Quality Checklist

- WinUI app builds directly, not only the core library.
- XAML compiles in Release.
- No certificates, tokens, package outputs, or Store-private data are committed.
- SQLite migrations are backward compatible with legacy Flutter databases.
- Provider tokens go through the credential-store abstraction.
- User-visible errors use native WinUI surfaces such as InfoBar or ContentDialog.

## Style

- C#: nullable enabled, implicit usings enabled, concise records/models where useful.
- UI: native WinUI controls and Fluent spacing; avoid custom-looking web-style widgets.
- Sync: keep provider adapters separate from sync orchestration and local data writes.

Questions are welcome through GitHub issues or discussions.
