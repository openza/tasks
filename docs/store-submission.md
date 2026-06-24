# Microsoft Store Submission Notes

Openza Tasks is Store-first for WinUI V1.

## Current Release Target

- Release: `1.0.0`
- Package version: `1.0.0.0`
- Store app name: Openza Tasks
- Public support/contact: `deependra@solanky.dev`
- Privacy policy: use the published `PRIVACY.md` page from this repository or website.

## Package Defaults

- App name: Openza Tasks
- Package identity: Openza.OpenzaTasks
- Architecture: x64
- Target OS: Windows 10 22H2+ and Windows 11
- Restricted capability: `runFullTrust`, required by packaged WinUI desktop apps
- Production version lane: `1.N.P.0`
- Preview and Dev identities must stay separate from Production. Do not install over or relaunch Production/Preview during release validation unless explicitly approved.

## Local Validation

Run these before creating the Store upload package:

```powershell
dotnet restore Openza.Tasks.slnx
dotnet test src\Openza.Tasks.Tests\Openza.Tasks.Tests.csproj -c Release --no-restore
dotnet build src\Openza.Tasks\Openza.Tasks.csproj -c Release -p:Platform=x64 --no-restore
pnpm --dir website build
gitleaks detect --source . --verbose
```

If `pnpm` is not on PATH, use:

```powershell
$env:ASTRO_TELEMETRY_DISABLED='1'; npm --prefix website run build
```

For Store upload package generation, the project disables `.appxsym` creation with
`AppxSymbolPackageEnabled=false`. This avoids MSIX tooling failures on machines
without `mspdbcmf.exe` while still producing the `.msixupload` package Partner
Center accepts.

## Partner Center Handoff

These steps require the maintainer's Microsoft Partner Center account:

1. Reserve or open the **Openza Tasks** product in Partner Center.
2. Associate `src\Openza.Tasks\Openza.Tasks.csproj` with the Store app from Visual Studio so the production manifest receives the Store-assigned identity and publisher values.
3. Confirm the Store identity still maps to the production package lane, not Preview or Dev.
4. Complete pricing, availability, category, age rating, privacy policy URL, support contact, and declarations.
5. Create the Store upload package from Visual Studio's Store packaging flow.
6. Upload the generated `.msixupload` or `.msixbundle` in Partner Center.
7. Submit after Partner Center package validation passes.

Do not commit generated certificates, `.msixupload` files, `.msixbundle` files, or `AppPackages/` output.

## Listing Copy

Short description:

> Native local-first task manager for Windows with optional Todoist, Microsoft To Do, and OneDrive backup support.

Suggested long description:

> Openza Tasks is a Windows-native task manager for fast local capture, project planning, and optional provider sync. Your tasks live locally in SQLite, with package-local restore points for rollback and optional OneDrive app-folder backup for durable recovery. Connect Todoist or Microsoft To Do when you want provider sync, or use Openza Tasks as a local-first planner with Markdown import/export.
>
> V1 is a fresh WinUI release line. It does not auto-migrate older Flutter-era local stores; reconnect integrations or import data explicitly when needed.

Suggested search terms:

- tasks
- todo
- planner
- project
- Todoist
- Microsoft To Do
- OneDrive backup
- local first

## Store Assets

Store-ready screenshots captured from the Preview Demo space:

- 1536x816 originals:
  - `assets\screenshots\main-view-demo.png`
  - `assets\screenshots\add-task-demo.png`
  - `assets\screenshots\search-demo.png`
  - `assets\screenshots\sync-demo.png`
- 1366x768 Partner Center variants in `assets\screenshots\store-1366x768\`:
  - `main-view-store-1366x768.png`
  - `add-task-store-1366x768.png`
  - `search-store-1366x768.png`
  - `sync-store-1366x768.png`

Older screenshots:

- `assets\screenshots\settings.png` is 1505x973 but may expose account state depending on the running app and should be reviewed before Store use.
- `assets\screenshots\main-view.png` is 1502x622 and is not tall enough for Store submission.

Do not use screenshots with personal tasks, real provider data, private account names, tokens, or private project names.

## Release Checklist

- [x] Production manifest exists with `Openza.OpenzaTasks` and version `1.0.0.0`.
- [x] Dev and Preview manifests are separate from Production.
- [x] Package artifacts and certificates are ignored by git.
- [x] Unit tests pass locally for Release.
- [x] WinUI app builds locally for Release/x64.
- [x] Website/docs build passes locally.
- [x] `gitleaks detect --source . --verbose` passes before commit/tag.
- [x] Partner Center product exists and app is associated from Visual Studio.
- [x] Production manifest publisher/identity is updated to Store-assigned values after association.
- [x] Store-ready screenshots are captured from non-private sample data.
- [x] Store package is generated from Visual Studio/MSBuild.
- [ ] Partner Center package validation passes.
- [ ] Submission is reviewed and published.

## Privacy

Use `PRIVACY.md` as the public privacy policy source. The app does not collect telemetry or analytics in V1.
