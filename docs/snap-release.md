# Snap Store release runbook

Openza Tasks uses one strict `openza-tasks` Snap as its only public Linux
package. The Snap contains the Avalonia desktop app and the `openza` CLI, and
Snap refreshes deliver updates automatically. These steps are maintainer-only;
ordinary users do not select an Openza environment or run migration tools.

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
mkdir -p artifacts/snap
snapcraft pack --output artifacts/snap/openza-tasks.snap
bash packaging/snap/inspect-snap-package.sh artifacts/snap/openza-tasks.snap
```

Inspect the built Snap before installation. It must contain both Production
channel markers, the desktop launcher, AppStream metadata, icon, GUI binary,
CLI binary, and `secret-tool`. Installing a local build requires explicit user
approval because it creates or replaces a live application.

## Publish and promote one revision

1. Manually run the GitHub Actions workflow **Publish Snap Store Edge** for the
   reviewed commit. It builds once and uploads that exact artifact to `edge`.
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

## First public release

There is no Linux-package migration in the first release. The public Snap
starts with its own data below `$SNAP_USER_COMMON/tasks` and its own Secret
Portal credentials. Source Dev data and maintainer Preview data remain
separate and are never imported automatically.
