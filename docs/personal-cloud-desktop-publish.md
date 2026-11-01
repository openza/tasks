# Desktop publish validation and local cloud artifacts

From 1.3.0, Openza Desktop does not bundle Personal Cloud. Cloud releases ship
separately as signed GitHub releases of `openza/personal-cloud`; Desktop lists
them, verifies the signature, archive and every artifact file, and installs the
newest release it supports when the owner chooses **Personal Cloud → Manage →
Update** (or during first setup). The release format, signing key, selection
rules and add-only compatibility rules are defined in the cloud repository's
`docs/cloud-releases.md`. The verifying code is `GitHubPersonalCloudReleaseSource`
with the public key in `PersonalCloudReleaseKeys`.

## Publish validation

Run from a clean Tasks checkout with Node.js 22/npm and the .NET 10 SDK:

```sh
node --test scripts/personal-cloud/*.test.mjs
node scripts/personal-cloud/publish-desktop.mjs 1.3.0
```

This is maintainer publish validation, not a release. It publishes the
Production profile, checks the channel marker, and fails if a
`PersonalCloudArtifact/` directory was bundled. It neither launches nor installs
the output and never overwrites an existing destination. CI runs the same check.

## Testing an unreleased cloud locally (Dev only)

A Dev build may bundle a locally built cloud artifact to test cloud changes
before they are released. Desktop then installs that artifact instead of a
release:

```sh
# in personal-cloud
npm run build:artifact -- <version>
# in tasks
dotnet run --project src/Openza.Tasks.Desktop -p:PersonalCloudArtifactDirectory=/path/to/dist/openza-personal-cloud-<version>
```

`scripts/personal-cloud/stage-artifact.mjs` can still rebuild the reference
revision in `artifact-lock.json` for acceptance tests; that lock no longer
selects what a release ships. Release Snaps never contain a cloud artifact
(`packaging/snap/inspect-snap-package.sh` checks this).
