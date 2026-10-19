# Cloud upgrade compatibility (cloud-upgrade-v1)

This contract preserves manifest format 1, protocol 1, schema 25 and the existing
successful health response and capabilities. It adds no migration or artifact pin
change. Desktop staging still verifies the exact pinned manifest and every file;
the runtime loader also validates the ordered migration bundle and SQL hashes.

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
