# Personal Cloud quota pause and dispatch budget

This client contract implements `quota-pause-v1` alongside the companion
repository's `docs/quota-pause-v1.md`. It keeps protocol 1, cloud schema 25,
manifest format 1, healthy capability negotiation and the checked-in artifact pin
unchanged. [Compatibility and upgrade contract](cloud-upgrade-compatibility.md)
still distinguishes the packaged preview.9/schema-20 pin from a local candidate.

## Daily quota recognition

A typed `daily_quota_exceeded` error carries `retryable: true` and
`quota: { protocolVersion: 1, resource: d1_rows_read | d1_rows_written, resetAt }`.
The reset is canonical ISO UTC at the next midnight, checked against the response
Date when present. Once the strict typed code/resource envelope is recognized,
malformed, stale or absurd time hints fall back to the next UTC midnight; they do
not downgrade known exhaustion to transient retries. Malformed code/resource
envelopes do not grant a daily pause. The client records local observation time,
the server's UTC reset and a fixed local retry deadline; blocked retries do not
move that deadline forward. Original server messages, SQL and request details are
not displayed.

When response Date differs from the local clock, the remaining server interval is
translated into a local deadline. A fast clock therefore cannot immediately bypass
a confirmed daily pause. The screen retains the server reset and identifies a
material clock difference. Restarts with the same clock offset retain the pause;
changing the system clock after persistence can change when the local deadline is
reached. No local policy can prove external UTC elapsed time across arbitrary
clock changes and restarts without another trusted observation. UTC-day counter
rollback does not replenish allowance, and a resumed failed request pauses again.

Workers request exhaustion can occur before Worker code runs. The client accepts
only a bounded unsuccessful Cloudflare edge response: HTTP 400–599, Server exactly
cloudflare (trimmed/case-insensitive) or a nonempty cf-ray, text/plain or text/html,
and at most 64 KiB. Plain text must trim-match `error code:\s*1027`. HTML requires
a dedicated span with class token `cf-error-code`, value 1027, and a Cloudflare
error-page title. Successful task data, arbitrary mentions, other edge codes and
generic 429/403/503 responses never become daily quota signals. Unrecognized edge
formats remain transient failures. The edge format is not guaranteed.

The mirrored `Fixtures/PersonalCloudQuota/quota-pause-v1.json` contains synthetic
representative cases, not a live captured exhaustion event. The typed error is
checked separately from edge headers.

## Durable pause and accounting

Each local profile has a private `personal-cloud-dispatch` directory with a
versioned JSON ledger per normalized Worker origin. It contains identity
association, observed/reset times, UTC-day counters and bounded retry state; no
task content, tokens or provider credentials. Atomic replacement and a shared file
lease coordinate local processes. The lease covers reservation and one HTTP send,
not token acquisition or callbacks into another client.

Health, device token/pair/revoke, mutations, changes/bootstrap, checkpoints,
provider bridge/catalog and optional initial-status requests share the policy.
The factory supplies it to each Worker HTTP client. Cloudflare control-plane
OAuth and resource management keep their separate transport. Pairing
and restart do not clear an existing origin pause. Local editing and independent
provider sync remain available.

Dispatches reserve mutation-envelope and HTTP-attempt counts separately before
network activity. Batch members, retries and uncertain outcomes consume allowance;
a crash does not refund a reservation. The budget applies to existing outbox
backlogs and fresh seeds, even without an initial-sync marker. It never rebuilds
envelopes, renumbers sequences or discards later edits to fit the day. The sender
selects the allowed ordered prefix, including a one-envelope batch when only one
slot remains.

The shared `dispatch-budget-v1.json` table defines **45,000 estimated write credits,
3,000 envelopes and 10,000 HTTP attempts per UTC day**. Task patches reserve 25
credits, task-source refreshes 16, and known single-record operations 50. Rich
creation adds 10 per label and 16 for an included source. Project deletion and tree
movement reserve the whole day because their fan-out is not bounded by envelope
size. Compound batches sum the exact transmitted prefix. Unknown operations and
operations exceeding the entire allowance produce a visible
`local_operation_budget_exceeded` hold with no automatic daily retry; their pending
envelopes remain intact. Failed attempts and retries consume their full charge.
Credits are conservative workload estimates, not actual D1 rows or account-wide
quota reservations. Validate these estimates against combined workload scenarios.

A quota or local budget pause suppresses subsequent Worker requests and reporting
loops until reset. Optional reports record daily exhaustion before swallowing their
failure; ordinary diagnostic failures remain optional. Failed storage retains an
in-memory brake and explicitly reports that restart protection was not saved.
Corrupt state fails closed. Lock contention is a bounded busy result, not corruption.

At reset, a serialized request resumes with small jitter. Transient failures use
bounded exponential backoff. The existing automatic sync timer resumes eligible
work when automatic sync is enabled; the policy never enables that preference.
When it is disabled, the user can continue with manual Sync after reset. Personal
Cloud settings and paused sync results show the exact UTC reset, attempted work
and remaining pending changes.

## Receipts and interrupted work

A sequential batch can return HTTP 200 with a confirmed receipt prefix followed
by the quota error. The transport records the pause and returns that body intact.
The sync client validates the full prefix, acknowledges those receipts, then
surfaces the pause. The unconfirmed suffix keeps its exact IDs, sequences and
canonical payloads. Invalid prefixes are not acknowledged. Credentials,
capabilities, initial seed, local task state and checkpoints remain governed by
their existing contracts; a quota error is not authority to bootstrap over pending
edits or replace the pairing.

## Measurement and limits

Catalog publication is change-only. A source timestamp alone does not republish a
full catalog record, even after six hours or a day. Catalog `source.refreshedAt`
means the last published facts observation, not latest provider contact. Actual
source content, availability, skip/adoption, choice or allowed legacy scope
assignment still publish with their new observation, under existing ownership and
stale-write guards. Existing non-null scope pinning remains enforced. Queued
catalog envelopes and FIFO identity are preserved. Unchanged scopes were already
change-only. Adopted task `source.refreshSnapshot` heartbeats still occur at
six-hour intervals; the catalog optimization does not suppress those updates.

Budget changes need measured D1 result metadata and request counts for
fresh upload per day/total, existing backlog catch-up, normal edits, six-hour
provider refreshes and browser idle. Use synthetic names/content/IDs at representative
population counts. Counts alone do not establish payload size, edit frequency or
which historical sources remain eligible. A cap must leave steady-state service
capacity above arriving work and enough margin to drain backlog; a growing queue
behind a low cap does not satisfy the objective.

Local D1 metadata is not live billing. Other devices and apps share the account's
allowance, so this per-profile policy does not reserve global account quota.
Validate live usage, upgrade recovery and packaged behavior separately before
release. Source-level checks do not replace those deployment requirements.
