# Personal Cloud artifact and Desktop publish validation

Run from a clean Tasks checkout with Git, Node.js 22/npm, and the .NET 10 SDK:

```sh
node --test scripts/personal-cloud/stage-artifact.test.mjs
node scripts/personal-cloud/publish-desktop.mjs 1.1.0
```

This is maintainer publish validation, not a new public distribution lane or a release.
It neither launches nor installs the output and does not access an existing task database.
The version argument is required; `1.1.0` above matches the existing package metadata
for validation and does not select a new release version.

`artifact-lock.json` pins the public Personal Cloud repository to full source commit
`be1691402129d7a571f9bd881a91d93bafe28ef1`, artifact `0.1.0-preview.9`,
and an expected manifest SHA-256. Staging fetches that exact commit into an ignored
temporary source directory, uses `npm ci` with its committed lockfile, and invokes
the upstream dry-run artifact builder. It requires network access to GitHub and npm,
but no Cloudflare credentials or sibling checkout. Temporary source/dependencies are
removed after completion; verified artifact output remains under `artifacts/personal-cloud/`.
No existing staged bundle is replaced; a cached bundle must pass the pinned manifest,
every file checksum/size, and the exact file allowlist before reuse. A mismatch stops
the operation rather than updating the lock automatically.

Publishing uses the dedicated Desktop package project with Production profile and
copies the verified manifest, Worker, complete asset set and ordered migration bundle into
`PersonalCloudArtifact/`. The helper verifies the published artifact and Production
channel marker. Output is framework-dependent and requires the .NET runtime; it is
only an intermediate validation artifact. Public Linux distribution remains Snap-only.
An existing publish destination is never overwritten, and failed output is retained
for diagnosis. CI performs this same publish validation in the Linux build job.

To update the cloud lock, review the new upstream full commit and dependency lock,
build its versioned artifact, and intentionally update the expected manifest digest.
Do not pin a branch name or silently accept a newly generated digest.

The source lock remains unchanged by the [upgrade compatibility fixes](cloud-upgrade-compatibility.md).
Offline validation of a newer local artifact does not select it for publication.

Public OAuth availability, registered redirect/scopes, packaged browser/loopback and
credential-store acceptance, release version/changelog, and domain configuration remain
separate release gates. The helper does not deploy, perform OAuth, migrate a live D1
database, change DNS, build Snap, or build Windows packages.
