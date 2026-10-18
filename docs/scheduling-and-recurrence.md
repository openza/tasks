# Scheduling, recurrence and completion

This contract separates intended meanings from current behavior. It does not add a planner, recurrence engine or reminder service. See the [task behavior contracts](behavior-contracts.md).

## Intended meanings

| Concept | Durable meaning |
| --- | --- |
| Date | Intended day/time for doing or seeing a task. |
| Deadline | Latest acceptable day/time for the outcome; independent of Date. |
| Scheduled work | A work interval on the task, distinct from its intended Date and Deadline. |
| Duration | Duration metadata; storing it does not imply a session or completed work. |
| Recurrence | A repeating definition, separate from completing a task or occurrence. |

## Current behavior

- Today, Calendar and Overdue contain open tasks. They use Date, Deadline and scheduled **start**; scheduled end, overlap and duration alone do not determine membership. Overdue includes planned work and past starts, not only missed deadlines. A block starting yesterday and ending today is Overdue on the strength of its start.
- There is one optional start/end pair, no multi-session editor, overlap/conflict detection, inferred duration or session-completion workflow.
- Date-only values are calendar days. Exact values retain their instant and use the computer's local day for desktop display and dated views.
- Date pickers show the stored day, or the instant's local day for exact-only records. Loading, refreshing and saving an unchanged displayed day do not create a date mutation. Unrelated edits preserve exact time. Explicit clear/replacement drops the corresponding instant, including from the save baseline so a later edit cannot restore it.
- A local explicit same-day edit of an already paired day/instant can preserve matching time. An explicitly submitted cloud Date/Deadline mutation replaces its exact time; the desktop editor suppresses it when the displayed day is unchanged. Web date-only edits also replace time. Schedule/recurrence metadata has no editable workflow on Web.
- Local completion retains dates, schedule and recurrence; it creates no next occurrence. Reopen clears completion time and retains planning. A provider may advance and reopen the same wrapper with its next source date. Pending local/cloud/provider intent protects against conflicting refresh.
- Recurrence is opaque metadata. A non-null local value takes precedence over source recurrence; an empty/whitespace local value masks the source, while null falls back. Repeat filters test for non-whitespace and parse no rule grammar.
- Todoist Date write-back is limited to non-recurring source tasks. Dated recurring provider intake can enter Someday rather than Inbox. Ordinary local capture still defaults to Inbox; this compatibility behavior chooses no user methodology.

## Engineering evidence and boundaries

[Task/date models](../src/Openza.Tasks.Core/Models/TaskDateValues.cs) implement day/instant precedence: a stored day wins a mismatched instant; matching/absent day permits exact time. Ordinary SQLite upsert normalizes an exact-only pair, but reads retain stored values. Cloud wire records require date-only/exact alternatives to be exclusive.

[Application edits](../src/Openza.Tasks.Application/Tasks/TaskApplicationService.cs), [detail edit planning](../src/Openza.Tasks.Desktop/ViewModels/Editing/TaskDetailEditPlanner.cs) and [source-date choices](../src/Openza.Tasks.Desktop/ViewModels/Editing/TaskSourceDateChoice.cs) preserve omission versus explicit edits, current revision, rebase and acknowledgement metadata. The view model retains async gates, edit-version loops, selection and publication. [Provider write-back](../src/Openza.Tasks.Core/Sync/ProviderWriteBackPlanner.cs) owns bounded provider intent.

[List projection](../src/Openza.Tasks.Core/Data/TaskListProjection.cs) owns SQL-free membership, ordering, hierarchy and snapshot counts. [SQLite storage](../src/Openza.Tasks.Core/Data/SqliteTaskStore.cs) retains hydration, direct SQL counts and all write/outbox/checkpoint boundaries. SQL midnight bounds resolve each local date's offset independently. Synthetic boundary tests compare SQL/projection at previous-day, midnight, repeated-hour, last-second and next-midnight instants; process-local America/New_York checks cover 23/25-hour days without changing the machine timezone.

[Workspace validation](../src/Openza.Tasks.Core/PersonalCloud/WorkspaceContracts.cs) accepts nullable duration integers from 0 through 525600 and bounded recurrence text, not a shared recurrence grammar. Local model storage is not a universal validation API. Provider recurrence stays source-owned; it is not silently promoted into a local rule.

## Decisions before expansion

A schedule editor needs timezone, partial/reversed interval, overlap and multi-session rules. A recurrence engine needs grammar, occurrence identity, missed/skip/completion behavior, ownership and multi-device idempotency. Reminder definitions and notification delivery attempts need separate models. None is selected merely because metadata columns exist. Windows execution remains deferred under the [transition plan](windows-transition.md).
