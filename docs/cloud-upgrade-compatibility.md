# Cloud upgrade compatibility (cloud-upgrade-v1)

This contract preserves manifest format 1, protocol 1, schema 25 and the existing
successful health response and capabilities. It adds no migration or artifact pin
change. Desktop staging still verifies the exact pinned manifest and every file;
the runtime loader also validates the ordered migration bundle and SQL hashes.

The [quota pause and dispatch budget](personal-cloud-quota.md) defines client-side
daily exhaustion handling, durable reservations and change-only catalog publication.

## Desktop sources, schemas and evidence

Recorded 19 October 2026. Exact source and artifact identity matter: a marketing
version or channel alone does not identify the Personal Cloud client. These are
source gates and local validation results, not an installed/deployed version map.
The matching Cloud-side schema history and Web boundaries are documented in the
`personal-cloud` repository's `docs/compatibility-matrix.md` and
`docs/architecture.md`.

| Desktop identity | Health support in source | Interpretation |
| --- | --- | --- |
| Tag `v1.1.0`, `f45fbe1314f9e6c767cbc3aefe8b64711e4525e4` | Current Personal Cloud client and artifact lock absent | Predates this implementation. An installed version string alone cannot establish compatibility. |
| First Personal Cloud integration, `3e2a3f923452d461f4228bb3ee65fc8ae1f79796` | Explicit schemas 17–20 | Historical health DTO rejects unknown members and lacks capabilities; sync lacks current per-pass negotiation. Its allowlist does not prove safe forward compatibility for an already-paired client. |
| Main, `5637e3adc7ea6a57b2a7fdfa44bf2b9de93c5201` | Explicit schemas 17–25, protocol 1, product tasks | Capability validation exists; predates the reviewed fail-closed discovery and complete migration/resource preflight fixes. |
| Integration baseline, `83d60890dd24ead67d08d8d45cd246bc3d3e3610` | Explicit schemas 17–25, protocol 1, product tasks | Same declared range and pin; the reviewed safety changes are on a branch atop this baseline. |
| Reviewed runtime, `e7378d7060ed8812eb3608890e25d53633a94f1e` | Explicit schemas 17–25; rejects 16/26 and wrong protocol/product | Per-pass readiness, capability retention, ordered checksum-prefix/resource preflight and upgrade lifecycle guards described below. |

The inspected Personal Cloud-era sources retain the checked-in artifact pin
`0.1.0-preview.9`, cloud revision `be1691402129d7a571f9bd881a91d93bafe28ef1`,
manifest SHA-256 `5eb0d68377c12f9f5a1f12f82f7f2330ff61d1c58689dae625f4c7fdbf02153d`.
That Worker advertises schema 20. Accepting schema 25 does not change this packaging
input or establish a deployed cloud's state. Artifact version labels are opaque;
they do not imply numeric upgrade ordering.

| Schema/feature at reviewed Desktop source | Code contract | Positive local evidence and limit |
| --- | --- | --- |
| 17–20 base protocol | Explicit health acceptance; legacy path when optional capabilities are absent | Mock HTTP health acceptance for each schema. Fake HTTP/cloud pairing and upgrade theory covers 17–23; this is not a historical deployed Worker matrix. |
| 21 initial-upload status | Optional monitoring; absence cannot block core sync | Status/parser fixtures; no new hosted-schema acceptance claim. |
| 22 mutation batch | Negotiate mutationBatch protocol 1 | Mock HTTP batch, fallback and pending-outbox tests. |
| 23 workspace/catalog | Workspace protocol 1 on 23–25; catalog 1/2 requires workspace | Workspace/catalog and downgrade fixtures; fake lifecycle theory includes 23. |
| 24 paging | Paging protocol 1 only on 24/25 | Mock HTTP paging/bootstrap/bounds tests. Named lifecycle theory does not include 24/25. |
| 25 updatedAt/current Worker | Health and capability relationship checks; current payload/store semantics | Parser/store regressions and actual schema-25 artifact-loader acceptance. Loader acceptance is not a persisted Desktop-to-Worker sync round trip. |

Health parser tests cover all nine accepted schemas; feature availability still
requires explicit capability negotiation. No complete Desktop-binary × historical
Worker/schema end-to-end matrix was run. In particular, the current schema-25
Worker requires its complete D1 footprint: its rejection of incomplete
empty/17/20/22/24 fixtures is negative readiness evidence, not successful backward
compatibility with older databases.

The reviewed local pair is Desktop `e7378d7` and Cloud runtime
`853551f8d31f30535ba40fbc490240e8243b6620`. Desktop's unique full shared scope
passed 1,071 tests with nine explicit external opt-in skips; Cloud passed 179 Worker
and 170 Web tests, with synthetic Chrome checks and independent review. Desktop
Release and Windows Dev managed compilation passed; native Windows/package,
real-provider and live-cloud acceptance remain separate.

Actual artifact-loader verification used local artifact
`0.0.0-local-upgrade-v1`, manifest format 1/protocol 1/schema 25, manifest SHA-256
`c97990d0e5a1ac224e5748ca214f6be341905356db761ce72f4857a6d7cf3eb3`.
This frozen local artifact is separate from the checked-in preview.9 pin and is not
a release or deployment. A later docs-only commit retains runtime evidence without
rebuilding this artifact; any rebuilt artifact needs its own verified digest.

## Health and sync

The Worker returns health 200 only after bounded, read-only checks of required
schema objects and columns, including current invariants. Incomplete or unavailable
schema returns no-store 503 with generic `schema_not_ready`, without SQL, resource
or task details. Readiness does not prove historical migration hashes were applied.

Desktop negotiates health before each sync pass, including reuse of the same
client. Unready, malformed, incompatible or failed health stops delivery without
changing persisted capabilities, pending envelopes or checkpoints. Initial-upload
status reporting also waits for readiness; local blocked progress can still be
shown without sending an optional cloud status request.

Only explicit 404, 405 or 501 from the health endpoint permits legacy single-envelope
delivery, and only when no workspace/catalog capability was previously negotiated.
Previously accepted workspace/catalog capabilities cannot silently regress. A
temporary HTTP or parsing failure never grants legacy fallback. Retry retains the
same mutation IDs, sequences and canonical envelopes.

## Upgrade and setup resume

Before upload, Desktop validates the complete candidate migration sequence and
reads the full applied checksum journal in explicit binary name order. Names must
start at 0001, advance without gaps or duplicates, and match the SQL SHA-256. The
applied journal must be an exact checksummed prefix of that candidate. Unknown,
newer, gapped, duplicate or changed entries stop before deployment or migration.
This includes a newer schema history carrying the same artifact version label;
artifact identifiers are not compared lexicographically to invent version ordering.

The Wrangler `d1_migrations` names-only ledger is not checksum provenance and is
not automatically converted. An existing database without verifiable checksum
history requires reviewed recovery. Only the supported fresh setup path accepts an
empty database. Setup retries preflight before re-upload; migration application
checks again and applies only the missing suffix. A complete matching journal is
not rewritten or replayed.
An unsuccessful per-statement [D1 query result](https://developers.cloudflare.com/api/resources/d1/subresources/database/methods/query/)
also stops the suffix even if the outer API envelope reports success.

Upgrade additionally checks the authorized account's canonical workers.dev origin
and the existing Worker's D1 and instance bindings before upload. This uses read-only
Cloudflare [account subdomain](https://developers.cloudflare.com/api/resources/workers/subresources/subdomains/methods/get/)
and [Worker settings](https://developers.cloudflare.com/api/typescript/resources/workers/subresources/scripts/subresources/script_and_version_settings/methods/get/)
APIs. A different origin, database or instance stops before remote writes. Export
and update management require the original workers.dev address; pairing alone does
not establish ownership of a custom-domain deployment.

Sync readiness and authorized repair are distinct: a trustworthy known migration
prefix can resume an interrupted upgrade even while health is 503. After deployment
and migration, the target must pass health with the exact requested artifact version.
Identity, pending intents and checkpoints are retained. Upgrade holds the existing
shared authoring lease, allowing local edits while excluding connection replacement.
The new in-memory profile is published only after its durable save succeeds.

These checks support safe rejection and retry; they do not make deployment and D1
migration one atomic operation. Already dispatched requests can still have an
uncertain result and must retain their existing idempotent envelopes. Native Windows
acceptance, packaging, live-provider checks and deployment remain separate gates.
Shared-label rename remains parked for the future dedicated label list.
