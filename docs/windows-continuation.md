# Windows continuation: Personal Cloud and task workspace

The Windows shared-task continuation now supports Cloud schema 25, authoritative
task modification times, stable Spaces selection/navigation, and the desktop-core
product boundary. Web shares task workflows and styling; third-party accounts,
intake and routing belong to Desktop. The companion repository's
`docs/local-parity-validation.md` describes a disposable Worker/SQLite round-trip
fixture, including the opt-in `PersonalCloudSharedWorkerAcceptanceTests`.
The earlier handoff below records the pre-validation snapshot. Release artifact
pinning and production deployment are separate from local candidate validation.

Continue Tasks on `feat/personal-cloud-initial-sync-progress` and
[Personal Cloud](https://github.com/openza/personal-cloud) on
`feat/web-desktop-functional-parity`. Use repository-specific managed worktrees,
preserve existing changes, and keep primary checkouts on their default branch.
Read both repositories' current agent instructions. Record `git rev-parse HEAD`
and `git status --short --branch` in each worktree before continuing; branch
names alone do not identify a reproducible snapshot.

## What is ready, and what needs Windows validation

Core and Application changes cover initial-sync progress, immutable mutation
replay/batching, workspace metadata and rich tasks, provider catalogs/scopes,
ownership checks, and bounded paged bootstrap. Avalonia includes the initial-sync
presentation and workspace/provider-intent integration. Core migrations
`0010_personal_cloud_initial_sync.sql` and `0011_personal_cloud_workspace.sql`
are embedded in the source; normal initialization applies them to the selected
local database. Preserve a consistent backup before opening an older Dev store.

WinUI has limited recurrence-property compatibility changes in its existing
task list, details and shell. **Native Windows compilation and WinUI UI parity
have not been verified.** The Avalonia initial-upload UI and newer workflows
must not be assumed to exist in WinUI merely because both use Core. Inspect and
validate them on Windows before implementing any remaining native UI work.

The reviewed Linux source snapshot passed 688 tests, with seven explicit
Worker/browser opt-in skips, plus a direct Avalonia Release build. Additional
local synthetic paging and UI checks passed. These results do not establish a
Windows build, packaged acceptance, real-provider acceptance, or end-to-end
parity between WinUI, Avalonia and Web.

## Windows prerequisites and isolated Dev identity

The repository README requires Windows 10 22H2 or Windows 11, the .NET 10 SDK,
and Visual Studio 2026 Community or newer with .NET desktop development, UWP
development, and Windows App SDK C# components. Git and Node.js 22/npm are also
needed for the companion source and artifact verification. `global.json` selects SDK
10.0.110 with latest-feature roll-forward. The WinUI project targets
`net10.0-windows10.0.26100.0`, minimum Windows build 19045, with an x64 runtime.
The README's official workload setup command is:

```powershell
winget configure -f https://aka.ms/winui-config
```

Prerequisite installation is human-run. Do not install tools, enable sideloading,
change signing credentials, or bypass platform security without the applicable
authorization. Reuse an existing approved local signing certificate if signing
is required; do not generate a replacement automatically.

For native development, use **Debug**, **x64**, and the actual launch profile
**Openza Tasks Dev** from `src/Openza.Tasks/Properties/launchSettings.json`.
Debug selects `Package.Dev.appxmanifest`, identity `Openza.OpenzaTasks.Dev`.
WinUI stores data in that package's own `ApplicationData.Current.LocalFolder`;
verify the Dev identity before allowing initialization. Do not point it at an
installed Production or Preview database, copy their data into a test, or
install/update/stop either daily-use package.

`OPENZA_TASKS_DEV_DATA_DIR` is an Avalonia Dev override, not a WinUI package-data
override. Use a new disposable directory for Avalonia comparisons and a fresh
Dev package store for WinUI. Keep equivalent test datasets in separate writable
stores; never let two hosts share one writable database.

## Select and verify the current combined Cloud bundle

The companion branch contains Cloud migrations through 24 and the current Web
parity source. There is no newly approved distributable artifact/version/hash
for that branch in this handoff. Verify the companion repository's current
Windows continuation note and exact commit before selecting a bundle.

The default `scripts/personal-cloud/artifact-lock.json` still pins
`0.1.0-preview.9`. Running `stage-artifact.mjs` with its default lock retrieves
that pin, **not the current combined branch**. Historical local alignment
bundles are also not a current release candidate. Do not deploy one merely
because it is already beside a binary, and do not edit the release lock as an
incidental Windows setup step.

The companion repository's source artifact command is:

```text
npm ci
npm run build:artifact -- <new-validation-version>
```

Its builder invokes Wrangler `deploy --dry-run`, writes a complete versioned
directory under `dist/`, and generates `artifact-manifest.json` with path, size
and SHA-256 entries. This is local artifact generation, not a Cloud deployment.
Use a new validation version: the builder replaces an existing output with the
same name. Artifact generation was validated on Linux; its Windows tool-launch
path remains to be checked. If a bundle is prepared on another approved build
host, transfer only the verified public-safe artifact, never its local data or
credentials. Do not create a credential workaround or an unreviewed builder.

Before injecting a selected bundle, independently confirm the source commit,
version, manifest SHA-256 and all 24 migration hashes against its reviewed
record. From the Tasks worktree, the existing verifier accepts an explicit
expected version/hash for a feature-validation bundle:

```powershell
# Supply the chosen artifact directory and its independently verified metadata.
$artifact = (Resolve-Path (Read-Host 'Verified artifact directory')).Path
$artifactVersion = Read-Host 'Verified artifact version'
$manifestSha256 = Read-Host 'Verified manifest SHA-256'
@'
import { verifyArtifact } from './scripts/personal-cloud/stage-artifact.mjs';
await verifyArtifact(process.argv[2], {
  artifactVersion: process.argv[3], manifestSha256: process.argv[4]
});
console.log('Artifact verified');
'@ | node --input-type=module - "$artifact" "$artifactVersion" "$manifestSha256"
```

Do not accept a checksum solely because it was computed from an arbitrary
download. `verifyArtifact` checks the complete allowlist and every file's
size/hash; `PersonalCloudArtifactDirectory.Load` verifies the bytes again when
the app starts. Both hosts copy the supplied directory to the output's
`PersonalCloudArtifact/` via the `PersonalCloudArtifactDirectory` MSBuild
property. The factory loads the whole bundle at startup, so an authorized Dev
restart is necessary after changing it. Do not run the Production publish
helper as a substitute for a Dev build.

## Build and run the Windows Dev hosts

In the Tasks worktree, after verifying the selected artifact:

```powershell
$env:PersonalCloudArtifactDirectory = $artifact
dotnet restore Openza.Tasks.slnx
dotnet test src\Openza.Tasks.Tests\Openza.Tasks.Tests.csproj -c Release --no-restore
dotnet build src\Openza.Tasks\Openza.Tasks.csproj -c Debug -p:Platform=x64 --no-restore "-p:PersonalCloudArtifactDirectory=$artifact"
```

Open `Openza.Tasks.slnx` in Visual Studio launched from that environment so the
artifact property is available to its build/deployment. Select **Openza Tasks
Dev**, **Debug**, **x64**, enable Build/Deploy only for that Dev target, then
F5. Verify the package identity and copied manifest before connecting it.
These are continuation instructions, not Windows commands already tested by
the Linux agent. A missing workload or signing prerequisite is a blocker to
report, not a reason to change package identities or bypass certificate checks.

For the Avalonia Windows reference, use a separate disposable Dev store:

```powershell
$env:OPENZA_TASKS_DEV_DATA_DIR = Join-Path $PWD 'artifacts\windows-dev-data'
dotnet restore src\Openza.Tasks.Desktop\Openza.Tasks.Desktop.csproj
dotnet run --project src\Openza.Tasks.Desktop\Openza.Tasks.Desktop.csproj -c Release -p:OpenzaChannel=Dev "-p:PersonalCloudArtifactDirectory=$artifact"
```

Keep automatic sync and provider connections off during initial comparison.
Fresh Windows provider sign-in, passkey/device pairing and Cloudflare OAuth
require the human's intended development account/target. Do not copy Linux
Secret Service entries, Windows Credential Locker state, browser sessions,
pairing profiles, OAuth tokens or private fixtures between machines.

## Remaining acceptance and excluded actions

- Verify native WinUI build and Dev startup; compare navigation, three/four-pane
  layout, task editor, dates, source/local recurrence, initial-sync feedback,
  settings, keyboard and provider-request states against the shared contract.
- Verify Windows browser/loopback OAuth and package credential-store behavior
  with fresh isolated Dev setup; a successful build is not that acceptance.
- Exercise shared sync/workspace flows with synthetic data and exact-version
  Cloud code. Cloud migrations 21–24 and local Core migrations 10/11 are different
  stores. No live migration or deployment is authorized by this note.
- Real-provider behavior remains a separate scoped acceptance step. Empty
  provider journals do not prove a Sync button will avoid configured accounts.
- The destructive lifecycle restore/removal drill remains deferred. Do not
  erase/re-pair stores to hide a sync error or treat a local SQLite restore as
  proof of remote D1 recovery.
- Linux databases, credentials, local fixture states, screenshots and ignored
  operational notes are intentionally not pushed. Do not depend on a previous
  maintainer's local paths or stale artifact directories.
- Production, Preview daily apps, merge, release, deployment and installation
  remain excluded. Snap packaging/installation was skipped, not accepted.
