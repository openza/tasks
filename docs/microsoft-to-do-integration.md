# Microsoft To Do import

Openza imports Microsoft To Do lists and tasks as provider source items. Adopting a task creates an ordinary local Openza task linked to that source.

## Checklist items (steps)

The provider requests `$expand=checklistItems` when listing tasks to avoid a request per task. If Graph rejects expansion (400 or 501), Openza retries without it and disables expansion for the remaining pages and lists in that fetch, including encoded continuation URLs. Missing collections stay absent from the snapshot: there is no per-task request fan-out, and the next sync retries expansion. Such tasks remain eligible for one-time step import when Graph supplies checklist data. Task pages and expanded checklist continuation pages follow `@odata.nextLink`; repeated links and cycles fail the snapshot rather than publish incomplete checklist data. An HTTP failure, including throttling on a checklist continuation page, never becomes an empty checklist.

The snapshot retains each checklist item's display name, checked state, checked timestamp and Graph array order in `msToDo.checklistItems`. On adoption, items become ordinary local child tasks (`ParentId`) displayed in the inspector's **Subtasks** section, not a separate steps model. Checked items are completed; `checkedDateTime` supplies completion time when available, otherwise import time is used. Child tasks have no provider identity or source link of their own and are never updated by Microsoft sync.

Graph order is encoded with strictly increasing, one-second-spaced creation timestamps, all earlier than import time. The desktop inspector sorts all children oldest first, then by ID for equal timestamps, without grouping by completion or sorting by priority/date. Mixed checked and unchecked steps remain in their original Graph order, and newly added local children go at the bottom. This also applies to existing local subtasks: checking or changing their priority/date no longer moves them within the inspector. Parent-row progress remains a completed/total count. There is no sibling-order column or new cloud contract; cloud descriptors already carry creation timestamps, and the Personal Cloud companion renders children in its existing creation-time ascending order. Other task views can use different sorts. Graph's returned order may itself differ from the order displayed in Microsoft's app. Creation times represent import ordering, not original checklist creation times.

Import sets `msToDoStepsApplied` in the parent task's source metadata. Previously adopted tasks receive a one-time backfill on their next provider refresh; existing local children are retained. The backfill adds checklist children without matching by title, since different checklist items can legitimately share a name. Once marked, later provider changes cannot rename, recheck, recreate or delete children. User edits and child deletions persist, including after legacy cloud bootstrap replacement and source disappearance/relink. Detachment clears provider identity and source context but retains the applied marker. Snapshots without checklist data remain unmarked so a fresh sync can import them; explicit empty checklists are marked.

To Do adoption and backfill write only local child rows and the applied marker. They never resolve a cloud target, take a Personal Cloud authoring lease, or queue cloud mutations. Normal Personal Cloud sync downloads capabilities and the workspace baseline first, then publishes unpublished children through existing task-create, parent-relationship and completion mutations, with consecutive per-entity versions in workspace and legacy modes. This also avoids seeding workspace metadata before an upgraded cloud's versions have downloaded. Cloud catalog adoption applies the same one-time mapping. No changes to Todoist source-label or task behavior are made.

## Reminders

Microsoft To Do reminders import once into an editable local Reminder field. Enabled
reminders are converted to UTC using their IANA or Windows time zone. Explicitly
disabled reminders mark the import complete without setting a reminder. Cached
snapshots without reminder fields, malformed dates and unknown zones leave the
import pending for a later fetch; unknown zones are logged and skipped.
Reminder instants outside the supported storage range are logged and skipped,
with the import marked complete because retrying cannot make them representable.

Previously adopted tasks are backfilled once, without replacing an existing local
reminder. The independent `msToDoReminderApplied` marker survives provider refresh,
detachment/relink and cloud hydration. Later Microsoft changes do not overwrite
local reminder edits or restore a cleared reminder after import.

Reminders stay on this device and do not produce Personal Cloud edits. Cloud pulls
and baseline replacements preserve the local reminder and its import marker.
Backup/restore and task export/import retain reminder definitions. Desktop's
inspector provides date, time and Clear controls. CLI accepts `--reminder` with an
ISO 8601 instant including `Z` or a UTC offset, and `--clear-reminder` on updates.
On Linux and Windows, the running Desktop app sends native notifications for due
reminders on incomplete tasks, including imported reminders.

## Categories

Categories and checklist items are imported independently. The parent retains both
`msToDoCategoriesApplied` and `msToDoStepsApplied` when both mappings have run,
including across refresh, cloud hydration and source detachment/relink. A missing
collection leaves only its own import pending.

Microsoft To Do sync reads every page of lists, master categories and tasks.
Task snapshots retain category names and master-category colors (blue when the
master category is unavailable).

On adoption, categories become ordinary local Openza labels. Names are trimmed
and matched case-insensitively against local labels only; duplicates are ignored.
Existing local labels retain their spelling and color. Provider catalog labels
are never attached to the local task.

Categories are applied once, recorded in the adopted task's source metadata.
Previously adopted tasks without that marker receive their current categories
on the next sync, without removing existing labels. Afterward, changing provider
categories does not change local labels, and removing an imported label sticks.
Todoist labels remain source metadata and are not promoted.

A task has at most 32 attached labels, matching the Desktop workspace protocol.
Category import keeps existing labels and adds categories in snapshot order until
that limit is reached. Extra categories are skipped and the import is marked
complete; it does not retry them after a later label removal.
Category adoption matches only local labels, never provider-catalog names,
and does not add a second label when the task already has that name attached.
Workspace hydration and label editing retain ordinary Openza behavior.

Category imports never stage unpublished tasks or workspace metadata. Normal
Personal Cloud sync downloads capabilities and cloud versions first, then includes
new tasks' local labels in workspace creation or legacy per-field creation intents.
Provider sync leaves previously published tasks unchanged and unmarked, including
while disconnected. After Personal Cloud pulls current task versions, it retries
cached categories and queues a separate add-label intent for each new attachment
under the authoring lease. Tasks with pending edits or unresolved conflicts wait
for a later successful pull. This payload atomically upserts the label by ID/name
and never references a Space or project. Category colors
are local presentation metadata; the cloud label relationship carries ID/name.
