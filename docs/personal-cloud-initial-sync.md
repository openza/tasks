# Personal Cloud initial sync feedback

Cloud setup and initial task sync have separate outcomes. A saved pairing remains valid when task upload, download, or catch-up is interrupted. Retry the normal Sync command; another pairing code is unnecessary.

New connections record a durable initial-sync marker in local schema 10, scoped to cloud instance and paired device. The task seeding transaction freezes its final device sequence, including task creation, Spaces, projects, labels, fields, parent relationships and completion changes. Counts use **changes uploaded**, not task counts or estimated percentages. Counts remain unavailable while preparing, then derive from durable outbox acknowledgements within that frozen range. Later edits do not grow or reopen an already completed seed.

Initial sync completes only after the seed is acknowledged and the sync pass finishes downloading changes, acknowledging its checkpoint, and checking for unresolved deferred changes. The existing upload batch limits and automatic-sync cadence are retained. A paused or interrupted pass preserves progress for restart/retry. An older connection without an authoritative marker reports progress unavailable rather than inferring first-sync status from normal queued edits.

Desktop setup, the task header and Settings show the same state: preparing, uploading, downloading, complete, interrupted or blocked. Retry uses the existing serialized Sync path. The Windows host retains its current layout; the shared setup-service interface remains compatible.

## Sync efficiency

Provider refresh compares all safe source facts against the newest confirmed or queued snapshot, including receipt-acknowledged snapshots whose echo has not arrived. Identical facts do not allocate a mutation or device sequence. A durable local observation watermark rejects delayed observations after restart. Unchanged facts publish at most one six-hour freshness heartbeat with the actual provider observation timestamp; changes publish immediately. Source identity mismatches and deleted tasks cannot publish refreshes. Existing immutable journal entries are never rewritten.

Cloud schema 22 can advertise `capabilities.mutationBatch` protocol 1, path `/v1/tasks/mutations/batch`, maximum two mutations and 16,384 UTF-8 bytes for the entire wrapper. The client preserves the existing 100-change sync pass, sends consecutive persisted envelopes sequentially, and validates all returned prefix receipts before acknowledging any. Partial failures retain the failed suffix; lost or invalid responses retain the whole submitted batch for exact replay. Single delivery remains available when capability is missing, a wrapper cannot fit, or the advertised route returns 404/405/501. HTTP 400 validation failures retain pending edits and never trigger blind fallback.

The legacy synthetic 913-task/five-field fixture produces 4,565 mutations: 4,565 single POSTs or 2,283 two-item batch POSTs. With workspace support, the rich 913-task fixture plus one new Space and project produces 915 mutations: 915 single POSTs or 458 batch POSTs, with the same 915 durable acknowledgements and completion only after checkpoint catch-up. In 144 unchanged observations over twelve hours, task snapshots and the scoped provider catalog each publish two six-hour heartbeats; unchanged scope metadata publishes none. These are synthetic request-count measurements, without production CPU, SQL-cost, latency, or quota claims.

## Workspace and provider catalog compatibility

Schemas 23–24 advertise workspace protocol 1. Independent Space/project records carry metadata, versions and tombstones. The client downloads the reference baseline before first staging, preserves canonical creation timestamps across SQLite reads and restarts, and cannot restore deleted references from stale task relationship names. Rich task creation and patching preserve exact deadlines, scheduled start/end, duration, notes and local recurrence. Whole-tree moves preserve internal parents in one mutation; project deletion clears assignments and detaches immediate child projects. Each complete workspace change is applied atomically before advancing the checkpoint.

Atomic project deletion and task-tree moves record predicted dependent task/project versions locally beside the immutable outbox. Later edits use those versions across restarts and acknowledgement-before-echo; pending dependent overlays survive older complete workspace records. Workspace bootstrap retains active matching task rows and their device-only metadata and external links. Raw provider identities survive only when the incoming source integration and external identity hash match the local binding.

Editable Openza task planned/deadline/scheduled instants preserve protocol millisecond precision using compatible REAL epoch seconds for fractional values. Readers retain support for existing INTEGER seconds and milliseconds. Notes-only edits therefore preserve exact synced instants without rewriting existing databases or changing provider/reference timestamp tables.

Provider recurrence remains a source fact. Schema 11 moves the former adopted-source recurrence into source metadata once; subsequent local recurrence is independent. Source refresh cannot overwrite the local value. Both hosts retain provider recurrence in existing inspectors and repeating views, while provider date writeback checks the provider's recurrence.

Provider catalog protocol 2 adds opaque per-connection scopes, including connections with zero tasks. Only owned Todoist scopes can change the existing Todoist routing settings. Labels can be typed; project choices are opaque and scoped to that connection. Unknown, foreign, Microsoft, and historical unscoped routing requests never broaden to other providers. A known owned route can be deleted after its connection becomes unavailable. Scope/catalog suggestions are bounded to 32 labels and project choices. Raw provider identifiers, account binding, credentials and snapshot caches stay local; the approved safe source descriptor can contain its provider deep link.

Catalog actions record their local identity before cloud start, apply local adoption once, publish the owned rich task before resolving adoption, and retain ambiguous provider outcomes. A restarted filing action reconciles fresh provider state before confirmation and never blindly repeats the move. Cloud acceptance and provider completion are separate states.

The decoder accepts historical six-array workspace payloads and new seven-array payloads containing scopes. Catalog protocol 1 retains legacy browse/adopt/skip compatibility; new scope keys require protocol 2. An older cloud preserves incompatible queued envelopes and reports `upgrade_required` without lossy rewriting. The existing single-envelope fallback and immutable replay rules remain.

Schema 24 optionally advertises `workspacePaging` protocol 1. Baseline requests use `workspace=1&paged=1`, opaque device-bound cursors and one fixed checkpoint. Each page contains at most 100 records and 2 MiB of complete UTF-8 JSON. Desktop stages pages in private SQLite tables, with limits of 100,000 records, 128 MiB and 10,000 pages per attempt. Only a complete consistent baseline replaces the replica, in one transaction that rechecks pending local edits and preserves matching device-only metadata. Cancellation, failure, invalid cursors, duplicate records and exhausted limits preserve the current replica. A changed baseline restarts from the first page, at most three baseline attempts per Sync call. Interrupted staging is discarded on the next attempt; page counts never imply percentage completion.

Paged change feeds use explicit `hasMore` rather than treating a short page as completion. Each response is at most 2 MiB, and compound changes are never split or skipped. A failed or incomplete pull sends no checkpoint acknowledgement; a committed prefix is resumed on the next Sync. Unpaged oversized responses require an upgrade, while an individually oversized change fails explicitly. Historical endpoints and the per-pass change-page limit remain supported.

## Local bundle verification and release boundary

The combined source bundle is verified through `scripts/personal-cloud/stage-artifact.mjs` and `PersonalCloudArtifactDirectory.Load`, then copied into a Dev build with `-p:PersonalCloudArtifactDirectory=<verified-directory>`. Its Worker, static assets and all 24 migrations must match the manifest hashes and sizes. This verification neither deploys the Worker nor installs or launches a package.

The normal release artifact lock remains pinned to the existing preview.9 revision. A future authorized release must commit the combined Cloud source and update that lock to its actual revision, version and manifest checksum before claiming the combined behavior in a packaged release. Local source/artifact validation does not establish production or Windows acceptance.

## Optional cloud report

The desktop sends `POST /sync/initial-status` with its existing `Bearer` device access token. Cloud schema 21 adds this endpoint; health checks accept schemas 17–24. Requests contain:

```json
{"protocolVersion":1,"reportSequence":1,"state":"uploading","acknowledged":0,"total":400}
```

- `reportSequence`: durably reserved increasing positive JavaScript-safe integer per paired device, before sending. Reports are serialized.
- `state`: `uploading`, `complete`, or `blocked`. Preparing/download phases use `uploading`; interruption uses `blocked` with `errorCode: "interrupted"`.
- `acknowledged` and `total`: either both null while preparing, or known nonnegative change counts. `complete` requires known equal counts and completed catch-up.
- `errorCode`: only with `blocked`; one of `offline`, `unauthorized`, `cloud_unavailable`, `conflict`, `interrupted`, or `unknown`.

Reports contain no task titles, payloads or raw exceptions. Active uploads send a 30-second heartbeat and report phase transitions. Cloud owns the 120-second stale-upload threshold and server timestamps. Status reporting is optional: timeouts, transport failures and HTTP errors (including old-server 400/404/405/501 responses) never fail task sync or discard intents. A later normal Sync retries reporting a completed initial state if its final report was lost. Browser completion therefore can lag local completion when reporting is unavailable.

Synthetic unit/headless checks cover full seed ranges across multiple capped passes, restart, retries, pairing/reinstall with existing cloud tasks, later edits, zero-task catch-up, deferred changes, conflicts, status endpoint failures and compatible health/pair/upgrade schemas. Real cloud/device acceptance and Windows builds require their separate authorized environments.
