# Openza Personal Cloud Architecture and Delivery Plan

Status: canonical product and delivery plan

Scope: Openza Personal Cloud umbrella; Tasks is the first supported product

Last updated: 2026-09-14

Audience: product, engineering, security, release, and support

This document is the source of truth for the planned Openza Personal Cloud capability. It refines, but does not replace, the current local application architecture in [`architecture.md`](architecture.md). When an implementation detail conflicts with a product contract in this document, stop and resolve the conflict here before shipping.

## 1. Executive decision

Openza remains local-first. Personal Cloud is an optional sync and Web companion that a user deploys into their own Cloudflare account. It is not a hosted Openza account system and is never required to use the Windows or Linux desktop apps.

The first release uses one Cloudflare Worker, Workers Static Assets, and one D1 database per Personal Cloud deployment. It does not require Durable Objects, Queues, R2, Workflows, or D1 read replicas. Those products may be reconsidered only after measured need.

The Web app is online-first and talks directly to the Worker. Once Personal Cloud is provisioned, Web remains useful when every desktop is offline. Desktop devices synchronize opportunistically and retain their complete local database and normal offline behavior.

Phase 1 supports Openza Tasks only and synchronizes all Openza Spaces and all rows adopted into the Openza `tasks` collection. Every synchronized row is an Openza task; some retain an optional source link after adoption. Future Openza products use separate product modules, tables, operations, and migrations inside the same Personal Cloud deployment. Desktop hosts remain responsible for Todoist, Microsoft To Do, and future external-provider integrations. Web cannot connect, configure, refresh, browse, or disconnect providers.

Provider-owned fields are read-only on Web. Openza-owned fields can be edited on Web. Complete and reopen are permitted semantic commands even for provider-linked tasks; a desktop integration host eventually writes those commands to the provider. A successful cloud mutation means that Openza accepted the change, not necessarily that the external provider has acknowledged it.

## 2. Product contract

### 2.1 Invariants

The implementation must preserve these invariants:

1. **Local-first desktop:** creating, viewing, editing, searching, completing, importing, exporting, and provider syncing continue to work without Personal Cloud or an internet connection.
2. **Optional user-owned cloud:** each deployment is controlled and billed by the user's Cloudflare account. Openza does not hold the task database or a standing Cloudflare administration credential.
3. **Online-first Web:** Web requires its deployed Worker for task data. It may cache its application shell, but it is not a second offline-first database in Phase 1.
4. **Desktop-independent reads and Openza edits:** Web reads and writes D1 through the Worker even if no desktop is online.
5. **Desktop-hosted provider integration:** provider tokens and provider APIs remain on Windows/Linux desktop. They are never copied to D1 or exposed to Web.
6. **Explicit field authority:** the system does not resolve conflicts by blindly copying a whole task object. Every mutation names an allowed semantic operation and affected fields.
7. **All Spaces, no implicit routing:** all Spaces, including archived Spaces, synchronize. Personal Cloud does not invent Work/Personal routing or expose one Space accidentally because it was selected on a desktop.
8. **Adopted tasks only:** unadopted, ignored, and missing provider intake rows do not synchronize. Adopting a source task creates the Openza wrapper that enters scope.
9. **Least user choice:** setup asks only for decisions that cannot safely be derived. Defaults, resource names, migrations, retry behavior, and sync scheduling are managed by Openza.
10. **Reversible rollout:** Personal Cloud can be disabled or disconnected without deleting or locking the local desktop database.

### 2.2 User-visible promises

- Turning on Personal Cloud does not move the authoritative local database out of the desktop app.
- A first sync uploads the in-scope Openza model; it does not upload provider credentials, unadopted provider intake, local restore points, logs, or unrelated app settings.
- Web clearly marks provider-sourced values as read-only and shows provider completion/reopen as pending until a desktop confirms the provider write.
- A revoked device cannot obtain new changes or submit new mutations.
- Deleting an item synchronizes as a tombstone long enough for offline devices to observe it.
- Sync errors do not silently discard local edits. Desktop queues them durably and Web reports whether an operation was accepted, rejected, or still awaiting external-provider confirmation.

### 2.3 Phase 1 non-goals

- A multi-user workspace, sharing, collaboration, assignments, or organization administration.
- An Openza-operated SaaS control plane or hosted task database.
- Provider setup, provider browsing, intake adoption, routing, or provider credential storage on Web.
- Synchronizing every table from the desktop SQLite file.
- Full offline task editing in Web.
- Attachments or binary task content.
- Background jobs that require Queues, Workflows, or Durable Objects.
- Live collaborative cursors, presence, or sub-second push.
- D1 read replicas, R2, custom domains, or Cloudflare Access as baseline requirements.
- A methodology-specific workflow or cloud-only task field.

## 3. Current Openza model and Phase 1 data scope

Personal Cloud is a semantic replica of the supported Openza domain, not a byte-for-byte copy of the desktop SQLite database. Desktop schema migrations and cloud schema migrations are independent.

### 3.1 Included domain entities

| Entity | Phase 1 scope | Notes |
| --- | --- | --- |
| Spaces | All rows, including archived | Preserve ID, name, color, icon, order, archive state, and timestamps. |
| Projects | Openza-owned projects required by in-scope tasks, plus empty Openza-owned projects | Preserve hierarchy, lifecycle, display fields, order, favorite state, and Space. Provider projects remain source context, not editable Openza projects. |
| Tasks | Every adopted row in the Openza `tasks` collection | Every row is an Openza task. A task may have no external source or may retain an optional safe source descriptor. Preserve hierarchy, completion/workflow state, Openza planning fields, safe source display fields, timestamps, and revision lineage. |
| Labels | Openza-owned labels attached to in-scope tasks, plus unattached Openza-owned labels | Preserve label identity and presentation fields. Provider-label catalog rows are not uploaded merely because a provider returned them. |
| Task-label links | Links between included tasks and labels | Mutated as set membership, not by replacing an opaque task document. |
| Safe source descriptor | Only fields needed to explain an adopted wrapper | Integration kind, provider connection display alias, external identity hash/opaque ID as required, source URL, title/description/project display snapshot, source dates, source priority, recurrence, and external confirmation state. |
| Device/provider command state | Cloud-specific metadata | Tracks pending/confirmed/rejected completion and reopen without storing provider credentials. |

“All adopted Openza tasks” means every row in the Openza task collection. Adoption creates an Openza-owned `tasks` row with `integration_id = openza_tasks`; some rows additionally retain `source_*` fields linking them to an external origin. Personal Cloud must not classify those rows as a separate Web task type. It excludes raw `provider_source_items` in `not_adopted`, `ignored`, or `missing` state, and the cloud does not reproduce the provider inbox.

### 3.2 Excluded local data

The initial sync allowlist excludes:

- provider access/refresh tokens and credential-store values;
- `provider_connections.settings`, tokens, account keys, and provider sync tokens;
- raw or unadopted `provider_source_items` and full opaque `snapshot_json` payloads;
- provider project/list and label catalogs except safe display snapshots attached to adopted tasks;
- `sync_routes`, provider-to-provider mappings, provider operations, and provider run logs;
- local restore points, OneDrive backups, exports, file paths, notification state, window state, theme, density, and device-local preferences;
- time entries and task enhancements until their product behavior and field authority are separately accepted;
- external links other than a safe source link needed to identify an adopted wrapper;
- secrets, diagnostic logs, crash dumps, and personal filesystem metadata.

The encoder must construct an explicit transport DTO. It must never serialize a database row or `TaskItem` wholesale and then attempt to remove sensitive properties.

### 3.3 Field ownership matrix

| Field group | Local task | Adopted provider wrapper | Web behavior | Conflict authority |
| --- | --- | --- | --- | --- |
| Openza ID, Space, parent | Openza-owned | Openza-owned | Editable where the existing app permits it | Latest accepted semantic mutation, subject to invariants |
| Openza project | Openza-owned | Openza-owned | Editable | Openza mutation order |
| Workflow status | Openza-owned | Openza-owned | Editable | Openza mutation order |
| Local priority | Openza-owned | Openza-owned | Editable | Openza mutation order |
| Planned/deadline/scheduled fields | Openza-owned | Openza-owned unless a future provider route explicitly owns a field | Editable; UI distinguishes any source snapshot | Openza mutation order |
| Duration, local recurrence, notes, local metadata | Openza-owned | Openza-owned | Editable when exposed | Openza mutation order |
| Labels | Openza-owned | Openza-owned | Editable | Set operations per label link |
| Completion state | Openza-owned | Shared semantic command with provider acknowledgement | Complete/reopen allowed | Openza intent wins locally; provider result determines external confirmation |
| Provider title/description/project/list/priority/dates/recurrence | Not applicable | Provider-owned source snapshot | Read-only | New provider snapshot from a desktop integration host |
| Provider source identity and URL | Not applicable | Provider-owned identity | Read-only | Desktop integration host; immutable identity after link creation |
| Provider credentials/configuration | Not applicable | Desktop/device-owned secret | Not present | Never enters Personal Cloud |

If an adopted wrapper currently displays provider title or description as its primary content, Web renders the source value but does not edit it. A future product decision may add an explicitly Openza-owned title override; it must not silently change the provider-owned field contract.

## 4. Target architecture

```text
                    user's Cloudflare account
              ┌─────────────────────────────────┐
 Browser ─TLS─► Worker API + auth ───────────────┼──► D1
    ▲         │        │                        │    domain state
    │         │        └── Workers Static Assets│    mutation log
    └─────────┤             (Web shell)          │    devices/sessions
              └───────────────▲─────────────────┘    checkpoints/tombstones
                              │ TLS sync protocol
                ┌─────────────┴─────────────┐
                │                           │
       Windows desktop              Linux desktop
       local SQLite                  local SQLite
       transactional outbox          transactional outbox
       provider credentials          provider credentials
                │                           │
         Todoist / Microsoft To Do / future providers
```

### 4.1 Cloud components

**Worker API**

- Authenticates browsers and paired desktop devices.
- Validates protocol and schema compatibility.
- Accepts typed semantic mutations with idempotency keys and preconditions.
- Applies domain changes and appends an ordered change record in the same D1 transaction/batch.
- Serves paginated snapshots and incremental changes.
- Enforces field ownership and rejects provider-management calls.
- Exposes authenticated health, device, recovery, diagnostics, and version endpoints.

**Workers Static Assets**

- Serves the versioned Web application from the same deployment.
- Uses an asset binding or asset-first routing so immutable assets bypass application code when appropriate.
- Applies strict cache rules: fingerprinted assets are long-lived; HTML and service-worker metadata revalidate.
- Contains no account tokens, instance secrets, provider credentials, or environment-specific privileged configuration.

**D1**

- Stores the normalized cloud projection, mutation/change ledger, tombstones, idempotency receipts, checkpoints, devices, sessions, recovery state, and migration history.
- Uses prepared statements and bounded batches. A domain write and its change-log record commit atomically.
- Uses one logical writer path through the Worker. Phase 1 does not depend on read replicas or cross-database transactions.

### 4.2 Why the initial stack stays small

One Worker plus D1 is sufficient for a single-user workload when writes are short, paginated, and transactional. Adding coordination products before evidence would create more provisioning permissions, failure modes, cost explanations, and upgrade paths.

Reconsider the baseline only when telemetry or a reproducible test proves a need:

| Candidate | Adoption trigger |
| --- | --- |
| Durable Objects | D1 transaction ordering cannot meet a demonstrated concurrent-write invariant, or a real-time connection coordinator becomes necessary. |
| Queues | Reliable work must continue after the request and cannot be represented as client pull/acknowledgement. |
| Workflows | A durable multi-step operation with waits/retries cannot be safely resumed using D1 state. |
| R2 | Supported binary attachments or exports exceed the appropriate D1/static-asset design. |
| D1 read replicas | Measured geographically distributed read latency and consistency requirements justify them. |

## 5. Cloud data design

### 5.1 Logical tables

Exact SQL belongs to implementation migrations. The logical schema is:

- `instance`: singleton instance ID, created time, protocol range, schema version, setup/recovery state.
- `devices`: device ID, name, kind, credential/public-key metadata, created/last-seen/revoked times, last acknowledged change sequence.
- `web_credentials`: passkey credential metadata and sign counters; no private keys.
- `sessions`: hashed opaque session/refresh tokens, expiry, rotation lineage, revocation.
- `pairing_grants`: hashed one-time grants, expiry, attempts, requested device metadata, consumed time.
- `recovery_credentials`: versioned salted verifier, rotation time, usage state; never plaintext recovery material.
- `spaces`, `projects`, `tasks`, `labels`, `task_labels`: normalized cloud projection with stable Openza IDs, entity versions, last mutation IDs, and deletion state.
- `source_descriptors`: safe provider snapshot for adopted wrappers, separated to make read-only enforcement explicit.
- `external_commands`: completion/reopen delivery state for provider-linked tasks.
- `mutations`: immutable receipt keyed by instance and mutation ID, actor/device, operation, outcome, affected entity/version, and timestamps.
- `changes`: monotonically increasing cloud sequence, entity kind/ID, operation, entity version, mutation ID, and compact payload or hydration marker.
- `tombstones`: entity kind/ID, deletion sequence/time, prior parent scope, and retention/compaction eligibility.
- `checkpoints`: per-device/bootstrap checkpoint state when a dedicated row is needed beyond `devices`.
- `schema_migrations`: migration ID, checksum, applied time, and application build.

Provider source JSON is transformed into explicit columns or versioned safe JSON. Arbitrary provider payloads are not stored.

### 5.2 Identity and time

- Existing Openza entity IDs remain stable across desktop and cloud. Personal Cloud must not remap a task because it moved Spaces or projects.
- New Web-created entities use collision-resistant sortable IDs generated by the client or Worker; the final algorithm is fixed in the protocol specification before implementation.
- Every submitted mutation has a globally unique `mutation_id`, stable across retries.
- A desktop outbox row also has a local monotonic sequence for debugging and preservation of same-device intent order.
- The cloud assigns a strictly increasing `change_seq` when accepting a mutation. This is the synchronization cursor and ordering authority; wall-clock timestamps are display/audit data only.
- Each entity carries an integer `entity_version` incremented for every accepted entity change.
- Timestamps are UTC instants. Date-only fields remain ISO calendar dates and are never shifted through a timezone conversion.

## 6. Sync protocol

### 6.1 Authority model

There is no single authoritative database for every concern:

- Desktop SQLite is authoritative for offline availability and unsent local intent.
- D1 is authoritative for the accepted Personal Cloud change order and the Web-visible synchronized projection.
- A desktop integration host is authoritative for fetching and transforming external-provider snapshots.
- The external provider is authoritative for its provider-owned fields and final acknowledgement of a provider command.
- The protocol's ownership table is authoritative when deciding whether a mutation is legal.

“Last write wins” based on device time is prohibited. The cloud serializes accepted semantic mutations, but it cannot overwrite provider-owned fields merely because a Web mutation arrived later.

### 6.2 Semantic mutation envelope

Every push uses a versioned envelope conceptually containing:

```json
{
  "protocolVersion": 1,
  "product": "tasks",
  "instanceId": "...",
  "deviceId": "...",
  "mutationId": "...",
  "deviceSequence": 42,
  "operation": "task.setWorkflowStatus",
  "entityId": "...",
  "baseEntityVersion": 7,
  "payload": { "workflowStatus": "next" },
  "clientCreatedAt": "..."
}
```

Allowed operations are narrow and versioned, for example:

- `space.create`, `space.rename`, `space.setAppearance`, `space.reorder`, `space.archive`, `space.restore`, `space.delete`;
- `project.create`, `project.updateOpenzaFields`, `project.move`, `project.archive`, `project.restore`, `project.delete`;
- `task.create`, `task.updateOpenzaFields`, `task.move`, `task.setParent`, `task.complete`, `task.reopen`, `task.cancel`, `task.delete`;
- `task.addLabel`, `task.removeLabel`;
- `label.create`, `label.update`, `label.delete`;
- `source.refreshSnapshot`, submitted only by a capable desktop integration host;
- `externalCommand.confirm`, `externalCommand.reject`, submitted only by the device that attempted provider delivery.

Whole-entity “upsert whatever the client sent” is not a public protocol operation. Each handler validates actor capability, field ownership, referential integrity, expected base version, payload size, and enum values.

### 6.3 Idempotency and ordering

1. The client writes an outbox mutation before considering a local edit syncable.
2. It retries with the same `mutation_id` until it receives a durable receipt.
3. D1 enforces uniqueness on `(instance_id, mutation_id)`.
4. A duplicate with an identical canonical request returns the original result.
5. Reuse of a mutation ID with different content returns a security/conflict error and is logged.
6. Same-device mutations must normally arrive in `device_sequence` order. A gap returns `sequence_gap` with the expected sequence; a harmless duplicate returns its receipt.
7. Cloud acceptance allocates `change_seq`, applies state, records the mutation receipt, and appends changes atomically.
8. A response includes the accepted entity version and latest relevant change sequence. Losing the HTTP response is safe because retry returns the receipt.

### 6.4 Pull checkpoints

- `GET /sync/changes?after=<change_seq>&limit=<bounded>` returns ordered changes and `next_checkpoint`.
- The server caps both record count and encoded response size; clients continue until `has_more=false`.
- A device advances its durable local checkpoint only after applying the whole page in a local SQLite transaction.
- It then asynchronously acknowledges the checkpoint to the Worker. Missing acknowledgements affect compaction, not correctness.
- A response can return `checkpoint_expired` with a current bootstrap descriptor when the requested history has been compacted.
- Pull-before-push is the default cycle so the client rebases pending semantic mutations on current entity versions. Push results may require another pull.

### 6.5 Desktop transactional outbox

Desktop changes and outbox insertion occur in the same SQLite transaction:

1. Apply the user's local semantic action.
2. Increment the local revision and record the base cloud version last seen.
3. Insert a canonical outbox envelope with mutation ID and device sequence.
4. Commit once.

The sync worker sends ordered pending rows, records cloud receipts, applies inbound changes, and marks acknowledged outbox rows in transactions. Process termination at any step is recoverable.

Provider-linked completion/reopen has two durable legs:

1. The Openza semantic mutation synchronizes among desktop, D1, and Web.
2. A capable desktop maintains or creates its provider outbox operation and attempts the external write. It later submits confirmation or rejection plus a refreshed source snapshot.

The existing completion outbox is a behavioral foundation, but implementation should converge on one transactional mutation/outbox abstraction rather than create unrelated retry systems. A desktop that is not configured for the relevant provider must not claim or reject the command.

### 6.6 Tombstones and deletion

- Deletes create tombstones; they do not immediately erase identity from the synchronization history.
- Tombstones cascade semantically. Deleting a Space or project follows explicit existing-product rules rather than relying on database foreign-key cascades to invent behavior.
- An offline device applying a tombstone removes or marks the entity locally and cancels stale unsent edits unless a defined restoration flow applies.
- A stale client cannot recreate a deleted entity through an ordinary update. Restoration requires an explicit `restore` mutation within the supported window or a new entity ID.
- Tombstones include enough former scope to remove cached list membership without disclosing excluded payloads.

### 6.7 Bootstrap and re-bootstrap

Initial/bootstrap sync is a consistent, paginated snapshot with a high-water mark:

1. Client requests a bootstrap session.
2. Worker captures or returns a snapshot boundary `snapshot_seq`.
3. Client downloads pages in dependency order: Spaces, projects, labels, tasks/source descriptors, task-label links, tombstones if applicable.
4. Each page is checksummed and resumable. The client stages it without replacing live data page by page.
5. After validating all pages, desktop merges the projection in a SQLite transaction or an explicitly resumable staged process.
6. Client then pulls changes after `snapshot_seq` and saves the resulting checkpoint.

For the first desktop upload into an empty Personal Cloud instance, the initiating device submits an idempotent bootstrap manifest and bounded batches. The instance remains `initializing` and unavailable for ordinary Web mutation until the manifest is complete and referential checks pass. Retrying cannot duplicate entities.

The first-upload collision policy is deterministic:

- An empty instance accepts one bootstrap owner.
- A non-empty instance never accepts a second “replace cloud” bootstrap automatically.
- Pairing another existing desktop performs normal merge/sync and surfaces identity collisions; it does not offer a casual overwrite choice.

### 6.8 Compaction

- Change history is retained until every non-revoked device has acknowledged beyond it, plus a defined safety interval.
- Devices inactive beyond the supported offline window become `rebootstrap_required`; they no longer block compaction.
- Compaction materializes the current entity projection, retains mutation receipts for the idempotency window, retains tombstones for the deletion window, and removes eligible older change payloads in bounded maintenance calls.
- Phase 1 maintenance is request-driven and resumable in D1. It may run during authenticated traffic or a simple Cron Trigger if later required; it does not justify Queues or Workflows by itself.
- Retention durations must be configurable defaults documented in the release, not scattered constants.

### 6.9 Conflict handling

Conflicts are classified, not flattened into one generic error:

| Conflict | Resolution |
| --- | --- |
| Different Openza fields changed from same base | Merge when operations commute; preserve both and increment version once per mutation. |
| Same scalar Openza field changed | Cloud acceptance order wins only if the later mutation explicitly rebases or policy allows; otherwise return `stale_field` with current value/version for deliberate retry. |
| Label membership changes | Add/remove set operations commute; repeated action is idempotent. |
| Reorder operations | Use stable ordering keys and a list/base revision; stale reorder returns current ordering for rebase. |
| Edit versus delete | Tombstone wins; ordinary edit cannot resurrect. |
| Move to deleted parent/Space/project | Reject with a typed referential conflict; client keeps local intent visible. |
| Provider snapshot versus Openza-owned edit | Merge by field ownership; neither overwrites the other. |
| Provider snapshot versus Web edit to provider-owned field | Web mutation is rejected before write. |
| Complete then reopen before provider delivery | Coalesce provider delivery to current desired state while preserving mutation history. |
| Provider rejects completion/reopen | Keep Openza intent and mark external state `rejected` with safe reason; do not silently flip the task. User can retry or reconcile from desktop. |

The Web and desktop UIs should usually resolve conflicts automatically by refetching and replaying a still-valid semantic intent. They ask the user only when two meaningful values cannot be preserved or the requested parent no longer exists.

## 7. Online-first Web behavior

### 7.1 Supported experience

Web Phase 1 supports:

- authenticate, lock, sign out, recover, and manage paired devices;
- browse all Spaces and the familiar task smart-list projections supported by cloud data;
- create local Openza tasks, projects, and labels;
- edit Openza-owned task/project/label fields;
- move tasks between Spaces and Openza projects with existing invariants;
- complete, reopen, cancel, and delete within the ownership contract;
- view source identity and provider snapshots for adopted wrappers;
- see `pending`, `confirmed`, or `rejected` external completion/reopen state;
- show last cloud acceptance and last provider confirmation separately.

Web does not display provider-connect buttons or imply that a browser can refresh a provider. If a provider snapshot is old, it says that a desktop integration host has not refreshed it recently.

### 7.2 Connectivity and caching

- Static assets may be cached for fast launch.
- Task data is fetched from the Worker and is not treated as durable offline state.
- A short-lived in-memory or browser cache may keep the last rendered screen, but the app must label it stale and disable mutations when the Worker cannot confirm them.
- Do not queue Web edits indefinitely in browser storage in Phase 1. This avoids a second offline conflict engine and false “saved” states.
- Network errors preserve the draft in the current form long enough to retry, but navigation warns before discarding it.
- Every mutation uses a client-generated idempotency key so a browser retry is safe.

### 7.3 External-provider state

For an adopted provider task, Web shows two states when needed:

- **Openza state:** current completion and Openza planning/workflow fields, immediately synchronized through Personal Cloud.
- **Provider confirmation:** `not_required`, `pending_desktop`, `attempting`, `confirmed`, or `rejected`.

Web must not claim “Completed in Todoist” or equivalent until a desktop confirms the external write or a later provider snapshot proves it.

## 8. Provisioning and lifecycle

### 8.1 Resource topology

Each Personal Cloud instance uses deterministic, collision-safe resource names and tags for:

- one Worker deployment containing API code and static assets;
- one D1 database bound to that Worker;
- Worker secrets for token signing/verification and recovery verification pepper;
- an optional route/custom domain only when the user explicitly configures one later.

Production and preview/dev resources must never share a D1 database. Local development uses local bindings or an explicitly named non-production remote environment.

### 8.2 Technical-preview setup: Connect Cloudflare

The technical preview exercises the same Desktop-led **Connect Cloudflare** path intended for public use. Local tests replace Cloudflare HTTP responses with controlled fakes, but do not introduce a second setup UX or a developer-only pairing workflow. A reviewed “Deploy to Cloudflare” template may remain an advanced recovery or contributor fallback; it is not a prerequisite or the primary preview path.

The preview flow must:

1. Show the exact repository revision and resources to be created.
2. Open Cloudflare OAuth from Openza Desktop and ask the user to select an account.
3. Create the Worker, static assets deployment, D1 database, bindings, and generated instance secrets.
4. Apply schema migrations once and verify an authenticated setup endpoint.
5. Pair the initiating desktop automatically, then open the instance URL and one-time passkey bootstrap without exposing secrets in logs or build output.
6. Record deployment version and migration version in the instance.

Technical-preview documentation must clearly state permissions, user-owned billing/quota implications, recovery limitations, update procedure, and how to remove the resources. It must not market this path as public readiness until tested across new and existing Cloudflare accounts.

### 8.3 Public setup: Connect Cloudflare OAuth

Public release replaces the template-first experience with a guided **Connect Cloudflare** flow using Cloudflare's OAuth 2.0 Authorization Code flow with PKCE for a public client. No static client secret may be embedded in Openza desktop, a browser bundle, or the open-source repository.

The intended flow is:

1. User chooses “Set up Personal Cloud” in Openza.
2. Openza generates a fresh PKCE verifier/challenge and state/nonce, then opens Cloudflare consent.
3. User selects the Cloudflare account and grants the minimum scopes needed to create/update the Worker, D1 database, bindings, and secrets.
4. The public client exchanges the authorization code with the verifier.
5. The provisioning coordinator creates or adopts only resources carrying the matching Openza instance identity.
6. It deploys a pinned, signed/versioned Openza artifact, migrates D1, performs health checks, and pairs the initiating desktop.
7. It discards the provisioning access token after setup. Refresh-token retention is avoided unless an accepted upgrade design proves it necessary.
8. Future upgrades request a short-lived Connect Cloudflare authorization again, or use a narrowly scoped, user-approved alternative documented at that time.

OAuth account access and Personal Cloud app authentication are separate systems. A Cloudflare OAuth token provisions infrastructure; it is not a browser session for the task app. The deployed Worker must not be able to use a forgotten provisioning token to administer the user's Cloudflare account.

Before public release, verify the exact current Cloudflare OAuth scopes, public-client review requirements, redirect URI rules, token revocation behavior, and API coverage against official documentation. The current direction is grounded in Cloudflare's public-client requirement for Authorization Code with S256 PKCE.

### 8.4 Upgrade and uninstall

- An upgrade computes a plan, shows version and migration impact, deploys code first when backward compatible, runs forward-only D1 migrations, verifies health, and only then marks the instance current.
- A failed deployment keeps the last healthy Worker version available where Cloudflare supports rollback; a failed migration stops rather than continuing with incompatible code.
- Disconnecting one desktop revokes only that device and leaves its local database intact.
- Disabling Personal Cloud locally stops sync without deleting the cloud instance.
- “Remove Personal Cloud” is a separate destructive flow that names the Worker and D1 database, offers an export, requires explicit confirmation, and verifies deletion. It is never part of ordinary uninstall.

## 9. Authentication, pairing, revocation, and recovery

### 9.1 Trust model

Each deployment is single-owner in Phase 1. The owner may have multiple browser credentials and desktop devices. Authorization is capability-based:

- `owner_web`: normal Web actions plus device/recovery administration;
- `desktop_sync`: normal Openza sync;
- `provider_bridge`: provider snapshot and provider-command acknowledgement for integrations configured on that device;
- `bootstrap`: one-time initialization only.

The Worker rejects an operation outside the actor's capability even if its payload is otherwise valid.

### 9.2 First owner setup

Provisioning returns a short-lived, one-use bootstrap grant through a protected channel. On first visit:

1. Verify the grant and deployment/instance identity.
2. Register at least one passkey for Web sign-in.
3. Generate a high-entropy one-time recovery code and show it once with explicit save/print guidance.
4. Store only the versioned verifier needed to validate the recovery code.
5. Consume the bootstrap grant and make the setup route unusable.

A recovery code is not a normal daily password. Rate limits, attempt counters, generic errors, and secret rotation protect it.

### 9.3 Desktop pairing

The desktop that initiates Connect Cloudflare is paired automatically during provisioning. The manual flow below is for an additional desktop, a replacement desktop, or re-pairing after revocation; it is not an extra first-run step.

The default path minimizes decisions:

1. In authenticated Web, choose “Pair a device.”
2. Worker creates a short-lived one-use grant and displays a QR code plus a manually enterable code.
3. Desktop sends the grant, instance URL, device name, and its requested capabilities over TLS.
4. Worker binds the grant to a new device ID and issues a random device refresh credential or registers a device public key, according to the final protocol security review.
5. Desktop stores its credential only in Windows Credential Locker or Linux Secret Service.
6. Worker consumes the grant and returns instance/protocol metadata.

Pairing grants are stored hashed, expire quickly, have strict attempt limits, cannot be reused, and reveal no task data before exchange succeeds. Device names are display labels, not authorization identifiers.

### 9.4 Sessions and request protection

- Browser authentication uses secure, `HttpOnly`, `SameSite` cookies with short-lived sessions and rotation. State-changing endpoints require origin validation and CSRF protection.
- Desktop uses a renewable device credential to obtain short-lived access. Tokens are audience- and instance-bound; long-lived opaque credentials are stored hashed server-side.
- Revocation is checked often enough that a short access-token lifetime bounds residual access. High-risk actions always check current device/session state.
- Requests carry a request ID; sensitive tokens never appear in URLs, logs, analytics, or error bodies.
- Pairing, login, recovery, export, and mutation endpoints have per-instance and per-source abuse limits with conservative false-positive handling.

### 9.5 Revocation

- Owner Web can list device name, kind, capability, created time, and last seen time.
- Revoking a device invalidates its refresh credential and sessions immediately; short-lived access expires within the documented bound.
- Revocation does not erase the device's already accepted mutations.
- A revoked device rejoining requires new pairing and, if its checkpoint was compacted, re-bootstrap.
- Revoking the last owner Web credential requires recovery-code confirmation or a separately defined break-glass flow.

### 9.6 Recovery

Recovery cases are explicit:

| Loss | Recovery path |
| --- | --- |
| Browser session lost | Sign in with a registered passkey. |
| One passkey lost | Use another passkey, then remove the lost credential. |
| All passkeys lost, recovery code available | Validate recovery code, revoke Web sessions, register a new passkey, and rotate the recovery code. |
| Desktop credential lost | Revoke that device from Web and pair it again. Local data remains local. |
| D1 data damaged | Use a tested D1 recovery/export procedure, then verify schema and change-log continuity before reopening writes. |
| All app credentials lost but Cloudflare account remains | Use a deliberately documented owner re-bootstrap procedure that requires Cloudflare control and rotates instance secrets; technical preview may require a guarded CLI command. |
| Cloudflare account lost | Openza cannot recover the cloud instance. A desktop's local data remains usable and can seed a new instance. |

Recovery must not introduce an Openza-held master key or backdoor. Public release is blocked until the all-app-credentials-lost path is tested and understandable by a non-developer.

## 10. Privacy and threat model

### 10.1 Privacy posture

- Task content lives in the user's local databases and their Cloudflare D1 database.
- Openza infrastructure does not receive task payloads as part of normal synchronization or provisioning.
- Cloudflare processes Worker traffic and stores D1 data under the user's account; setup explains this plainly and links to current Cloudflare terms.
- Operational logs use request IDs, versions, counts, latency, error classes, and hashed identifiers. They exclude titles, descriptions, notes, labels, URLs, provider IDs, tokens, mutation payloads, and recovery material.
- Optional diagnostics export is user-initiated, previewable, redacted, and saved locally before sharing.
- No third-party analytics, advertising SDK, session replay, or crash upload is enabled by default in Web.

### 10.2 Threats and mitigations

| Threat | Primary mitigations |
| --- | --- |
| Stolen browser session | Secure cookies, short lifetime, rotation, revocation, passkeys for reauthentication, no tokens in local storage. |
| XSS stealing task data | Strict CSP, no inline script/eval, dependency review, output encoding, Trusted Types where supported, same-origin API, minimized third-party code. |
| CSRF | SameSite cookies, Origin/Referer validation, explicit anti-CSRF token for state changes. |
| Stolen desktop credential | OS credential store, hashed server verifier, short-lived access, device revocation, least capability. |
| Replayed mutation | TLS, instance/audience binding, unique mutation ID, device sequence, expiry/nonces where applicable, idempotency receipt. |
| Pairing-code guessing | High entropy, short expiry, attempt limits, one-time consumption, hashed storage, generic error. |
| Enumeration | Opaque IDs, uniform unauthorized/not-found responses, rate limiting, no public list endpoints. |
| Malicious/stale client editing provider fields | Server-side operation allowlist and field ownership; never trust disabled UI controls. |
| Compromised Cloudflare account | User-visible deployment history, least OAuth scopes, discarded provisioning token, documented Cloudflare account security; acknowledge that account control is ultimate control. |
| Compromised deployment/update artifact | Reproducible/pinned artifacts, checksums/signatures, protected release pipeline, dependency/SBOM review, no unreviewed remote script execution. |
| D1 backup/restore rollback | Instance epoch/schema checks, change-log continuity validation, device re-bootstrap when cursor lineage changes. |
| Sensitive logging | Structured redaction tests, body logging disabled, safe error taxonomy, diagnostic preview. |
| Resource exhaustion or abusive client | Authentication before expensive work, bounded pages/payloads, indexes, quotas, retry budgets, rate limits. |

The security model does not claim end-to-end encryption against the user's Cloudflare account administrator. Client-side content encryption is deferred because it would materially affect search, recovery, Web functionality, key management, and support; it requires a separate product decision and threat model.

## 11. Migrations, upgrades, and compatibility

### 11.1 Version axes

Track independently:

- desktop app version;
- Web/Worker release version;
- sync protocol version;
- D1 schema version;
- desktop SQLite schema version;
- transport DTO version and supported operation versions.

Every sync response returns server release, protocol minimum/maximum, and instance schema health. Every client sends its version and supported protocol range.

### 11.2 Compatibility policy

- Within a supported protocol major version, changes are additive first: optional fields, new operations, or new response capabilities.
- Required behavior uses capability negotiation; absence means the older safe behavior, not a guessed default.
- Server deploys before a client starts emitting a new operation.
- A server must tolerate the documented prior client window. A client outside the window gets `upgrade_required` before submitting mutations.
- Unknown fields are preserved or ignored according to the DTO contract; unknown operations are rejected, never partially applied.
- Removing/renaming a field requires a staged dual-read/dual-write migration and proof that supported clients no longer depend on it.
- Downgrading Worker code across an incompatible D1 migration is blocked. Rollback requires a declared compatible code range or a tested data restore.

### 11.3 Migration execution

1. Validate migration checksums and current version.
2. Put the instance into a narrowly scoped maintenance/read-only state if the migration cannot be online.
3. Apply one idempotent, forward-only migration at a time.
4. Backfill in bounded resumable steps when data volume could exceed a request budget.
5. Verify constraints, expected counts, indexes, and application health.
6. Record release/checksum and leave a safe operator-facing failure reason if incomplete.

Desktop SQLite migrations must add the Personal Cloud outbox/checkpoint state without changing the existing fresh-start, backup, or channel contracts. Dev, Preview, and Production clients never share a Personal Cloud instance by accident; an explicit advanced override is required for testing.

## 12. Quotas, errors, and resilience

### 12.1 Bounded design

- All list, changes, bootstrap, and diagnostic endpoints paginate by count and encoded bytes.
- Uploads use small bounded mutation batches; one bad mutation does not make unrelated later batches ambiguous.
- D1 queries use explicit indexes and select only required columns.
- Long backfills and compaction persist their cursor and resume across requests.
- Payload limits are lower than platform maxima and versioned in server capabilities.
- Current Cloudflare limits are read from official documentation during implementation/release validation rather than hardcoded into product copy.

### 12.2 Error contract

Errors have a stable machine code, safe user message, request ID, retryability, and optional retry time. Core categories are:

- `unauthenticated`, `forbidden`, `device_revoked`, `reauthentication_required`;
- `invalid_request`, `unsupported_protocol`, `upgrade_required`;
- `conflict`, `stale_field`, `sequence_gap`, `checkpoint_expired`, `entity_deleted`;
- `rate_limited`, `quota_exhausted`, `payload_too_large`;
- `migration_required`, `maintenance`, `storage_unavailable`;
- `provider_pending`, `provider_rejected`;
- `internal_error`.

Internal exceptions and SQL text never reach clients.

### 12.3 Client behavior

- Retry transient network errors, 429, and retryable 5xx responses with capped exponential backoff, full jitter, and `Retry-After` when present.
- Do not retry validation, authorization, protocol, or ownership errors without a state change.
- Desktop retains queued local mutations through restarts and surfaces age/count of stuck work without blocking local use.
- Quota exhaustion pauses cloud sync and explains that local data is safe. It does not delete outbox rows or repeatedly hammer the service.
- Web preserves an unsent form in memory and reports that the cloud did not accept it.
- Circuit-break repeated failures per instance/device and allow a manual retry.

## 13. Observability and support

### 13.1 Instance-local observability

Minimum signals:

- request count, status/error class, route template, latency, Worker release;
- D1 operation latency/failure class and bounded row counts;
- accepted/duplicate/rejected mutation counts by operation type;
- pull lag by acknowledged sequence, active/rebootstrap-required device count;
- outbox pending age/count reported by desktops without task content;
- bootstrap/migration/compaction state and last successful checkpoint;
- external command counts by pending/confirmed/rejected and integration type, with no provider identity payload.

Authenticated `/health` returns only version, schema state, maintenance state, and coarse dependency health. Public unauthenticated health, if present, returns no instance details.

### 13.2 Correlation and diagnostics

- Worker assigns or validates a bounded request ID and returns it on every response.
- Clients associate mutation ID, device-local outbox ID, request ID, and resulting change sequence in redacted logs.
- A diagnostics export includes versions, capability negotiation, schema health, device-safe aliases, cursor/lag summaries, error codes, and recent redacted request IDs.
- Diagnostics have deterministic redaction tests. Users preview the file before sharing.

### 13.3 Support runbooks required before preview

- Cannot provision / OAuth consent denied.
- Worker deployed but setup incomplete.
- D1 binding or migration unhealthy.
- Desktop paired but not syncing.
- Checkpoint expired / device needs re-bootstrap.
- Provider completion remains pending or is rejected.
- Quota/rate limit reached.
- Lost passkey/device/recovery code.
- Upgrade failed or Worker rolled back.
- Export, disconnect, and destructive cloud removal.

## 14. Repository boundaries

The eventual implementation should preserve existing host/Core separation. Proposed ownership:

| Path | Responsibility |
| --- | --- |
| `docs/personal-cloud-plan.md` | Canonical product, architecture, and delivery plan. |
| `docs/personal-cloud-protocol.md` | Versioned transport operations, envelopes, error codes, compatibility examples. |
| `src/Openza.Tasks.Core/PersonalCloud/` | Framework-neutral desktop sync client, allowlisted DTO mapping, outbox/checkpoint abstractions, conflict application. No Cloudflare SDK dependency. |
| `src/Openza.Tasks/Services/` | WinUI credential/pairing UI and Windows background/lifecycle integration. |
| `src/Openza.Tasks.Desktop/Services/` | Avalonia credential/pairing UI and Linux background/lifecycle integration. |
| `src/Openza.Tasks.Tests/` | Core/outbox/mapper/migration/provider-bridge tests and shared protocol fixture verification. |
| `scripts/personal-cloud/` | Public-safe local validation and technical-preview packaging/provisioning helpers. |
| Sibling repository `openza/personal-cloud` | Shared identity/device/deployment infrastructure plus isolated product modules. Tasks owns the first Worker API, D1 migrations, Web app/static assets, cloud tests, and release lifecycle. Future products enter through separately reviewed modules. |

Generated protocol fixtures may be shared as checked-in JSON, but C# and TypeScript implementations do not share runtime code. One language-neutral contract and cross-runtime conformance suite prevents accidental drift. The Tasks repository remains the canonical home for the product contract and desktop protocol view; the dedicated Personal Cloud repository consumes released fixtures and owns the deployable cloud/Web application.

Do not put Cloudflare API credentials in repository configuration. Wrangler environments, D1 IDs, deployment outputs, local databases, and `.dev.vars` remain ignored. Production and preview deploys use reviewed release artifacts and environment-specific bindings.

## 15. Delivery strategy: smallest testable increments

The architecture above is the destination, not permission to build it all at once. Delivery proceeds through small evidence-gated checkpoints. Each checkpoint adds one observable behavior, validates it, records the result, and stops for review before the next checkpoint starts.

### 15.1 Working rules

1. Only the current checkpoint is in implementation scope. Later checkpoint designs may be documented, but their code is not scaffolded “for later.”
2. A checkpoint adds one synchronization behavior or one safety property wherever practical.
3. Each checkpoint states what is deliberately absent so a passing test cannot imply broader readiness.
4. Every failure is reproduced at the smallest layer first. Do not add Web, authentication, provider behavior, or deployment to compensate for an unproven sync kernel.
5. Validation evidence is recorded before more capability is added. A failing or ambiguous result pauses the sequence.
6. All desktop work uses isolated Dev data. All cloud work is local simulation until the technical-preview provisioning checkpoint is explicitly authorized.
7. No checkpoint authorizes Cloudflare login, account access, infrastructure creation, deployment, provider writes, commit, push, PR, or release by implication.

Cross-cutting concerns remain owned throughout: product/protocol, Core/local data, cloud API/data, Web, desktop hosts, security/privacy, provisioning/release, and quality/support. They enter only when the current checkpoint needs them.

### 15.2 Checkpoint K0 — freeze the one-task contract

Goal: define the smallest interoperable message without running a cloud service.

Included:

- one existing Space ID treated as a required parent reference;
- one local Openza task;
- `task.create` only;
- the minimum mutation envelope, accepted receipt, and change record;
- an explicit transport allowlist;
- matching C# and TypeScript golden fixtures.

Excluded: SQLite migration, network transport, Worker, D1, Web, task update, completion, deletion, provider wrappers, authentication, pairing, deployment, and user-visible UI.

Validation:

- both runtimes parse and emit the same canonical fixtures;
- a negative test proves credentials, provider metadata, raw `source_metadata`, and unrelated local fields cannot enter the payload;
- unknown required protocol behavior fails closed;
- the document and fixture name every field rather than serializing `TaskItem` wholesale.

Exit: the fixture and allowlist receive an explicit review; no implementation beyond contract tests proceeds on an unsettled envelope.

### 15.3 Checkpoint K1 — atomic desktop intent, no server

Goal: prove a task creation cannot commit locally without its sync intent.

Included:

- the smallest desktop SQLite migration for outbox metadata;
- one transaction that inserts a local task and its `task.create` outbox row;
- stable `mutation_id` and monotonic `device_sequence` generation;
- repository-level tests using a disposable database.

Excluded: HTTP, Worker, D1, checkpoint pulling, retries over a network, Web, provider behavior, authentication, and UI setup.

Validation:

- normal commit produces exactly one task and one outbox row;
- injected failure before commit produces neither row;
- injected failure after commit preserves both rows after process restart;
- repeatedly reading the outbox preserves the same mutation ID and canonical content;
- existing local task behavior and migrations remain green.

Exit: crash-boundary evidence proves local edit/outbox atomicity. Stop and review the migration before adding transport.

### 15.4 Checkpoint K2 — idempotent local Worker and D1 acceptance

Goal: prove one queued `task.create` is accepted exactly once.

Included:

- a local-only Worker runtime and local D1 database in the dedicated Personal Cloud repository;
- the minimum `tasks`, `mutations`, and `changes` tables;
- one `task.create` endpoint with a temporary test-only actor boundary;
- atomic projection, receipt, and change-log insertion;
- a fake/test desktop transport that sends the K1 outbox message.

Excluded: Cloudflare account access, remote bindings, deployment, production authentication, Web, pull/checkpoints, updates, deletes, provider tasks, and multiple devices.

Validation:

- first submission creates one task, one mutation receipt, and one change;
- retrying the identical mutation after success or a lost response returns the original receipt and creates no additional rows;
- reusing the mutation ID with different content is rejected;
- database failure cannot leave a task without its receipt/change record or vice versa;
- task payload content is absent from test logs.

Exit: duplicate and failure-injection evidence proves exactly-once effect from at-least-once delivery. Stop before adding pull.

Creating the sibling Personal Cloud repository and implementing K2 require separate authorization; K0 and K1 do not silently create it.

### 15.5 Checkpoint K3 — pull one change from one checkpoint

Goal: prove an accepted cloud task can be reconstructed deterministically.

Included:

- `GET /sync/changes` for one bounded page;
- checkpoint `0` to the returned `change_seq`;
- transactional application into a second disposable desktop database or a purpose-built Core replica harness;
- acknowledgement only after the local transaction commits.

Excluded: pagination beyond one page, bootstrap sessions, compaction, concurrent writers, Web, authentication, provider tasks, and deployment.

Validation:

- pulling from `0` creates the same task and saves the checkpoint;
- repeating the same pull is harmless;
- failure while applying the page advances neither task state nor checkpoint;
- restart resumes from the last committed checkpoint;
- pulling after the saved checkpoint returns no change.

Exit: source and replica converge on the allowlisted task fields and checkpoint. Stop before adding another mutation type.

### 15.6 Checkpoint K4 — one complete failure matrix

Goal: validate the one-task create path under interruption before expanding functionality.

Run the K1–K3 path with failure injected:

- before and after the local SQLite commit;
- before request send, after send, and after server commit but before response receipt;
- before and during replica apply;
- after replica commit but before checkpoint acknowledgement;
- across desktop and local Worker restarts;
- with a malformed, oversized, or protocol-incompatible payload.

Exit: every case ends in one accepted cloud task, an intact local outbox until acknowledgement, and eventual source/replica convergence—or a typed, non-destructive rejection. This is the sync-kernel review gate.

Nothing beyond K4 begins until the evidence is reviewed and the one-task create path is accepted.

### 15.7 Checkpoint K5 — read-only Web proof

Goal: prove desktop-independent Web reading without introducing Web mutations or full authentication.

Included:

- one local static Web page served through the local Worker/static-assets setup;
- a test-only local session;
- read-only display of the single cloud task and its Space identity;
- explicit online/unavailable/stale states.

Excluded: Web create/edit/complete, service-worker data queue, pairing, passkeys, provider tasks, deployment, and visual feature parity.

Validation: the task remains readable while both desktop processes are stopped; Worker/D1 unavailability never presents cached data as current or editable.

Exit: accept or reject the Worker + Static Assets composition before building a product UI.

### 15.8 Openza mutation expansion — one operation per checkpoint

After K0–K5 are accepted, expand the proven path in this order. Each line is a separate checkpoint with its own fixtures, local transaction, Worker handler, pull application, restart/duplicate tests, and review:

1. `task.updateOpenzaFields` for title only on a local task.
2. The same operation for one additional Openza scalar field at a time, beginning with workflow status and local priority.
3. `task.complete`, then `task.reopen`, for a local task only.
4. One date-only field, then exact-time handling and timezone tests.
5. `task.delete` plus a tombstone, without compaction.
6. One project and task-to-project membership.
7. A second Space and task move with no selective-sync setting.
8. Labels through independent add/remove set operations.
9. Preserve provider-originated parent/subtask relationships during publish, pull, bootstrap, and Space moves. This is an internal fidelity operation, not a new native Openza create/re-parent feature; Web must not introduce subtask editing before the desktop product supports it.
10. A bounded second change page.
11. A second device and same-field conflict.
12. Bootstrap/re-bootstrap, then tombstone retention and compaction.

Web mutation support follows the same order only after the corresponding desktop/cloud operation is stable. It reuses the semantic operation; it does not introduce a Web-specific whole-row API.

### 15.9 Source-linked Openza task expansion — one safe source fact before one command

Every item in this section remains an Openza task in the same `tasks` collection and uses the same Web task experience. This section adds synchronization of the optional source link and desktop-only external delivery; it does not add provider browsing, provider setup, or a second Web task model.

1. Allow one source-linked Openza task through the same create/bootstrap/change path using an explicit safe source descriptor, with negative privacy tests.
2. Render it as the same Openza task on Web; source-origin display facts, when shown, remain read-only.
3. Refresh one source-owned display fact from a fake desktop provider into the same cloud task projection.
4. Submit `task.complete` as Openza intent while the fake provider host is offline; show `pending_desktop`.
5. Confirm one successful provider write.
6. Validate rejection, retry, stale snapshot, complete/reopen coalescing, and multiple capable desktops separately.

No real provider account or Production/Preview database is used by these checkpoints.

### 15.10 Identity and recovery expansion

Do not design authentication and synchronization simultaneously. Once the sync kernel and read-only Web composition are accepted, replace the test-only actor in isolated steps:

1. one owner passkey and one secure Web session;
2. session rotation, expiry, CSRF/origin, and revocation;
3. one short-lived pairing grant;
4. one desktop credential in the OS credential store;
5. device revocation and re-pairing;
6. recovery code generation, use, and rotation;
7. all-app-credentials-lost recovery;
8. abuse limits and independent security review.

Each step runs the existing K4 failure matrix where applicable. Identity work cannot weaken idempotency or expose task/provider data in logs.

### 15.11 Technical-preview checkpoints

Remote Cloudflare work starts only after separate explicit authorization and after local behavior is green:

1. Confirm current OAuth API coverage, exact scope identifiers, redirect rules, and minimum permissions.
2. Prove Authorization Code + S256 PKCE from the Desktop without provisioning.
3. Provision one disposable D1 database through the permanent coordinator, verify ownership, and remove it.
4. Provision the complete version-pinned Worker/D1 instance with resumable partial-failure handling and automatic initiating-desktop pairing.
5. Repeat K0–K5 against preview resources with synthetic data.
6. Validate quotas, latency, diagnostics, migrations, export, rollback, and removal independently.
7. Rehearse on a fresh account and an existing account, stopping after each resource-creation stage to verify ownership and cleanup.
8. Package and exercise Personal Cloud in a strict-confined development Snap: bundle the verified Worker/Web artifact, permit the fixed loopback OAuth callback, verify browser handoff and Secret Service access, and prove that Production-channel data and credentials stay isolated from Dev. Build it with Preview channel markers and install it only as the named parallel Snap instance `openza-tasks_pcdev`, giving it a separate credential namespace, data directory, configuration, interfaces, applications, and aliases; a package installed as plain `openza-tasks` must not replace the installed production app. Pair an existing Personal Cloud rather than provisioning a duplicate, and rehearse the explicit test-cloud cleanup or production-seed decision before connecting real Production data. Before distribution, either obtain the required Secret Service interface auto-connection or use a reviewed desktop Secret Portal path.

Exit: the technical-preview release gates below pass. The result remains a technical preview, not public onboarding.

### 15.12 Public onboarding checkpoints

Public Connect Cloudflare onboarding hardens the same flow proven in technical preview; it does not replace it with a different setup mechanism:

1. Complete publisher-domain verification and the required public-client approval/visibility process.
2. Verify the public client requests only the preview-proven minimum scopes.
3. Discard/revoke provisioning access and prove the deployed app has no standing administration token.
4. Validate upgrade authorization, non-developer usability, recovery, and destructive removal separately.
5. Validate supported Desktop versions and staged public release operations.

Exit: all public release gates pass. Deploy to Cloudflare may remain an advanced/manual fallback, but it is not the primary public onboarding promise.

## 16. Test strategy

### 16.1 Unit and property tests

- Allowlist mapper never serializes excluded fields, secrets, opaque provider JSON, or filesystem data.
- Every semantic operation validates ownership, capabilities, enum/domain rules, and referential integrity.
- Canonical payload hashing is stable across C# and TypeScript.
- Mutation IDs, duplicate receipts, sequence gaps, and base-version behavior are deterministic.
- Conflict algebra: commuting operations converge regardless of delivery order; non-commuting operations yield typed conflicts.
- Date-only and instant fields round-trip across timezones and daylight-saving boundaries.
- Tombstones prevent accidental resurrection.
- Token/session/pairing/recovery verifiers, rotation, expiry, and rate-limit state behave correctly.

### 16.2 Database and migration tests

- Fresh D1 and every supported prior schema migrate to current.
- Failed/interrupted backfills resume idempotently.
- Domain mutation + receipt + change append is atomic.
- Uniqueness, foreign-key/application invariants, indexes, pagination ordering, and query plans are asserted.
- D1 restore/rollback lineage forces safe device re-bootstrap when needed.
- Desktop outbox insertion is atomic with local edits and survives process termination at each sync transition.

### 16.3 Protocol conformance

- Shared golden request/response/change fixtures run in both runtimes.
- Supported previous client and current server combinations pass the compatibility matrix.
- Unknown optional fields are safe; unknown operations and incompatible major versions fail closed.
- Error bodies contain safe stable codes and never internal exception/SQL/secrets.

### 16.4 End-to-end scenarios

- Offline desktop edit, delayed reconnect, Web convergence.
- Web edit while all desktops are offline, then desktop convergence.
- Two desktops edit different fields, same field, labels, ordering, and deleted entities; provider-originated parent relationships remain intact through synchronization and subtree Space moves.
- Lost response and repeated push produce exactly one accepted mutation.
- Bootstrap interruption at every page and resume.
- Checkpoint compaction followed by forced re-bootstrap.
- Adopted provider task refresh alongside Openza field edits.
- Complete/reopen coalescing with provider host offline, success, timeout, rejection, and later source refresh.
- Device revocation during pull/push and after access-token issuance.
- Passkey loss, recovery-code rotation, and guarded owner re-bootstrap.
- Quota, 429, 5xx, D1 failure, corrupt response, clock skew, oversized payload, and migration maintenance.

All desktop end-to-end tests use isolated Dev data and provider fakes. Production/Preview apps, real provider accounts, and live local databases are not touched without separate explicit authorization.

### 16.5 Web and security tests

- Keyboard, focus, screen-reader semantics, responsive layout, high contrast, and reduced motion.
- CSP, Trusted Types where used, output encoding, dependency audit, CSRF/origin enforcement, cookie attributes.
- Authorization matrix for every route and operation.
- Pairing/recovery brute-force controls and uniform error behavior.
- Static asset cache/update correctness and old-Web/new-Worker compatibility.
- No secret/task content in browser storage, source maps, logs, telemetry, or error reports.

### 16.6 Performance and soak tests

Use small, medium, and deliberately large personal datasets. Measure bootstrap size/time, incremental sync latency, query count, Worker CPU, D1 rows read/written, browser render responsiveness, and outbox drain. Run long offline gaps and repeated disconnect/retry soak tests. Set release budgets from measured representative use, with headroom below current Cloudflare limits.

## 17. Release gates

### 17.1 Technical preview gates

- Product scope, ownership matrix, protocol, and threat model accepted.
- K0–K5, the required Openza/provider/identity expansion checkpoints, and all technical-preview checkpoint exit criteria passed on current mainline artifacts.
- Full relevant tests green; security/secret scan green; dependencies and licenses reviewed.
- No provider credential, raw intake payload, task content, or secret appears in logs/diagnostics/build output.
- Fresh and existing Cloudflare account deployment rehearsals completed.
- Migration, retry, quota, export, disconnect, upgrade, and removal runbooks exercised.
- Windows and Linux Dev hosts tested without touching live app data.
- A strict-confined development Snap completes OAuth, existing-cloud pairing, restart, and sync with isolated synthetic Production-channel data; the shipped artifact and required interfaces are verified from the packed Snap.
- Known limitations, supported versions, cost/quota responsibility, and recovery caveats published.
- Independent architecture/security review has no unresolved release-blocking finding.

### 17.2 Public release gates

All preview gates, plus:

- Guided Connect Cloudflare OAuth with public-client S256 PKCE is approved and uses verified minimum scopes.
- No embedded client secret or retained standing Cloudflare administration token.
- Partial/cancelled provisioning safely resumes or rolls back.
- Non-developer usability tests pass setup, pairing, recovery, upgrade, and removal.
- Compatibility is proven across the documented desktop support window.
- Upgrade and rollback are proven on representative prior D1 schemas.
- Abuse controls and external security review are complete.
- Privacy notice, data map, dependency/SBOM, support lifecycle, and vulnerability reporting path are published.
- At least one staged preview cohort has met reliability and data-integrity targets through the defined observation period.

No release gate may be waived merely because local desktop data remains available; synchronization can still propagate damaging mutations or expose private content.

## 18. Risk register

| Risk | Impact | Mitigation / release evidence |
| --- | --- | --- |
| Whole-row sync leaks provider secrets or opaque metadata | Critical privacy breach | Explicit DTO allowlist, negative serialization tests, redacted logs, security review. |
| Ambiguous authority overwrites provider/Openza fields | Data loss or provider corruption | Field ownership matrix, semantic operations, server enforcement, conflict tests. |
| Completion shown as externally confirmed too early | Broken user trust | Separate Openza intent from provider confirmation; pending/rejected UI and durable bridge. |
| Desktop edit and outbox insert are not atomic | Silent unsynced change | One SQLite transaction, crash/failure injection tests. |
| Duplicate/reordered requests create divergence | Data corruption | Stable mutation ID, device sequence, atomic receipt/change log, duplicate conformance tests. |
| Long-offline device resurrects deleted data | Data loss/confusion | Tombstones, checkpoint expiry, explicit restore, re-bootstrap policy. |
| D1/platform quotas stop sync | Availability loss | Bounded payloads/queries, backoff, quota state, local-first desktop, capacity gates. |
| Online-first Web is mistaken for offline-safe | Lost Web draft or false save | Clear connection state, no “saved” until receipt, limited shell cache, navigation warning. |
| Public provisioning requests excessive Cloudflare access | Security/adoption failure | Minimum scopes, explicit consent, PKCE, discard token, reviewed deterministic resources. |
| User loses every app credential | Instance lockout | Recovery code, multiple passkeys, tested Cloudflare-control re-bootstrap, local reseed path. |
| Cloudflare account compromise exposes content | Confidentiality loss | Honest threat model, account-security guidance, least provisioning persistence; no false E2EE claim. |
| Worker/D1 migration mismatch | Outage or corruption | Compatibility range, checksummed migrations, maintenance state, health gates, tested restore/rollback. |
| Multiple release channels cross-sync | Test data contaminates live data | Instance/channel binding, clear environment identifiers, explicit advanced overrides only. |
| Snap confinement blocks OAuth callback, browser handoff, or credential storage | Setup fails only after release | Bundle the Personal Cloud artifact, declare the minimum interfaces including loopback binding, inspect the packed Snap, and run the complete setup/pair/restart/sync path in an isolated development Snap before release. |
| Supporting the stack overwhelms a small project | Incomplete/unsafe release | Small baseline stack, vertical slices, preview cohort, hard public gates, defer optional services. |

## 19. Accepted decisions

The following are decisions, not open design alternatives:

- Openza desktop remains local-first and fully usable without Personal Cloud.
- Personal Cloud is optional and deployed to the user's Cloudflare account.
- Web is online-first and independent of desktop availability after provisioning.
- Phase 1 synchronizes all Spaces and adopted Openza tasks; raw provider intake is excluded.
- Windows and Linux desktop remain provider integration hosts; Web does not manage providers.
- Provider-owned fields are read-only on Web.
- Openza-owned fields and complete/reopen semantic commands synchronize.
- Initial cloud stack is Worker + Workers Static Assets + D1.
- No Durable Objects, Queues, R2, Workflows, or D1 read replicas initially.
- Deploy to Cloudflare is a technical-preview setup path.
- Public onboarding requires guided Connect Cloudflare OAuth with a public-client Authorization Code + S256 PKCE flow.
- Protocol uses semantic mutations, idempotency receipts, sequence checkpoints, tombstones, and transactional desktop outbox.
- User choices are minimized; there is no sync-folder or per-Space selection in Phase 1.

## 20. Deferred scope

- Multi-user sharing/collaboration and tenant isolation beyond one owner deployment.
- End-to-end/client-side content encryption.
- Web provider management, provider intake, or direct provider API calls.
- Attachments, R2, rich documents, time entries, and task enhancements.
- Offline-first Web mutation queue.
- Push notifications, email reminders, real-time collaboration, WebSockets.
- Cloud-to-cloud provider routes or headless provider tokens.
- Selective Space sync, multiple Personal Cloud instances per desktop, account federation.
- Automatic standing-token upgrades without interactive Cloudflare authorization.
- Cloudflare custom domain automation.
- Durable Objects, Queues, Workflows, read replicas, and analytics products without measured need.

## 21. Open questions requiring an explicit decision

These questions do not block writing the protocol skeleton, but the named gate cannot pass until they are decided:

1. **Web owner credential:** confirm passkey-first plus one-time recovery code as the public baseline after browser/platform compatibility testing. Owner: security/product. Gate: identity and recovery expansion.
2. **Device proof:** choose opaque refresh credential plus short access token versus a registered device signing key after a focused implementation/security spike. Owner: security/Core. Gate: identity and recovery expansion.
3. **Delete/restore windows:** set tombstone, idempotency-receipt, and inactive-device retention defaults from realistic offline usage and D1 capacity tests. Owner: product/cloud. Gate: delete, bootstrap, and compaction expansion checkpoints.
4. **Provider rejection UX:** decide whether rejected Openza completion remains the visible primary state indefinitely or requires a reconciliation banner/action. Owner: product. Gate: provider expansion.
5. **Openza title override:** decide whether adopted wrappers need an Openza-owned display-title override; provider title remains immutable from Web either way. Owner: product. Gate: before exposing title edit for wrappers.
6. **Web feature parity subset:** enumerate the exact existing smart lists, filters, grouping, and task editing controls required for technical preview. Owner: product/Web. Gate: before expanding beyond the K5 Web proof.
7. **All-credentials-lost recovery:** define the guarded Cloudflare-control procedure and whether public setup can make it non-CLI. Owner: provisioning/security. Gate: public release.
8. **Upgrade authorization:** validate whether re-running PKCE consent for each upgrade is acceptable or a narrower signed updater can be safely delegated. Owner: provisioning/security. Gate: public release.
9. **Instance URL stability:** decide the supported behavior when a Worker route or custom domain changes, including passkey RP-ID implications. Owner: security/provisioning. Gate: identity expansion and public onboarding.
10. **Cloud export format:** choose a public-safe, versioned export that supports recovery without pretending the D1 schema is the application contract. Owner: Core/product. Gate: technical preview.

## 22. Progress log template

Keep one durable task log beside implementation artifacts for each slice. Do not use the log to weaken gates or replace test evidence.

```markdown
# Openza Personal Cloud — Checkpoint Progress

Last updated: YYYY-MM-DD HH:MM TZ
Owner/task:
Branch/worktree:
Base commit:
Target checkpoint and exit criterion:

## Scope

- Included:
- Explicitly excluded:
- Product decisions applied:

## Completed

- [ ] Deliverable — evidence/path/commit

## Remaining

- [ ] Deliverable — next concrete action

## Verification

| Check | Environment | Result | Evidence |
| --- | --- | --- | --- |
| Unit/conformance | | PASS/FAIL/BLOCKED | |
| Migration/data integrity | | PASS/FAIL/BLOCKED | |
| Windows Dev | | PASS/FAIL/BLOCKED | |
| Linux Dev | | PASS/FAIL/BLOCKED | |
| Worker/Web local | | PASS/FAIL/BLOCKED | |
| Security/privacy | | PASS/FAIL/BLOCKED | |
| Secret scan | | PASS/FAIL/BLOCKED | |

## Data and compatibility observations

- Dataset/profile:
- Protocol/client/server/schema versions:
- Mutation/checkpoint/tombstone evidence:
- Performance/quota evidence:

## Risks and findings

| Severity | Finding | Status | Owner | Required action |
| --- | --- | --- | --- | --- |

## Decisions and open questions

- Decision made, rationale, approver, date:
- Decision still required, owner, blocking gate:

## External actions

- Cloudflare authorization/deploy performed: no/yes + explicit authorization evidence
- Infrastructure created/changed: none or exact resources
- Commit/push/PR/release performed: none or exact references

## Handoff

- Current safe state:
- How to resume:
- Uncommitted files:
- User action required:
```

## 23. Reference baseline

Implementation must re-check current official platform documentation before fixing limits, permissions, API fields, or migration behavior:

- [Cloudflare Workers Static Assets configuration and binding](https://developers.cloudflare.com/workers/static-assets/binding/)
- [Cloudflare D1 Workers Binding API](https://developers.cloudflare.com/d1/worker-api/)
- [Cloudflare D1 database sessions and batches](https://developers.cloudflare.com/d1/worker-api/d1-database/)
- [Cloudflare D1 platform limits](https://developers.cloudflare.com/d1/platform/limits/)
- [Cloudflare Workers platform limits](https://developers.cloudflare.com/workers/platform/limits/)
- [Cloudflare OAuth applications](https://developers.cloudflare.com/fundamentals/oauth/)
- [Creating a Cloudflare OAuth client and public-client PKCE requirements](https://developers.cloudflare.com/fundamentals/oauth/create-an-oauth-client/)
- [Cloudflare OAuth endpoints](https://developers.cloudflare.com/fundamentals/oauth/integrate-with-cloudflare/)

Platform documentation is evidence for implementation mechanics, not permission to deploy or authorize an account. Any live Cloudflare action remains a separate, explicit user-approved step.
