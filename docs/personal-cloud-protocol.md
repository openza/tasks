# Openza Personal Cloud Protocol

Status: K0 through K5 plus title, workflow-status, local-priority, completion, and reopen checkpoints validated locally; review pending

Protocol major: 1

Last updated: 2026-09-13

This document defines the language-neutral Personal Cloud protocol. It is intentionally incomplete: K0 through K5 specify the `tasks` product and one local Openza task created by one test device, accepted by a local Worker/D1 simulation, reconstructed by one disposable replica, recovered across the defined interruption boundaries, and displayed in a local read-only Web proof. The first post-K5 checkpoints add separate versioned title, workflow-status, local-priority, completion, and reopen changes. The broader architecture and delivery gates remain canonical in [`personal-cloud-plan.md`](personal-cloud-plan.md).

An operation not defined here is unsupported. Clients and servers must not infer update, completion, deletion, provider, authentication, or deployment behavior from the K0 contract.

## 1. K0 boundary

K0 answers one question:

> Can C# and TypeScript represent exactly the same safe, retryable `task.create` intent, acceptance receipt, and resulting change record without serializing the desktop task model wholesale?

### 1.1 Included

- Protocol major version `1`.
- One logical Personal Cloud instance.
- One test device.
- One pre-existing Space with ID `space_default`.
- One local Openza task.
- One semantic operation: `task.create`.
- One accepted receipt.
- One resulting change record.
- Canonical request comparison for idempotent retry.

### 1.2 Explicitly excluded

- Network endpoints, Worker routes, D1 tables, SQLite migrations, and executable runtime behavior.
- Authentication, authorization, browser sessions, pairing, and device revocation.
- Task update, complete, reopen, cancel, delete, restore, labels, projects, hierarchy, dates, recurrence, notes, and metadata.
- Provider-linked tasks, source snapshots, provider identities, credentials, intake, and provider commands.
- Pull pagination, persisted checkpoints, bootstrap, tombstones, compaction, and conflicts between devices.
- Web UI, Cloudflare authorization, infrastructure, deployment, and release.

## 2. Encoding rules

- Media type, when transport is introduced: `application/json` encoded as UTF-8.
- JSON property names use lower camel case.
- Enumerated values and operation names use lowercase dotted or snake-case values exactly as specified.
- Unknown top-level request properties are rejected in K0. Later protocol versions may define forward-compatible optional extension containers explicitly.
- Required strings must be valid Unicode, contain no unpaired surrogate, and meet their field limits before canonicalization.
- Timestamps use RFC 3339 UTC form with `Z` and millisecond precision: `YYYY-MM-DDTHH:mm:ss.sssZ`.
- Integer values are JSON integers, not quoted strings or floating-point values.
- `null` is used only where the schema explicitly permits it. Missing and `null` are not generally interchangeable.
- IDs are opaque, case-sensitive strings. A receiver must not extract meaning from their prefixes.
- A canonical request hash is lowercase hexadecimal SHA-256 over the UTF-8 bytes of the request after JSON Canonicalization Scheme serialization. Transport headers are not part of the hash.

The canonical request hash lets the server distinguish a safe retry from malicious or erroneous reuse of a mutation ID. The executable fixtures must use the same canonical bytes and expected hash in both runtimes.

## 3. Identifier contract

K0 preserves the current Openza task and Space IDs rather than remapping them in Personal Cloud.

| Field | K0 rule |
| --- | --- |
| `instanceId` | Opaque non-empty ASCII identifier, 1–128 characters. Fixture prefix: `instance_`. |
| `deviceId` | Opaque non-empty ASCII identifier, 1–128 characters. Fixture prefix: `device_`. |
| `mutationId` | Globally unique opaque ASCII identifier, 1–128 characters, stable across every retry. Fixture prefix: `mutation_`. |
| `entityId` / `task.id` | Existing Openza task identifier, 1–128 characters. The two values must match. Current local fixture prefix: `local_`. |
| `task.spaceId` | Existing Openza Space identifier, 1–128 characters. K0 uses `space_default`. |

K0 identifiers contain ASCII letters, digits, `_`, `-`, `.`, or `:`. Prefix examples document provenance for humans; the server does not authorize or classify an entity from a prefix.

## 4. Mutation envelope

### 4.1 Schema

| Property | Type | Required | K0 constraint |
| --- | --- | --- | --- |
| `protocolVersion` | integer | yes | Exactly `1`. |
| `product` | string | yes | Exactly `tasks`; other Openza products define separate modules. |
| `instanceId` | string | yes | Must match the receiving instance. |
| `deviceId` | string | yes | Test actor identity in K0. Production authorization is deferred. |
| `mutationId` | string | yes | Unique and stable across retries. |
| `deviceSequence` | integer | yes | Positive; K0 fixture begins at `1`. |
| `operation` | string | yes | Exactly `task.create`. |
| `entityId` | string | yes | Must equal `payload.task.id`. |
| `baseEntityVersion` | null | yes | Create has no prior entity version. |
| `payload` | object | yes | Exactly the K0 `task.create` payload. |
| `clientCreatedAt` | timestamp string | yes | Audit information only; never orders mutations. |

### 4.2 Golden semantic request

```json
{
  "protocolVersion": 1,
  "product": "tasks",
  "instanceId": "instance_00000000000000000000000000000001",
  "deviceId": "device_00000000000000000000000000000001",
  "mutationId": "mutation_00000000000000000000000000000001",
  "deviceSequence": 1,
  "operation": "task.create",
  "entityId": "local_00000000000000000000000000000001",
  "baseEntityVersion": null,
  "payload": {
    "task": {
      "id": "local_00000000000000000000000000000001",
      "spaceId": "space_default",
      "title": "Validate one task",
      "completionState": "open",
      "workflowStatus": "inbox",
      "createdAt": "2026-09-13T06:30:00.000Z"
    }
  },
  "clientCreatedAt": "2026-09-13T06:30:00.000Z"
}
```

Whitespace and property order above are for readability. The canonical fixture also records the canonical byte representation and expected SHA-256 hash.

## 5. `task.create` payload

### 5.1 Field allowlist

K0 permits exactly these task properties:

| Property | Type | Constraint |
| --- | --- | --- |
| `id` | string | Must equal envelope `entityId`; non-empty; maximum 128 characters. |
| `spaceId` | string | Must identify the pre-existing K0 Space; maximum 128 characters. |
| `title` | string | Trimmed value must be non-empty; maximum 512 UTF-8 bytes with invalid Unicode rejected. |
| `completionState` | string | Exactly `open` in K0. |
| `workflowStatus` | string | Exactly `inbox` in K0. |
| `createdAt` | timestamp string | Original desktop creation time in normalized UTC form. |

The K0 receiver rejects missing allowlisted properties and every additional task property. Defaults are not inferred because current model and storage defaults are not identical in every path.

### 5.2 Fields prohibited in K0

The following current `TaskItem` concerns are deliberately absent:

- `externalId`, `integrationId`, and `providerConnectionId`;
- description, source description, project, parent, and priority;
- source integration/connection/external/provider-task IDs and source URL;
- planned, deadline, scheduled, duration, and recurrence fields;
- notes;
- provider, source, and local metadata JSON;
- updated/completed timestamps and local SQLite revision;
- labels;
- computed properties such as `Status`, `IsCompleted`, and `IsProviderTask`.

Their absence in K0 is not a Phase 1 product exclusion. Each enters only through a later, separately reviewed operation or allowlist expansion.

### 5.3 Domain preconditions

The conceptual receiver accepts the mutation only when:

1. The protocol and instance match.
2. The device sequence is the next expected value.
3. The mutation ID is unused, or it is an identical retry.
4. The task ID does not already identify a live task, except through the identical mutation receipt.
5. `space_default` exists and is not deleted.
6. The payload contains exactly the allowlisted fields and valid values.

K0 does not define implicit task merging.

## 6. Acceptance receipt

### 6.1 Schema

| Property | Type | K0 meaning |
| --- | --- | --- |
| `protocolVersion` | integer | Server protocol used; exactly `1`. |
| `product` | string | Exactly `tasks`. |
| `mutationId` | string | Echoes the accepted/retrieved mutation. |
| `outcome` | string | `accepted` for first application or `duplicate` for identical retry. |
| `entityId` | string | Created task ID. |
| `entityVersion` | integer | Exactly `1` for creation. |
| `changeSeq` | integer | Positive cloud-assigned change position. |
| `serverAcceptedAt` | timestamp string | Server audit time, not conflict ordering input. |

### 6.2 First-acceptance fixture

```json
{
  "protocolVersion": 1,
  "product": "tasks",
  "mutationId": "mutation_00000000000000000000000000000001",
  "outcome": "accepted",
  "entityId": "local_00000000000000000000000000000001",
  "entityVersion": 1,
  "changeSeq": 1,
  "serverAcceptedAt": "2026-09-13T06:30:01.000Z"
}
```

An identical retry returns the same values with `outcome` set to `duplicate`. It must not allocate another entity version or change sequence.

## 7. Resulting change record

The K0 change record is sufficient for a later replica harness to reconstruct the allowlisted task. Persisted checkpoint behavior begins in K3, not K0.

| Property | Type | K0 constraint |
| --- | --- | --- |
| `protocolVersion` | integer | Exactly `1`. |
| `product` | string | Exactly `tasks`. |
| `changeSeq` | integer | Same value as the receipt. |
| `mutationId` | string | Originating mutation. |
| `operation` | string | Exactly `task.created`. |
| `entityKind` | string | Exactly `task`. |
| `entityId` | string | Created task ID. |
| `entityVersion` | integer | Exactly `1`. |
| `payload` | object | Contains the same allowlisted task representation. |
| `serverAcceptedAt` | timestamp string | Same accepted instant as the receipt. |

```json
{
  "protocolVersion": 1,
  "product": "tasks",
  "changeSeq": 1,
  "mutationId": "mutation_00000000000000000000000000000001",
  "operation": "task.created",
  "entityKind": "task",
  "entityId": "local_00000000000000000000000000000001",
  "entityVersion": 1,
  "payload": {
    "task": {
      "id": "local_00000000000000000000000000000001",
      "spaceId": "space_default",
      "title": "Validate one task",
      "completionState": "open",
      "workflowStatus": "inbox",
      "createdAt": "2026-09-13T06:30:00.000Z"
    }
  },
  "serverAcceptedAt": "2026-09-13T06:30:01.000Z"
}
```

## 8. K0 error envelope

```json
{
  "protocolVersion": 1,
  "product": "tasks",
  "error": {
    "code": "invalid_request",
    "message": "The task.create mutation is invalid.",
    "retryable": false,
    "requestId": "request_00000000000000000000000000000001"
  }
}
```

K0 defines these machine codes:

| Code | Retryable | Meaning |
| --- | --- | --- |
| `invalid_request` | no | Structure, field, type, length, timestamp, or value failed validation. |
| `unsupported_protocol` | no | Protocol major is not supported. |
| `unsupported_product` | no | Operation belongs to another Openza product module. |
| `instance_mismatch` | no | Envelope targets another instance. |
| `sequence_gap` | after state refresh | Device sequence is ahead of the expected value. |
| `mutation_id_reused` | no | Existing mutation ID has different canonical request content. |
| `entity_exists` | no | Task ID already exists from another mutation. |
| `parent_not_found` | after state refresh | Required Space is absent or deleted. |
| `internal_error` | yes | Safe generic failure; implementation detail is not disclosed. |

Validation errors do not echo task title or the rejected payload. A production request ID is server-generated or strictly validated; it is never accepted as an unbounded logging value.

## 9. Idempotency truth table

| Existing server state | Incoming request | Result |
| --- | --- | --- |
| No receipt for `mutationId` | Valid request and new task ID | Apply once; return `accepted`. |
| Matching receipt and matching canonical hash | Any retry timing | Do not apply; return original receipt as `duplicate`. |
| Matching receipt and different canonical hash | Any request | Reject `mutation_id_reused`. |
| No matching receipt but task ID already exists | Any create | Reject `entity_exists`; do not merge. |
| No matching receipt and missing Space | Any create | Reject `parent_not_found`. |

“Exactly once” describes the effect, not network delivery. Clients use at-least-once delivery with the same mutation ID until they receive a durable receipt.

## 10. Required executable fixtures for K0 exit

K0 documentation is not sufficient evidence by itself. The later executable contract checkpoint must add public-safe golden files for:

1. Valid request and expected canonical bytes/hash.
2. Accepted receipt.
3. Duplicate receipt.
4. Resulting change record.
5. Missing required task field.
6. Unknown task field.
7. Prohibited provider/source metadata field.
8. Mismatched envelope/task ID.
9. Unsupported protocol.
10. Same mutation ID with changed title.
11. Invalid timestamp and invalid Unicode.
12. Oversized identifier/title at the accepted limit and one unit beyond it.

C# and TypeScript must independently parse, validate, canonicalize, and emit these fixtures. A fixture is not accepted merely because both runtimes share generated code with the same bug.

## 11. K0 review checklist

- [ ] The task payload contains only `id`, `spaceId`, `title`, `completionState`, `workflowStatus`, and `createdAt`.
- [ ] Every request, receipt, change, and error explicitly identifies `product: "tasks"`.
- [ ] Current provider credential and metadata properties are absent, not nullable placeholders.
- [ ] `entityId` and `payload.task.id` must match.
- [ ] Create uses `baseEntityVersion: null` and produces `entityVersion: 1`.
- [ ] Mutation order uses cloud `changeSeq`; timestamps do not determine order.
- [ ] Same mutation ID and same canonical hash returns the original effect.
- [ ] Same mutation ID with different content fails closed.
- [ ] The pre-existing Space dependency is explicit.
- [ ] No wording implies authentication, HTTP, D1, Web, provider, or deployment readiness.
- [ ] Both runtime fixture suites pass before K1 begins.

## 12. Decisions deferred beyond K0

- Concrete generation algorithms for instance, device, and mutation IDs.
- Overall HTTP payload limit, to be fixed before transport is implemented.
- HTTP route, headers, status mapping, compression, and request size enforcement.
- Device authentication and server authorization.
- D1 transaction/schema implementation.
- Persisted outbox/checkpoint schema.
- Every task field and semantic operation beyond minimal create.
- Version-minor negotiation and optional extension behavior.

## 13. K3 single-change pull contract

K3 adds only one read operation: `GET /sync/changes?after=<checkpoint>&limit=1`. Both query properties are required exactly once. `after` is a non-negative safe integer and K3 accepts only `limit=1`.

The response contains exactly:

```json
{
  "protocolVersion": 1,
  "product": "tasks",
  "changes": [],
  "nextCheckpoint": 0
}
```

For the accepted K0 mutation, `changes` contains the existing `task.created` record and `nextCheckpoint` is its `changeSeq`. When no later change exists, `changes` is empty and `nextCheckpoint` equals the requested checkpoint. The executable golden response is `pulled-change-page.json`.

The replica validates the strict response and change allowlists before opening its transaction. It inserts the reconstructed task, records the applied change, and advances its durable checkpoint in one local SQLite transaction. Reapplying an identical page is harmless. If task application fails, neither the task, applied-change record, nor checkpoint commits.

K3 does not add multi-change pages, bootstrap, compaction, checkpoint expiry, multiple writers, authentication, Web behavior, provider tasks, deployment, or another mutation type. The K0 error envelope remains the only error shape and always identifies `product: "tasks"`.

## 14. K4 interruption and acknowledgement contract

K4 adds no user-facing task behavior. It closes the recovery loop around the K1 through K3 path.

A source desktop keeps its canonical outbox row after local commit and sends that same row until it receives a strictly validated `accepted` or `duplicate` mutation receipt matching the row's mutation and entity IDs. Only then may it set the row's local `acknowledged_at`. A malformed, incompatible, unknown, or mismatched receipt leaves the row pending. Repeating a valid acknowledgement is harmless.

After a replica applies a change and advances its local checkpoint in one committed SQLite transaction, it may send:

```json
{
  "protocolVersion": 1,
  "product": "tasks",
  "instanceId": "instance_00000000000000000000000000000001",
  "deviceId": "device_k4_replica",
  "checkpoint": 1
}
```

to `POST /sync/checkpoints`. The Worker returns the device ID and `acknowledgedCheckpoint`. Repeating the same acknowledgement or sending a lower checkpoint cannot move the stored checkpoint backward. A checkpoint ahead of the cloud change log returns typed `sequence_gap` and makes no change. Missing cloud acknowledgement does not undo the already committed replica; it only leaves cloud-side compaction knowledge stale.

K4 mutation and checkpoint request bodies are capped at 16 KiB. Malformed, oversized, unsupported-protocol, and unsupported-product requests use the K0 typed error envelope and create no durable task, change, receipt, or checkpoint effect.

The executable matrix covers interruption before and after source commit, before send, after send, after cloud commit with a lost response, before and during replica apply, after replica commit before checkpoint acknowledgement, and subsequent desktop/Worker invocations over durable state. Its accepted path ends with one cloud task, one change, one receipt, an outbox retained until receipt acknowledgement, and a converged replica. K4 remains test-only and local: it does not add authentication, Cloudflare account access, deployment, Web, additional task operations, provider tasks, bootstrap, or compaction.

Deferral is intentional. None of these decisions is needed to review the minimal one-task sync kernel at the K4 gate.

## 15. K5 read-only Web contract

K5 composes the existing Worker with Workers Static Assets. The static shell is served from `public/`; API routes continue through the Worker. One test-only local session sends `x-openza-test-session: k5-local-read-only` to `GET /v1/tasks`. This fixed value is intentionally visible in the browser bundle and is not authentication. The K5 build must not be deployed or represented as secure.

The read endpoint accepts no query parameters and returns at most the single K5 task:

```json
{
  "protocolVersion": 1,
  "product": "tasks",
  "freshness": "current",
  "serverReadAt": "2026-09-13T06:31:00.000Z",
  "tasks": [
    {
      "id": "local_00000000000000000000000000000001",
      "title": "Validate one task",
      "spaceId": "space_default",
      "completionState": "open",
      "workflowStatus": "inbox",
      "createdAt": "2026-09-13T06:30:00.000Z"
    }
  ]
}
```

Every API success or error has `Cache-Control: no-store`. After a successful fetch, the browser may retain the last confirmed K5 response only to demonstrate an explicit stale state. A successful response is shown as `Online · Current`. A failed refresh with a valid prior response is shown as `Unavailable · Last known`, including its last-confirmed time and a warning that it may be outdated. A failed refresh without prior data is shown as `Unavailable` and renders no task. The page automatically downgrades current data to last known after 60 seconds without confirmation.

Task content is inserted with text nodes, not HTML. The page contains no task create, edit, complete, delete, provider, pairing, or credential controls, and its browser code sends no mutation request. The one task remains readable directly from D1 while both desktop processes are stopped. Worker or D1 failure returns a generic, non-cacheable error without task content or private diagnostic detail.

K5 remains local-only and stops at the Worker plus Static Assets composition review gate. Production sessions, authentication, pairing, passkeys, deployment, visual feature parity, Web mutations, service-worker data queues, provider tasks, and additional list/pagination behavior remain deferred.

## 16. First post-K5 mutation: title only

This checkpoint answers one narrowly bounded product question: can a title edited on one desktop arrive on another desktop exactly once, without enabling general task editing? The source desktop changes its local task and appends the outbound intent in one SQLite transaction. The Worker changes D1, records the receipt and ordered change, and advances sequence state in one D1 transaction. A replica changes its title and checkpoint in one SQLite transaction.

The request operation is `task.updateOpenzaFields`. It reuses the mutation envelope but requires a positive `baseEntityVersion` and permits exactly this payload:

```json
{
  "title": "Validate one renamed task"
}
```

For the golden task at version `1`, acceptance creates entity version `2` and an ordered `task.updatedOpenzaFields` change. That change contains only the task `id` and accepted `title`. An identical retry returns the original durable receipt with `outcome: "duplicate"`; it does not allocate another version or change. A missing task returns `entity_not_found`. A stale base version returns non-retryable `version_conflict` and cannot overwrite the accepted title. The desktop only acknowledges an update outbox row when the receipt version equals `baseEntityVersion + 1`.

Executable coverage includes shared C#/TypeScript canonical fixtures, local source rollback, D1 rollback, lost-response retry, process restart, ordered pull, replica rollback and restart, duplicate replay, missing entity, malformed or additional fields, oversized title, and stale-version rejection. The persisted K5 D1 database is migrated in place and retains its original task and receipt before the rename is applied.

The Web page remains strictly read-only; it merely displays the latest accepted D1 record on its next successful refresh. At this title-only checkpoint, completion state, workflow status, description, dates, labels, hierarchy, deletion, provider tasks, multi-device conflict resolution, production authentication, Cloudflare account resources, deployment, and release remain outside scope. Section 17 separately admits workflow status.

## 17. Second post-K5 mutation: workflow status

This checkpoint extends `task.updateOpenzaFields` with exactly one additional scalar field. A workflow-status mutation permits exactly this payload:

```json
{
  "workflowStatus": "next"
}
```

Supported values are `none`, `inbox`, `next`, `waiting`, and `someday`, matching the local Openza workflow states for an open task. `completed` and `cancelled` are completion states and remain unsupported. A request containing both `title` and `workflowStatus`, or any other field, is rejected. The source desktop applies the status and appends its immutable outbox intent in one SQLite transaction.

For the golden task at entity version `2`, acceptance produces entity version `3` and the next ordered `task.updatedOpenzaFields` change. The change payload contains only task `id` and `workflowStatus`. An identical retry returns the original receipt as `duplicate`; a stale base version returns non-retryable `version_conflict`. The replica applies the status and advances its checkpoint together, while preserving the previously accepted title.

The local D1 migration expands the stored workflow-status allowlist without altering the existing task, title, receipts, changes, or sequences. The read-only Web endpoint accepts all five supported workflow states and displays the accepted value; browser mutation controls remain absent.

Executable coverage includes shared canonical fixtures, all supported status values at the contract boundary, rejection of combined and unsupported fields, source and D1 rollback, receipt-version validation, stale-write rejection, ordered pull, replica rollback and restart, duplicate replay, migration from the title checkpoint, and process-level Worker restart. At this workflow-status checkpoint, local priority, completion/reopen, other fields, Web mutations, production authentication, Cloudflare account resources, deployment, and release remain deferred. Section 18 separately admits local priority.

## 18. Third post-K5 mutation: local priority

This checkpoint extends `task.updateOpenzaFields` with one more Openza-owned scalar. A priority mutation permits exactly this payload:

```json
{
  "priority": 1
}
```

Priority is the existing Openza integer scale: `1` through `4`, corresponding to Urgent, High, Normal, and Low in the product. Values outside that range, non-integers, and requests combining priority with title or workflow status are rejected. The checkpoint does not reinterpret provider priority and does not add a new ranking system.

For the golden task at entity version `3`, acceptance produces version `4` and the next ordered `task.updatedOpenzaFields` change. The change payload contains only task `id` and `priority`. The source desktop changes its local priority and appends the immutable outbox intent in one SQLite transaction. D1 changes priority, records the receipt and change, and advances sequence state atomically. The replica changes priority and checkpoint together while preserving the accepted title and workflow status.

The D1 migration adds a constrained priority column with default `2`, preserving the existing task and all prior receipts, changes, versions, and sequences. The read-only Web endpoint displays the accepted numeric priority. An identical retry after process restart returns the durable receipt as `duplicate`; a stale base version cannot overwrite a newer priority.

Executable coverage includes shared C#/TypeScript canonical fixtures, priorities `1` through `4`, invalid and combined-field rejection, source and D1 rollback, receipt-version validation, stale-write rejection, ordered pull, replica rollback and restart, duplicate replay, fresh and in-place migration, and a process-level Worker restart. At this priority checkpoint completion/reopen remained deferred; section 19 separately admits completion. Dates, labels, hierarchy, deletion, provider tasks, Web mutations, production authentication, Cloudflare account resources, deployment, and release remain deferred.

## 19. Fourth post-K5 mutation: complete one local task

This checkpoint adds the first explicit lifecycle command, `task.complete`. It accepts exactly one payload property:

```json
{
  "completedAt": "2026-09-13T06:38:00.000Z"
}
```

`completedAt` is the actual client-observed completion time in the protocol's millisecond UTC format. The source task must be local, Openza-owned, and open. Its local completion state, timestamp, immutable outbox intent, and next device sequence commit in one SQLite transaction. The command deliberately retains title, workflow status, and priority so completion does not erase the context needed by a later reopen checkpoint.

For the golden task at entity version `4`, acceptance produces version `5` and the next ordered `task.completed` change. The change payload contains task `id`, `completionState: "completed"`, and `completedAt`. D1 advances the task, receipt, ordered change, cloud sequence, and device sequence transactionally. An identical mutation retry returns the original receipt as `duplicate`; a stale base version returns `version_conflict`; a new completion command for an already-completed task returns `invalid_transition`.

The replica applies completion state, completion time, applied-change record, and checkpoint in one SQLite transaction. The read-only Web endpoint displays the completed state and timestamp but gains no write control. Shared fixtures, source and D1 rollback, receipt-version validation, stale and invalid-transition rejection, ordered pull, replica rollback/restart, duplicate replay, fresh and in-place migration, and process-level Worker restart are executable evidence.

This checkpoint covers local tasks only. Section 20 separately admits `task.reopen`. Dates, labels, hierarchy, deletion, provider-task command delivery, Web mutations, production authentication, Cloudflare account resources, deployment, and release remain outside scope.

## 20. Fifth post-K5 mutation: reopen one local task

This checkpoint completes the first local lifecycle pair with `task.reopen`. It accepts exactly one payload property:

```json
{
  "reopenedAt": "2026-09-13T06:40:00.000Z"
}
```

`reopenedAt` records when the client performed the semantic command. The source task must be local, Openza-owned, and completed. Its local state returns to `open`, `completedAt` becomes JSON/SQL null, and the state change, immutable outbox intent, and next device sequence commit in one SQLite transaction. Title, workflow status, and priority are unchanged, so a task completed from Next returns to open in Next rather than losing its working context.

For the golden task at entity version `5`, acceptance produces version `6` and the next ordered `task.reopened` change. Its task payload contains `id`, `completionState: "open"`, and `completedAt: null`. D1 advances the task, receipt, change, cloud sequence, and device sequence transactionally. An identical retry returns the original receipt as `duplicate`; a stale base version returns `version_conflict`; a new reopen command for an already-open task returns `invalid_transition`.

The replica clears completion and advances its applied-change record and checkpoint in one SQLite transaction. The read-only Web endpoint reflects the open state without adding a write control. Shared fixtures, exact null handling, source and D1 rollback, receipt-version validation, stale and invalid-transition rejection, ordered pull, replica rollback/restart, duplicate replay, fresh and in-place migration, and process-level Worker restart are executable evidence.

This checkpoint covers local tasks only. Dates, labels, hierarchy, deletion, provider-task command delivery, Web mutations, production authentication, Cloudflare account resources, deployment, and release remain outside scope.
