# Cloud upgrade compatibility (cloud-upgrade-v1)

The client/server contract uses manifest format 1, protocol 1 and target schema 25.
Artifact selection is controlled by the checked-in lock. Desktop staging verifies
the pinned manifest and every file; the runtime loader also validates the ordered
migration bundle and SQL hashes.

The [quota pause and dispatch budget](personal-cloud-quota.md) defines client-side
daily exhaustion handling, durable reservations and change-only catalog publication.

## Source and schema compatibility

A product version string or channel alone does not identify the Personal Cloud
client. Use exact source and artifact identities when diagnosing compatibility.
The companion repository's `docs/compatibility-matrix.md` records Cloud schema
history; `docs/architecture.md` describes Web module and API boundaries.

| Desktop source | Supported health schemas | Compatibility limit |
| --- | --- | --- |
| Public `v1.1.0`, `f45fbe1314f9e6c767cbc3aefe8b64711e4525e4` | Personal Cloud absent | A custom build with the same version label may have different features. |
| Initial integration, `3e2a3f923452d461f4228bb3ee65fc8ae1f79796` | 17–20 | Strict historical DTOs lack capability negotiation; an allowlist alone does not establish forward compatibility. |
| Compatibility implementation from `e7378d7060ed8812eb3608890e25d53633a94f1e` | 17–25, protocol 1, product tasks | Per-pass readiness, capability retention, migration checksum/resource preflight and durable upgrade publication apply. |

The artifact lock still selects `0.1.0-preview.9`, Cloud source
`be1691402129d7a571f9bd881a91d93bafe28ef1`, manifest SHA-256
`5eb0d68377c12f9f5a1f12f82f7f2330ff61d1c58689dae625f4c7fdbf02153d`.
That bundle advertises schema 20. Desktop accepting schema 25 does not change the
packaging input or identify the deployed database. Artifact labels are opaque,
not a numeric upgrade order. See [artifact staging](personal-cloud-desktop-publish.md).

| Schema/feature | Client contract |
| --- | --- |
| 17–20 base protocol | Health acceptance; legacy semantics when optional capabilities are absent. |
| 21 initial-upload status | Optional monitoring; absence cannot block core sync. |
| 22 mutation batch | Negotiate mutationBatch protocol 1 before bounded batch delivery. |
| 23 workspace/catalog | Workspace protocol 1 on 23–25; catalog 1/2 requires workspace. |
| 24 paging | Paging protocol 1 only on 24/25. |
| 25 updatedAt | Validate health/capability relationships and current payload/store semantics. |

Schema acceptance and feature negotiation are separate. The schema-25 Worker
requires its complete D1 footprint; it must not run against an older incomplete
database simply because Desktop accepts that older schema. Test rejected
readiness, capability downgrade, legacy fallback, migration-prefix mismatch and
pending-envelope preservation separately. An offline artifact-loader check proves
bundle integrity, not a persisted Desktop-to-Worker round trip or installed-package
acceptance.

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
history requires a maintainer recovery procedure. Only the supported fresh setup path accepts an
empty database. Setup retries preflight before re-upload; migration application
checks again and applies only the missing suffix. A complete matching journal is
not rewritten or replayed.
An unsuccessful per-statement [D1 query result](https://developers.cloudflare.com/api/resources/d1/subresources/database/methods/query/)
also stops the suffix even if the outer API envelope reports success.

Upgrade additionally checks the configured account's canonical workers.dev origin
and the existing Worker's D1 and instance bindings before upload. This uses read-only
Cloudflare [account subdomain](https://developers.cloudflare.com/api/resources/workers/subresources/subdomains/methods/get/)
and [Worker settings](https://developers.cloudflare.com/api/typescript/resources/workers/subresources/scripts/subresources/script_and_version_settings/methods/get/)
APIs. A different origin, database or instance stops before remote writes. Export
and update management require the original workers.dev address; pairing alone does
not establish ownership of a custom-domain deployment.

Sync readiness and maintenance repair are distinct: a trustworthy known migration
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
