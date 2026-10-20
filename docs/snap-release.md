# Snap Store release runbook

Openza Tasks uses one strict `openza-tasks` Snap as its only public Linux
package. The Snap contains the Avalonia desktop app and the `openza` CLI, and
Snap refreshes deliver updates automatically. These steps are maintainer-only;
ordinary users do not select an Openza environment or run migration tools.

## Coordinated Desktop and Personal Cloud release plan

On 2026-10-20 the owner reported that Snap Store approval had been obtained.
That approval has not been independently checked here. It brings Snap packaging
and publication into this release plan; it does not establish that the current
candidate has been built, tested, uploaded, or made available in a Store channel.
Record the approved revision and its provenance when preparing the release.

The current step is branch publication and PR review only. Existing PR CI can
build a Snap and smoke-install it on a disposable GitHub runner; this does not
validate an upgrade of an existing user installation. No local Snap build,
installed-app replacement, manual publishing workflow, or cloud upgrade is part
of this step. Keep the owner involved at each later approval gate below.

The reviewed Desktop runtime is `6b0ab43237cca3976b27ea5ef956badc2f216499`;
the coordinated Cloud source is `660c847d6ba7a1cb16c6252b2e2aee46361d0cb5`
on `fix/cloud-quota-budget` in the companion repository. Desktop still pins
`0.1.0-preview.9` / schema 20. The newer **validation-only** bundle
`0.0.0-local-quota-660c847d6ba7` targets schema 25, manifest format 1, with
manifest SHA-256
`cdd87066e97e622f13c32ec9507292d65791b4361ed763926c5e4e610c47c5bc`.
Its successful Desktop loader check does not change the pin or ship that bundle.
The current pinned publish/Snap CI therefore does not certify a package carrying
the new schema-25 candidate. See [artifact staging](personal-cloud-desktop-publish.md),
[compatibility](cloud-upgrade-compatibility.md), and [quota behavior](personal-cloud-quota.md).

1. **Review both PRs before a separately approved merge.** Preserve their exact
   source commits and required checks. Choose the release version, changelog and
   final Cloud artifact deliberately; review any new source pin and manifest
   digest in Desktop. A release artifact replacing the local validation bundle
   needs its own provenance and loader verification.
2. **Prepare the Snap only after build approval.** Use the existing build and
   inspection wrappers with the selected verified Cloud artifact. Record the
   source, version, package SHA-256, both app/CLI channel markers, and embedded
   artifact digest. Keep output under ignored `artifacts/snap/`. Check disk and
   memory headroom; the builder owns exit cleanup and verification that its
   Snapcraft/LXD build instance is gone. Do not install the output yet.
3. **Approve and test an isolated upgrade.** Use a separate Ubuntu test account
   or VM, synthetic tasks and test-account credentials. Establish the previous
   package revision and expected database, settings, pairing and outbox state,
   then upgrade to the exact candidate. Verify GUI/CLI data sharing, task IDs and
   relationships, restore/export behavior, Secret Portal credential access,
   provider and backup credential separation, pairing identity, capability
   negotiation, pending envelope bytes/order, quota pause/restart/reset, and the
   automatic-sync choice. Include offline and unavailable-credential startup.
   Use an isolated Dev cloud; do not copy real credentials or mutate real
   provider tasks. A Preview parallel-instance smoke test is useful isolation
   evidence, but it does not prove Production-to-Production refresh or credential
   continuity. Do not replace the owner's daily app or data for these tests.
4. **Approve the exact Store upload and channel changes.** The manual
   `Publish Snap Store Edge` workflow builds and publishes an artifact; do not
   dispatch it as a dry run. After approval, retain its exact package/hash and
   Store revision, validate that revision from `edge` in isolation, then request
   approval to promote the same revision to `candidate` and later `stable`.
   Follow the runbook below; Store approval alone is not publication approval.
5. **Update participating Desktop clients before Cloud.** Validate that the new
   packaged client works with the existing schema-20 cloud and understands the
   target schema-25 quota errors. Establish installed-client coverage before
   enabling the new Cloud behavior; old strict parsers are not assumed compatible.
   The unchanged installed `Update` flow does not acquire this local candidate.
6. **Request the separate Cloud upgrade approval.** Name the exact target,
   source, artifact and digest. Use the reviewed backup, target-identity and
   migration-history preflight before the approved upgrade flow; production
   database writes remain human-executed. Afterward verify health/readiness,
   preserved state and usage. Keep existing outbox entries intact; do not
   compact, reorder or regenerate them to accelerate catch-up.

Windows native packaging, MSIX/Store replacement and sign-in acceptance remain
later work behind their own gates. Dev-gated managed continuity tests do not
complete those gates. Shared-label rename remains parked for a future dedicated
label list; this release keeps equivalent task-editor label edits as a no-op.

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
uploads are external writes. Perform them only when explicitly authorized.

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
CLI binary, and `secret-tool`. Installing a local build requires explicit user
approval because it creates or replaces a live application.

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
do not enable the feature or install the local package without explicit approval.
After approval, follow the official parallel-install procedure and install the
local file with the explicit instance name and `--dangerous`. Connect only the
minimum required interfaces for `openza-tasks_pcdev`, test against the retained
Dev Personal Cloud, and remove only that named instance after preserving any
requested diagnostics. Never install this preview file as plain `openza-tasks`.

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
