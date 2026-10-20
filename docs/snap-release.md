# Snap Store release runbook

Openza Tasks uses one strict `openza-tasks` Snap as its public Linux package.
It contains the Avalonia desktop app and the `openza` CLI. Store installations
track a release channel and receive Snap refreshes; local sideloads need Store
adoption before they track those updates.

## Coordinated Desktop and Personal Cloud release plan

### Published version and proposed next release

[Openza Tasks is published on the Snap Store](https://snapcraft.io/openza-tasks).
Store revision **1** is version **1.1.0**, available on **latest/stable** and
**latest/edge**, created **2026-09-12 13:07 UTC**, with size **71,716,864 bytes**.
The [GitHub v1.1.0 Snap asset](https://github.com/openza/tasks/releases/tag/v1.1.0)
has the same size. Its source tag resolves to
`f45fbe1314f9e6c767cbc3aefe8b64711e4525e4`, before Personal Cloud was added.
Equal byte counts do not prove checksum or binary identity.

For a new installation, use `sudo snap install openza-tasks`. The GitHub asset
with checksum verification and `--dangerous` is a local-file fallback. Public
**1.1.0 has no Personal Cloud**; a custom package with the same version string
may contain different code.

The proposed next Linux Snap version is **1.2.0**, with planned changes under
[CHANGELOG Unreleased](../CHANGELOG.md#unreleased). Before release, update the
Snap version, both .NET publish version properties, AppStream metadata and final
changelog/tag together. The proposal does not change those version fields.
Windows package publication remains a separate release lane.

### Cloud artifact selection

Desktop currently pins `0.1.0-preview.9` / schema 20. The development Cloud source
`660c847d6ba7a1cb16c6252b2e2aee46361d0cb5` produces the validation bundle
`0.0.0-local-quota-660c847d6ba7`, schema 25, manifest format 1, with SHA-256
`cdd87066e97e622f13c32ec9507292d65791b4361ed763926c5e4e610c47c5bc`.
This bundle is not the packaged release pin. Select a release artifact and digest
explicitly; a successful loader check or pinned CI build does not select it.
See [artifact staging](personal-cloud-desktop-publish.md),
[compatibility](cloud-upgrade-compatibility.md) and [quota behavior](personal-cloud-quota.md).

### Release sequence

1. Review the coordinated Desktop and Cloud changes. Record exact source commits,
   release version, dependency lock and artifact digest before merging. Changing
   the Cloud pin requires fresh provenance and loader validation.
2. Build with the existing Snap wrappers and the selected Cloud artifact. Record
   the package hash, app/CLI channel markers and embedded artifact digest. Keep
   output under ignored `artifacts/snap/`. Check disk and memory headroom; clean
   the Snapcraft/LXD build instance on exit and verify cleanup afterward.
3. Validate an upgrade in a separate Ubuntu test account or VM with synthetic
   tasks and test credentials. Check GUI/CLI data sharing, task relationships,
   restore/export, Secret Portal access, provider/backup credential separation,
   pairing identity, pending envelope bytes/order, quota restart/reset and the
   automatic-sync preference. Include offline and unavailable-credential startup.
   A Preview parallel-instance smoke test does not establish Production-to-
   Production refresh or credential continuity. Keep live installations separate.
4. Obtain maintainer release approval before the manual `Publish Snap Store Edge`
   workflow. It builds and publishes; it is not a dry run. Retain the exact
   package/hash and Store revision. Validate that revision from `edge`, then
   promote the same revision to `candidate` and `stable` after channel acceptance.
5. Update participating Desktop clients before upgrading Cloud. The packaged
   client must support the existing schema-20 deployment and target schema-25
   quota errors. Older strict parsers are not assumed compatible. The existing
   installed `Update` flow does not acquire a local development bundle.
6. Before the Cloud upgrade, approve the exact target and artifact, verify a
   backup, and run target-identity/migration-history preflight. Production database
   writes remain human-executed through the upgrade procedure. Verify readiness,
   preserved state and usage afterward. Keep pending outbox entries intact.

Windows native packaging, MSIX/Store replacement and sign-in acceptance remain
separate validation work. Shared-label rename is deferred to a dedicated label
list; equivalent task-editor label edits remain a no-op.

## Sideload-to-Store transition

A local installation might show version `1.1.0`, revision `x8`, installed with
`--dangerous`, while the Store offers revision `1`. These are different package
identities for provenance purposes: the version label alone cannot establish
source, features, checksum or existing Personal Cloud state. A separate parallel
instance such as `openza-tasks_pcdev` has its own test state and must remain
untouched during adoption of the main `openza-tasks` installation.

1. Confirm the approved candidate version, Store revision and package hash with
   `snap info openza-tasks` and `snap list --all openza-tasks`. Stop if
   latest/candidate is empty, inherits the older stable revision, or does not
   contain the intended package. Public revision 1 is not the Cloud candidate.
2. Make and verify an app export and restorable backup. Close the app and other
   writers before taking a consistent pre-upgrade data copy. Retain a known-good
   package. The [snapshot guide](https://snapcraft.io/docs/how-to-guides/manage-snaps/create-data-snapshots/)
   documents `sudo snap save openza-tasks`, `sudo snap check-snapshot <set-id>`
   and `sudo snap export-snapshot <set-id> openza-tasks-before-store.zip`.
   Substitute the returned set ID and retain the export independently. A snap
   data snapshot does not establish that external keyring secrets are backed up.
3. Prove a compatible rollback path in isolation before approving adoption. A
   retained, supported prior revision may permit
   `sudo snap revert openza-tasks --revision=x8`. Otherwise validate recovery with
   a known-good local package and compatible data backup first. Stop if neither
   path is available. Revert does not change channel tracking: agree a per-snap
   hold/tracking policy, and avoid named refreshes or lifting a hold until the
   recovery is accepted.
4. After maintainer approval for the live installation, use same-name adoption:

   ```bash
   sudo snap refresh openza-tasks --amend --channel=latest/candidate
   ```

   Canonical's [update guide](https://snapcraft.io/docs/how-to-guides/manage-snaps/manage-updates/)
   documents `--amend`; its [official example](https://snapcraft.io/blog/time-to-branch-out)
   combines it with `--channel`. Do not remove/purge the app or target `_pcdev`
   as an adoption shortcut.
5. Verify Store revision/version, candidate tracking, GUI/CLI startup, tasks,
   settings and restore/export access. Production uses `$SNAP_USER_COMMON/tasks`
   and credential namespace `tasks`. Snap's [environment contract](https://snapcraft.io/docs/reference/development/environment-variables/)
   makes this common data revision-independent, but **refresh/revert does not
   restore it**. A code downgrade is not a database rollback. A compatible data
   restore is a separate recovery step with writers stopped; `snap restore`
   overwrites the current data.
6. Check credential and pairing continuity without exposing secret values. Source
   retains Secret Tool attributes `openza-app=tasks` and `credential-key`; the
   [Secret portal](https://snapcraft.io/docs/how-to-guides/snap-development/use-the-secret-portal/)
   requires its backend/interface and a per-snap master secret. Stable names and
   keys are necessary continuity conditions, not proof that tokens or pairing
   survive on every system. Preserve existing main-instance pairing and pending
   outbox, including state created by a custom build. Stop on unexpected identity
   changes rather than resetting or clearing state. Never reconstruct that state
   from `_pcdev` or use a real provider mutation as a credential probe.

Treat adoption, recovery restores, Store promotion and Cloud upgrades as separate
maintainer release decisions. Follow the packaged acceptance requirements before
changing a live installation.

## One-time Store setup

1. Sign in to the Snapcraft publisher account and register the exact name
   `openza-tasks`.
2. Complete the public Store listing with the same product name, icon,
   screenshots, support URL, source URL, license, and privacy information used
   by the Openza website and repository.
3. Request the automatic alias `openza` for the Snap app
   `openza-tasks.openza`. Until approval, the supported command remains
   `openza-tasks.openza`.
4. Create a time-limited Store credential scoped to `openza-tasks`, the `edge`
   channel, and only the upload/release permissions needed by CI. Store its
   contents as the `SNAPCRAFT_STORE_CREDENTIALS` secret in the protected
   `snap-store` GitHub environment. Never commit the credential file.

Registration, Store listing changes, alias requests, credential creation, and
uploads are external writes. Include them in the approved maintainer release procedure.

## Build and validate locally

Update the version in `snap/snapcraft.yaml`, its two .NET publish properties,
and `snap/gui/com.openza.Tasks.metainfo.xml` together. Then run:

```bash
dotnet test src/Openza.Tasks.Tests/Openza.Tasks.Tests.csproj -c Release -m:1
dotnet build src/Openza.Tasks.Desktop/Openza.Tasks.Desktop.csproj -c Release
bash packaging/snap/test-snap-package.sh
gitleaks detect --source . --verbose
bash packaging/snap/build-local-snap.sh \
  artifacts/snap/openza-tasks.snap \
  /path/to/openza-personal-cloud-<version>
```

The local build wrapper inspects the package and cleans its Snapcraft build environment on exit. Check available disk space before building and verify that the task build instance has been removed afterward.

Inspect the built Snap before installation. It must contain both Production
channel markers, the desktop launcher, AppStream metadata, icon, GUI binary,
CLI binary, and `secret-tool`. Installing a local build replaces application state and belongs in an approved
maintainer test or release procedure.

### Isolated Personal Cloud preview

Build the Personal Cloud runtime package with Preview channel markers so its
Openza credential namespace stays separate from Production:

```bash
bash packaging/snap/build-personal-cloud-preview-snap.sh \
  artifacts/snap/openza-tasks-personal-cloud-preview.snap \
  /path/to/openza-personal-cloud-<version>
```

The wrapper builds from a disposable working-tree snapshot and verifies both
Preview markers in the packed Snap. Runtime testing uses Snap's parallel
instance name `openza-tasks_pcdev`, which gives the test instance separate user
data, configuration, interfaces, applications, and aliases. Parallel instances
are an experimental snapd feature and require an administrator to enable them;
enable the feature and install packages only in a maintainer-approved test environment.
Follow the official parallel-install procedure and install the
local file with the explicit instance name and `--dangerous`. Connect only the
minimum required interfaces for `openza-tasks_pcdev`, test against the retained
Dev Personal Cloud, and remove only that named instance after preserving any
test diagnostics. Never install this preview file as plain `openza-tasks`.

## Publish and promote one revision

1. Complete Linux, Windows, and Snap CI for the reviewed commit, then manually run
   **Publish Snap Store Edge** for that same commit. It builds once, inspects the
   package, smoke-tests the packaged CLI, retains the verified artifact, and
   uploads that exact artifact to `edge`. The `snap-store` environment must have
   its publishing credential configured before dispatch.
2. Record the Store revision from the workflow/Store and install that revision
   from `edge` on a clean Ubuntu test account or machine.
3. Validate GUI startup, shared GUI/CLI data, task CRUD, restore points,
   Todoist credential storage through the Secret Portal, sync, confinement,
   desktop integration, CLI output, and refresh persistence.
4. Promote the same Store revision to `candidate`; do not rebuild it. Repeat
   the smoke test from the Store.
5. Promote that same revision to `stable`. Users on stable receive it through
   Snap's normal automatic refresh mechanism.

Use `snapcraft revisions openza-tasks` to resolve the revision, then promote it
with the official release command:

```bash
snapcraft release openza-tasks <revision> candidate
snapcraft release openza-tasks <revision> stable
```

Do not upload a new build separately to candidate or stable. Promotion keeps
the tested binary identical across channels. For rollback, release the last
known-good revision back to the affected channel.

## GitHub release and validation

Use one product `v<version>` tag across platforms, such as `v1.1.0`, and keep Store revision numbers as package-specific build identifiers. The tag-triggered **Release Validation** workflow validates both hosts and retains its inspected Linux Snap as a diagnostic artifact. It does not create a GitHub release automatically or publish a Windows package. After validation succeeds, dispatch **Publish Snap Store Edge** at that exact tag. Download and extract the immutable `openza-tasks-store-snap` workflow artifact from that successful publishing run. Compute the SHA-256 checksum from the extracted raw Snap, then attach that exact `openza-tasks_<version>_amd64.snap` file and its matching `openza-tasks_<version>_amd64.snap.sha256` file to the GitHub release. Do not attach the workflow artifact ZIP as the Snap package.

Every release must describe which platforms received that version. Preserve unpublished Windows drafts and do not claim Windows availability until the tagged Windows host has passed its separate acceptance checks.

If Store review delays channel availability, the verified Snap may be published as a GitHub prerelease with the local-package installation command and a clear explanation that `--dangerous` means the package lacks a Store assertion while remaining strictly confined. After Store approval, promote the same revision unchanged through the accepted channels and update the GitHub release with the Store installation and `snap refresh --amend` transition instructions.

A local Snap revision such as `x7` is not a Store revision. Do not promote a local revision number or assume that a successful local install proves Store acceptance. Real-account Microsoft, GitHub, and OneDrive flows and restore behavior should be exercised under confinement with isolated test data before stable promotion; see [Avalonia acceptance checks](avalonia-parity.md).

## First public release

There is no Linux-package migration in the first release. The public Snap
starts with its own data below `$SNAP_USER_COMMON/tasks` and its own Secret
Portal credentials. Source Dev data and maintainer Preview data remain
separate and are never imported automatically.
