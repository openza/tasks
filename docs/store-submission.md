# Microsoft Store Submission — Avalonia

## Release and identity

The existing Store app is the final frozen WinUI release, package family
`Openza.OpenzaTasks_q7pgk2crrc53t`. The Avalonia Production package replaces it
under the same identity; WinUI stays frozen. The current Store version is on the
[Store page](https://apps.microsoft.com/detail/9NQGDKXGRGF8); each release is
recorded in the changelog and GitHub Releases.

- Product: [Openza Tasks, 9NQGDKXGRGF8](https://apps.microsoft.com/detail/9NQGDKXGRGF8)
- Name: `Openza.OpenzaTasks`
- Publisher: `CN=DAC3BFD1-0B27-4912-85AF-DB96E18108D1`
- Application Id: `App`; architecture: x64; Windows 10 22H2+ / Windows 11
- Manifest: `packaging/windows/Package.Production.appxmanifest`
- Self-contained .NET and Windows App SDK; capabilities: internetClient, runFullTrust.

## Build the upload package

On Windows with .NET 10 and Windows SDK MakeAppx on PATH:

```powershell
Push-Location src\Openza.Tasks.Desktop
dotnet build Openza.Tasks.Desktop.csproj -c Release
Pop-Location
dotnet test src\Openza.Tasks.Tests\Openza.Tasks.Tests.csproj -c Release
./scripts/windows/package-msix.ps1 -Channel Production
gitleaks detect --source . --verbose --redact
```

The script publishes the dedicated Desktop Package entry project with
`OpenzaPackagingProfile=Production`, `OpenzaChannel=Production` and
`OpenzaWindowsContinuity=Production`. The profile embeds matching metadata and
publishes the `.openza-channel` marker containing `Production`. A missing or
incorrect marker stops packaging. This is not an ordinary source launch.

The printed `MSIX:` path under ignored `artifacts/msix/<id>/` is the unsigned
`.msix` upload candidate. Partner Center accepts MSIX and applies Store signing;
this script does not generate an msixupload container. Optional local signing
uses `-CertificatePath` and a SecureString `-CertificatePassword`, plus SignTool.
The certificate must match the manifest publisher. Never commit certificates,
passwords, packages, private data or private Store metadata.

Inspect the package before handing it off:

```powershell
makeappx unpack /p <printed-msix-path> /d artifacts/msix/production-inspection /o
Get-Content artifacts/msix/production-inspection/AppxManifest.xml
Get-Content artifacts/msix/production-inspection/.openza-channel
```

Confirm identity/publisher/version, App Id, Avalonia executable and Production
marker. Do not install, replace, stop or relaunch live Production/Preview for
validation without specific approval. The script never installs or launches.

## What upgrading users see

The same package family retains its package-local database and settings.
Continuity uses only the matching package LocalFolder, archives originals and a
consistent SQLite safety copy, and converts supported settings into
`settings.avalonia.json`. It does not search for Flutter-era stores.
Automatic sync and OneDrive upload start **off**. Users should review connections
and reconnect providers as requested before enabling them. Unscoped legacy
Todoist/GitHub/backup-passphrase credentials are not automatically copied.
Microsoft cache reuse is attempted for the configured account/client; unusable
caches require sign-in. Originals are retained. Local safety copies are restore
points, not durable backups; use explicit export or OneDrive backup.

## Partner Center handoff

1. Open the existing Openza Tasks product in Partner Center, not a new app.
2. Upload the generated unsigned Production MSIX to the new submission.
3. Verify package validation, listing, privacy URL, declarations and availability.
4. Use reviewed Avalonia screenshots with synthetic data only.
5. Submit only with maintainer approval after native upgrade acceptance.

Building, unpacking and unit tests do not prove installed upgrade behavior,
Microsoft sign-in, real vault access or provider/Personal Cloud continuity.
See [Windows transition](windows-transition.md) for those acceptance checks.
