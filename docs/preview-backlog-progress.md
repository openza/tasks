# Openza Tasks Preview Backlog Tracker

This is the single progress tracker for the Preview backlog work on `Develop Tasks app for Windows`.

Last updated: 2026-05-23
Branch: `feature/preview-backlog-daily-use`

## Status Key

- `Done`: Implemented and build/test verified.
- `Remaining`: Not done yet or needs a new implementation pass.
- `Deferred`: Intentionally left out of this pass.

## Current Priority

The current priority is to try the implemented Tasks/Projects startup and row behavior in the app:

- App should open directly into the correct task list without sitting on a stale or empty Inbox for minutes.
- Sidebar menu clicks should show the selected list and should not later be overwritten by an older sync refresh.
- Task and project lists should scroll smoothly.
- Collapsed groups should stay stable.
- Task rows should not jump after completion, details edits, or quick row edits.
- Row action buttons should not shift row text.
- Task title and metadata text should be selectable for copying.

## Done

These items are implemented and build/test verified. If app usage still shows issues, track those as new `Remaining` items.

- `Done` Startup should not block on backup, OneDrive upload, or automatic sync.
  - First view selection and task refresh now run before startup maintenance.
  - Startup backup, pending OneDrive upload, and automatic provider sync now run after first load.
- `Done` Sidebar/task refresh should not repaint stale data after sync.
  - Task refreshes are versioned so older refresh results cannot overwrite newer sidebar selections.
- `Done` Restore open task details when returning to a sidebar list.
  - The app remembers the open task per task-list key.
  - Returning to a list reloads and reopens that task if it still belongs to the list.
  - Lists without a remembered open task hide the previous list's details pane.
- `Done` Faster task refresh and sidebar counts.
  - Added `TaskListRefreshSnapshot` so visible tasks, all-space task context, and counts come from one task read.
  - Task refresh updates navigation counts and project counts from the same snapshot.
  - Task and project search refreshes are debounced.
- `Done` Virtualized collapsible task groups.
  - Task groups now render as one flattened `ListView`: group header rows plus task rows.
  - Collapsing a group removes task rows from the flat list and preserves expansion state while the page is active.
- `Done` Virtualized Projects pane.
  - Projects now render as one flattened `ListView`: provider group headers plus project rows.
  - Project group expansion state is preserved across project list refreshes.
- `Done` Stable task rows.
  - Task row view models update in place by task id.
  - Completion slide/fade animation was removed.
  - Checkbox state now relies on stable row updates instead of row motion.
- `Done` Native row actions.
  - Removed the row copy-title button.
  - Task title and metadata text are selectable where WinUI allows it.
  - Row action strip reserves fixed width.
  - Direct row actions: Date, Project, Labels, Status, Priority, Move Space, Delete.
  - Row actions save immediately through AppShell helpers.
- `Done` Task/list/detail state preservation.
  - Task selection and details are no longer cleared just because the user switches sidebar menu items.
- `Done` Inbox clarification flow.
  - If a selected Inbox item disappears after clarification, the next visible Inbox item is selected.
- `Done` Move task to another Space.
  - Completed earlier on `feature/move-task-between-spaces` and merged into `main`.
- `Done` OneDrive backup list refresh after backup actions.
  - Upload, pending upload, local restore, and OneDrive restore paths refresh the cloud backup list.
- `Done` Hide repeated visible `Ctrl+N` shortcut hint.
  - App shell hides keyboard accelerator placement.
- `Done` Details pane copy actions.
  - Details menu can copy task title, notes, source text, and metadata.
  - Row copy button was intentionally removed; row text copying should use text selection.
- `Done` Task metadata display.
  - Local task details show Created and Modified.
  - Provider/source section shows Created for linked source tasks.
- `Done` Todoist/source recurring metadata display.
  - Provider/source section shows recurrence metadata when present.
- `Done` Projects pane defaults to Active projects.
- `Done` Project row folder icon quieted.
  - Project rows use a quiet dot instead of the previous folder glyph.
- `Done` Sort direction.
  - `TaskQuery` supports `TaskSortDirection`.
  - View settings persist sort direction.
  - Unit coverage verifies title and created-date direction behavior.

## Remaining

These are not implemented yet, or require a new decision before implementation.

- `Remaining` Add startup/refresh timing diagnostics if startup or sidebar switching still feels slow.
  - Measure first task refresh, provider source load, project list build, and automatic sync duration.
- `Remaining` Decide compact-width behavior for row actions.
  - If all direct row buttons do not fit at narrow widths, move lower-risk actions into a more menu only for compact widths.
- `Remaining` Revisit "Tasks vs Projects" naming.
  - Do this after Projects pane and task grouping polish has settled.

## Deferred

These are intentionally not part of the current speed/row-polish pass.

- `Deferred` Manual drag/drop sorting with persisted `sort_order`.
- `Deferred` Native recurring tasks.
- `Deferred` Larger subtask expansion work beyond current source/local support.
- `Deferred` Attachment support.
- `Deferred` Microsoft To Do parity.
- `Deferred` Todoist post-import behavior beyond current routing support.
- `Deferred` Provider completion comments and provider-side moves.
- `Deferred` Obsidian importer comparison.
- `Deferred` Themes.
- `Deferred` Sync server.
- `Deferred` Notion sync.
- `Deferred` Obsidian sync.
- `Deferred` Mobile PWA.
- `Deferred` New Space creation polish.

## Verification

- `Done` `dotnet test src\Openza.Tasks.Tests\Openza.Tasks.Tests.csproj -c Release --no-restore`
  - Latest result: 104 passed.
- `Done` `dotnet build src\Openza.Tasks\Openza.Tasks.csproj -c Release -p:Platform=x64 --no-restore -p:OutDir=E:\Personal\sw-projects\openza\tasks\build\codex-winui-out\`
  - Latest result: succeeded with 0 warnings and 0 errors.
- `Done` `git diff --check`
  - Latest result: clean.
