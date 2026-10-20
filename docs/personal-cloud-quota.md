# Personal Cloud quota pause and dispatch budget

This client contract implements `quota-pause-v1` alongside the companion
repository's `docs/quota-pause-v1.md`. It keeps protocol 1, adds optional schema-26
current-catchup reporting while retaining schema-25 compatibility,
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
rollback does not replenish allowance. A future ledger day or a deadline more than
24 hours ahead is conservatively reanchored to today with today's counters spent;
it cannot keep dispatch disabled for multiple days after a large clock correction.
A resumed failed request pauses again.

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
movement use the guarded fanout contract below when supported; older Cloud
instances retain the conservative whole-day reservation. Compound batches sum the
exact transmitted prefix. Unknown operations and
operations exceeding the entire allowance produce a visible
`local_operation_budget_exceeded` hold with no automatic daily retry; their pending
envelopes remain intact. A channel-scoped maintainer command can authorize one exact
pending attempt; this explicit recovery path does not automatically retry it every
day. Failed attempts and retries consume their full charge.
Credits are conservative workload estimates, not actual D1 rows or account-wide
quota reservations. Validate these estimates against combined workload scenarios.

A confirmed Cloud quota, HTTP-attempt limit, retry backoff or unavailable storage
suppresses all Worker requests. Local write-credit/envelope exhaustion and an
oversized pending operation suppress mutation uploads; health, authentication,
downloads and checkpoint acknowledgement remain available within the request cap.
Successful reads do not clear an upload hold. Sync reports the blocked upload after
pulling, with exact pending envelopes and initial-catchup counts retained.

Optional reports record daily exhaustion before swallowing their failure; ordinary
diagnostic failures remain optional. Failed storage reports that restart protection
was not saved and re-probes after five seconds. Once storage works, malformed,
unreadable or newer-format ledgers are quarantined without discarding the evidence,
and a durable replacement conservatively spends today. A durable recovery marker
closes the crash window between quarantine and replacement. The memory brake clears
only after durable state is restored. Lock contention is a bounded busy result, not
corruption.

At reset, a serialized request resumes with small jitter. Transient failures use
bounded exponential backoff. The existing automatic sync timer resumes eligible
work when automatic sync is enabled; the policy never enables that preference.
When it is disabled, the user can continue with manual Sync after reset. Personal
Cloud settings and paused sync results show the exact UTC reset, attempted work
and remaining pending changes.

Caller/shutdown cancellation propagates without persisting failure backoff. A real
HttpClient timeout is classified outside its handler, where the original caller
token can be distinguished from HttpClient's linked timeout token. Reservations
already made remain consumed in both cases.

## Guarded fanout and explicit single-attempt recovery

Health may advertise `fanoutBudget: { protocolVersion: 1, requestHeader:
"x-openza-fanout-bound" }`. Only that verified capability enables scaled credits:
`100 + 12 × N` for each project deletion or tree move. `N` bounds live affected tasks
plus direct live child projects for deletion, and the live subtree including its
root for movement. The nonnegative safe-integer header is outside the immutable
canonical mutation body. The same bound applies separately to every fanout member
of a batch. No formula is clamped to the daily allowance; no new workspace size cap
replaces the existing response-size limit.

Desktop uses mutation-bound indirect-version metadata captured during local
authoring. When that evidence is absent or a bound was stale, a conservative
whole-workspace task/project count is used, including Cloud records retained after
local detachment. The Worker enforces the bound atomically against concurrent
growth. A retryable `409 fanout_bound_exceeded` preserves FIFO and triggers a pull,
then bounded backoff before a fresh estimate with the same mutation ID, sequence,
digest and payload. Accepted-receipt lookup precedes bound checks, so changing a
hint does not invalidate an already accepted retry. Every attempt is charged.
The paired `fanout-budget-v1.json` fixture records the formula and wire contract;
the original cost fixture retains legacy weights.

For an unknown or above-budget FIFO head, `openza cloud-budget status` reports its
ID and exact-envelope SHA256 and prints the explicit `allow-once` command. The
current channel and saved origin scope the permission; a later entry or changed
digest is rejected. Permission expires in 15 minutes and is durably consumed before
one single-envelope attempt, even if cancellation or an uncertain result follows.
That attempt adds a full day's write-credit charge to spending already recorded;
it can exceed the configured local daily write/envelope budget. It requires another
explicit decision to retry. It never bypasses confirmed Cloud quota, the HTTP cap, backoff or
failed durable storage. It sends no request itself; use Sync afterward. See the
[CLI contract](cli.md). The exemption is a maintainer tradeoff, not a guarantee that
arbitrary fanout fits a daily D1 allowance or the Cloud response-size limit.
It is excluded from the automatic daily-budget model and any "below 50%" result
for measured automatic workloads. Review the exact entry, fanout and current usage
before granting this exception. Neither the automatic model nor an exemption is
an account-global quota reservation or guarantee.

## Current catch-up status and Web bulk work

Initial-upload status is terminal and describes only the initial seed. It cannot
prove that a later backlog has drained. A separate optional health capability,
`catchupStatus: { protocolVersion: 1, path: "/sync/catchup-status",
staleAfterSeconds: 900 }`, enables authenticated reports containing only protocol
version, a durable monotonic report sequence, `catching_up`/`idle`, and exact
pending-envelope count. Reports run before upload and after passes/holds; unchanged
status has a five-minute heartbeat bound during normal sync. Old Cloud instances
receive no calls to this endpoint. Global dispatch gates still apply.

Desktop captures a durable backlog watermark for incomplete initial preparation or
seed, deferred recovery, prior-UTC-day pending work, an upload-budget hold, or
pending conservative credits exceeding one full daily allowance. Fresh normal
provider/note changes under that allowance do not trigger catch-up merely because
there are many envelopes. Once catch-up starts, it stays active until the entire
current pending queue is empty and initial/recovery work is complete; newer edits
cannot hide an unfinished backlog. Normal small fresh queues may remain `idle`.

The private report journal is separate from daily counters and keyed by origin,
instance and device. Each sequence is saved before HTTP. A stale report response
returns the current server sequence; the client persists that maximum and samples
fresh state at the next pass instead of replaying an old idle report. Corrupt
journals are preserved in quarantine and start conservatively as `catching_up`
until sequence recovery succeeds. Unwritable/unconfirmed metadata is visibly
unknown while downloads, local work and proven ordinary edits remain available;
no mutation identity is reset. On a negotiated Cloud, a triggered backlog needs a
current accepted `catching_up` report before its first upload. Failed, rejected or
unpersistable reports produce a recoverable upload-only `catchup_status_unavailable`
result for that pass. The next normal pass samples fresh state and retries; it does
not install a permanent quota hold. This prevents a previously fresh server idle
record from admitting Web bulk work while Desktop starts an unreported backlog.

Large new Web changes are deferred until the UTC day after Desktop catch-up
finishes. The server timestamps completion, preserves idle heartbeat history and
enforces its Web daily credit admission. Missing/stale status fails closed for bulk
admission. Ordinary Web edits and downloads remain available. This is measured
workload scheduling, not an account-global quota reservation or a guarantee for
arbitrary workspaces. Offline Desktop clients cannot prove fresh catch-up status.

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
