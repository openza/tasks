# Microsoft To Do categories

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
Incoming cloud attachments always retain their exact IDs, including legacy IDs
shared with a provider catalog. Hydration never remaps an attachment by name.
Desktop retains existing chips by ID; ambiguous CLI label names require an exact
ID. Category adoption matches only local labels, never provider-catalog names,
and does not add a second label when the task already has that name attached.

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
