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

When Personal Cloud is connected, new tasks include their labels in workspace
creation or legacy per-field creation intents. Backfilling an already published
task queues a separate add-label intent for each new attachment. Category colors
are local presentation metadata; the cloud label relationship carries ID/name.
