# Task behavior baseline

Adopted 2026-10-18; source inspection at Tasks `5637e3a`. These lightweight contracts define important product meanings. Avalonia is the implementation reference for ordinary desktop interactions. The online-first Web companion shares these meanings without identical UI or desktop offline guarantees. The detailed [Personal Cloud protocol](personal-cloud-protocol.md) retains wire-level authority; its staged history must be read alongside current source and [Windows evidence](windows-continuation.md).

## Capture and organization

A desktop must support durable local capture without a provider or Personal Cloud connection. Add a task, finish it: Spaces, projects, labels and planning are optional organization tools, not prerequisites. Inbox means **newly captured, not yet organized**. It is a neutral default, not a judgment or a required GTD process.

Current anchors: [`TaskApplicationService`](../src/Openza.Tasks.Application/Tasks/TaskApplicationService.cs) trims and requires a nonempty title, resolves a space, permits no project and writes local state; [`CreateTaskRequest`](../src/Openza.Tasks.Application/Tasks/TaskRequests.cs) defaults to Inbox. Cloud-connected authoring uses supported semantic intents and retains a compatibility path for older cloud capabilities. Hosts still contain direct store calls; this service is not yet the only entry point.

Completion and workflow are independent. The fixed workflow set is `none`, `inbox`, `next`, `waiting`, `someday`; completion is `open`, `completed`, `cancelled` ([models](../src/Openza.Tasks.Core/Models/TaskStatus.cs)). A task without a project is not necessarily in Inbox. Custom statuses require concrete workflow evidence and a deliberate product decision. Spaces are user-owned visibility scopes; provider identity and personal routing conventions must not define them.

## Completion and provider ownership

Completing records completion rather than deleting the task or its recurrence definition. Reopening makes the task open and clears its completion timestamp. Do not turn either action into an implicit workflow/status move. Local, provider and cloud paths must preserve their respective transactional intent rules.

The [store](../src/Openza.Tasks.Core/Data/SqliteTaskStore.cs) supports atomic local completion plus pending provider write-back. Cloud paths persist semantic completion/reopen intents; retries reuse mutation identity rather than inventing another operation. [`ProviderWriteBackPlanner`](../src/Openza.Tasks.Core/Sync/ProviderWriteBackPlanner.cs) limits completion write-back to tasks with writable provider identity. A local acknowledgement is not proof that the provider accepted it.

| Authority | Current boundary and exceptions |
| --- | --- |
| Provider source | Source identity, snapshots, description and provider context remain owned by the adapter/provider. Adoption creates a linked local wrapper; it is not a free-form provider editor. |
| Openza enhancements | Local notes, labels, local priority and project/workflow choices generally survive refresh. Do not overwrite them by copying a whole provider object. |
| Completion | Refresh respects pending local completion/cloud/provider commands before accepting provider completion state. Track intent and delivered result separately. |
| Dates and hierarchy | Refresh is intentionally conditional: source Date can update planned fields when enabled and no Date write is pending; recurring source deadlines refresh deadline fields. Adopted children can inherit parent space/project/workflow under the store's existing conditions. These are exceptions to blanket ownership claims. |
| Provider writes | Completion/reopen plus bounded Todoist Date updates for non-recurring source tasks exist today. This does not authorize arbitrary title/notes/labels/deadline write-back. |

The precise refresh anchor is `RefreshAdoptedTaskFromSourceAsync` in the store. Preserve its conditions during extraction/refactoring. Changes to authority require explicit contracts and tests, not just a UI edit.

## Sync, conflicts and recovery

Each desktop has a durable local copy. Personal Cloud is optional reconciliation, not a prerequisite for basic local operation. Core owns task persistence and protocol state; Application coordinates shared operations; hosts own platform presentation and services. Provider credentials stay on desktop, behind `ICredentialStore`. Cloud task data is access-controlled, **not end-to-end encrypted**.

Current cloud anchors: [`PersonalCloudSyncClient`](../src/Openza.Tasks.Core/PersonalCloud/PersonalCloudSyncClient.cs), store partials and [protocol](personal-cloud-protocol.md). Preserve semantic mutations, durable outbox, idempotency receipts, explicit base versions, tombstones and persisted checkpoints. A stale base version can produce `version_conflict`; do not silently overwrite accepted data or describe conflicts as automatic last-writer-wins. Applying pulled state and its checkpoint must remain atomic. Initial upload completion must account for the frozen seed, catch-up and deferred changes; sending a batch alone is not completion.

Recovery must preserve a consistent safety copy, local pending intent and connection state according to the selected recovery path. Local restore points are rollback aids inside app state; they are not durable backup. OneDrive app-folder backup or explicit export provides durable backup. Restore and reconnect are distinct actions: do not assume restoring SQLite also restores credentials, device identity or a cloud deployment. Use the existing [Personal Cloud plan](personal-cloud-plan.md), [initial-sync notes](personal-cloud-initial-sync.md) and [Windows continuation](windows-continuation.md) for the detailed recovery paths and evidence limits.

Regression cases for subsequent behavior changes: offline capture/restart; completion/reopen with delayed provider acknowledgement; duplicate mutation replay; stale version rejection; crash between pull and checkpoint; tombstone replay; restore with pending mutations; missing/revoked pairing credentials. Use isolated stores and test deployments, never daily-driver data.

## Planning meanings and unresolved choices

| Term | Meaning/boundary |
| --- | --- |
| Date (`planned_on`, optional `planned_at`) | Intended day/time for doing or seeing a task. It is distinct from a deadline. |
| Deadline (`deadline_on`, optional `deadline_at`) | Last acceptable day/time for the outcome. |
| Scheduled session (`scheduled_start`, `scheduled_end`, `duration_minutes`) | Fields already exist. Stronger calendar/time-slot semantics, overlap rules and editor behavior still need a concrete design before wider use. |
| Recurrence (`recurrence_rule`, source recurrence context) | Repeating-work metadata exists. Completing an occurrence and removing recurrence are different intentions. A universal local/cloud/provider rollover policy is not established by merely storing the rule. |
| Reminder | No reminder domain model is established here. A definition and a notification delivery attempt must be separate concepts in a future design. |

Freeze durable meanings before expanding Personal Cloud deployments, not speculative empty schema columns. Decide recurring completion/rollover and scheduled-session semantics with concrete workflows. Reminders, server-visible scheduling data, encryption and content-dependent automation remain open design work. Do not infer that today's cloud performs them or that end-to-end encryption can be added without revisiting server capabilities.
