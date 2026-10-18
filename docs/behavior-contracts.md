# Task behavior contracts

These contracts state the intended product meanings. Current behavior, exceptions and unresolved decisions are listed separately. Avalonia is the desktop interaction reference; the Web companion is online-first and shares semantics without identical screens or offline guarantees.

## Durable meanings

- Capture and complete tasks locally without connecting a provider or Personal Cloud. Spaces, projects, labels and planning are optional.
- Task-editor label edits that are equivalent under the editor's case-insensitive, order-independent comparison are a no-op: change detection and save requests use the same comparison. The editor never renames shared labels. Deliberate renaming belongs to a future dedicated label list screen, which is not implemented. See [label editing](label-rename.md).
- Project and workflow status are independent. Assigning a project never changes status; choosing a status never changes project. Inbox is the default capture status; None explicitly means no workflow status. Workflow and completion are separate. Workflow statuses stay fixed: none, inbox, next, waiting and someday.
- Spaces are user-owned visibility scopes. Provider identities and personal routing conventions do not define them.
- Completing preserves the task and its recurrence definition; reopening makes it open again. Neither action silently chooses another workflow status.
- Date means intended work; Deadline means the outcome's latest acceptable day/time. Scheduled work, recurrence and reminders need their own explicit meanings.
- Personal Cloud is optional reconciliation. Provider credentials stay on desktop; cloud task data is access-controlled, not end-to-end encrypted.
- Recovery preserves local work and pending intent. Package-local restore points are recovery aids; durable backup means OneDrive app-folder backup or explicit export. Restoring SQLite does not restore credentials or a cloud deployment.

## Current behavior and decided Inbox contract

| Area | Current behavior or limit |
| --- | --- |
| Inbox view | Includes only open tasks whose workflow status is inbox and which have no Openza project. Project absence and workflow status remain separate fields. |
| Inbox badge | Follows workflow status. A project-assigned Inbox-status task is intentionally unprocessed within that project, and its Inbox badge is correct even outside the Inbox view. Project assignment and status selection do not change each other. This contract was decided on 2026-09-11 and shipped in Linux 0.1.9; this cleanup preserves it without runtime changes. |
| Capture | A title is required and trimmed; ordinary local capture defaults to inbox and can omit a project. Hosts still have direct store call sites. |
| Provider adoption | Creates a linked local wrapper. Provider identity, snapshots, description and source context remain provider-owned. |
| Local enhancements | Notes, labels, local priority, project and workflow generally survive provider refresh. Refresh has explicit date/hierarchy exceptions below. |
| Provider writes | Completion/reopen and bounded Todoist Date changes for non-recurring source tasks exist. Arbitrary provider title, notes, labels or deadline editing is not implied. |
| Recurrence | Metadata is stored; local completion does not create another occurrence. A provider can advance and reopen the same wrapper. No universal recurrence engine or reminder service is established. |
| Windows continuity | Active on Ubuntu through C# compilation, design and synthetic/integration tests, behind its Dev gate. Native build/validation, packaging/distribution, MSIX upgrade testing and Store replacement are not current priorities. Upgraded-user reconnect and initial sync-off policies remain open; this work does not choose them. |

## Engineering evidence and preservation rules

The [scheduling contract](scheduling-and-recurrence.md) details current day/instant, scheduled-start, recurrence and completion behavior on this branch.

[Application operations](../src/Openza.Tasks.Application/Tasks/TaskApplicationService.cs) and [requests](../src/Openza.Tasks.Application/Tasks/TaskRequests.cs) implement local capture and selective edits. [List-row state](../src/Openza.Tasks.Desktop/ViewModels/TaskListItemViewModel.cs) derives badges from workflow status; [SQLite queries](../src/Openza.Tasks.Core/Data/SqliteTaskStore.cs) implement Inbox membership. [Models](../src/Openza.Tasks.Core/Models/TaskStatus.cs) distinguish workflow from open/completed/cancelled completion state.

Provider refresh in SqliteTaskStore.RefreshAdoptedTaskFromSourceAsync may update local planning when enabled and no Date write is pending; recurring source deadlines can refresh local deadlines. Adopted children can inherit parent space/project/workflow under existing conditions. Preserve these conditions during refactoring rather than replacing a whole task with provider data.

[Provider write-back planning](../src/Openza.Tasks.Core/Sync/ProviderWriteBackPlanner.cs) and the store retain atomic local completion plus pending provider intent. Pending local/cloud/provider completion intent protects against conflicting refresh. A local acknowledgement is not proof of external delivery.

[Cloud sync](../src/Openza.Tasks.Core/PersonalCloud/PersonalCloudSyncClient.cs) and the [protocol](personal-cloud-protocol.md) retain semantic mutations, durable outbox, idempotency, base versions, tombstones and checkpoints. Stale versions can conflict; do not silently overwrite accepted data. Pulled state and checkpoint persist atomically. Initial upload completion accounts for frozen seed, catch-up and deferred changes.

Use the [initial-sync notes](personal-cloud-initial-sync.md) for recovery mechanics and the [Windows continuity plan](windows-transition.md) for active Ubuntu work and later package/vault acceptance. Test offline restart, delayed acknowledgement, duplicate/stale replay, crash recovery, tombstones, restore with pending intent and missing/revoked pairing in isolated stores. Tests and source compilation do not establish live-provider or packaged acceptance.
