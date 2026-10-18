# Openza Tasks Architecture

## Direction and contract authority

The direction adopted on 2026-10-18 is a simple local-first task app that grows with the user, with an optional cloud they control. Avalonia is the one desktop UI receiving active development. Windows is the major target, Linux is supported, and macOS is deferred. WinUI receives necessary maintenance as the current Windows Store host until the [transition gates](windows-transition.md) are accepted; this decision does not establish that replacement is ready.

Lightweight [behavior contracts](behavior-contracts.md) are the product source of truth. Avalonia is the practical reference for ordinary desktop interactions. Existing protocol documents retain their detailed wire contracts; historical staged checkpoints are not a complete description of today's UI. Resolve discrepancies explicitly rather than copying behavior between hosts blindly.

| Layer | Responsibility |
| --- | --- |
| Desktop | Complete local task experience, provider connections, offline operation and OS integration |
| Optional Personal Cloud | Sync, secure remote access and selected future background capabilities in the user's Cloudflare account |
| Web companion | Online-first capture, viewing and useful task changes away from desktop; shared semantics without identical screens or assumed desktop offline guarantees |

Keep .NET 10 Core and Application UI-independent. Preserve semantic operations, durable outbox, idempotency, field ownership, tombstones and checkpoints. Keep Cloudflare provisioning/infrastructure separate from task logic where practical, without a generic multi-cloud framework. Future reminders, automation and server visibility need separate designs; task data is currently access-controlled, not end-to-end encrypted, and provider tokens stay on desktop.

Openza Tasks is designed around three capabilities:

- local-first task management
- connected task apps such as Todoist and Microsoft To Do
- future sync routes that can run inside Openza or headlessly

The SQLite schema is internal to the app. It is not a public plugin contract yet, but it should stay clean enough to support the planner and sync-engine roadmap.

## Application Hosts

Openza Tasks has two application hosts over the same cross-platform Core:

- `Openza.Tasks` is the Windows-native WinUI 3 app and remains the current Windows Store distribution during maintenance/transition.
- `Openza.Tasks.Desktop` is the Avalonia app and active desktop implementation reference. It owns desktop presentation and platform integrations without introducing Avalonia dependencies into Core or Application.
- `Openza.Tasks.Application` owns shared application operations and runtime coordination over Core. Hosts still have direct store call sites; this is not a claim that presentation orchestration is already fully separated.

The hosts do not share UI code. Data models, SQLite storage, provider synchronization, import/export, and backup behavior belong in `Openza.Tasks.Core` when they are platform-neutral. Credentials, notifications, launch behavior, packaging, and other OS integrations remain host-specific.

On Linux, `Openza.Tasks.Desktop` stores application data below the platform local-data directory, uses the freedesktop Secret Service through `secret-tool` for provider credentials, and creates daily SQLite restore points. The public Linux distribution is one strict `openza-tasks` Snap containing the Avalonia host and CLI. Inside the Snap, both hosts share `$SNAP_USER_COMMON/tasks`, which survives automatic refreshes, while credentials remain in the desktop Secret Portal. Snap packaging is validated independently from the Windows Store lane.

## Spaces

Spaces are Openza-owned scopes for separating a person's work contexts, such as Work and Personal. A space is not a provider account, not a workspace account, and not a Todoist/Microsoft list. It is the boundary for what the app shows.

V1 defaults task and project discovery to the selected space. The selected space scopes:

- smart lists and task counts
- projects
- task search and filters
- connected-app intake
- import/export surfaces

Global search is available as a temporary navigation overlay. It starts in Current Space and only searches across spaces when the user explicitly switches the scope to All Spaces. This keeps Openza safe for practical use cases such as screen sharing at work without accidentally showing personal tasks. User-specific routing, such as mapping Todoist labels to spaces, belongs in import/setup scripts or future configurable rules, not hardcoded app behavior.

For a clean new database, Openza creates one default space. If setup/import tooling creates spaces first, the app does not add an extra empty default space.

Store V1 is a fresh-start WinUI product. It does not auto-migrate old local app stores; future import tooling can be designed as an explicit user action if needed.

## Task Model

Tasks separate completion from workflow:

- `completion_state`: whether the task is open, completed, or cancelled
- `workflow_status`: whether an open task is in Inbox, Next, Waiting For, Someday, or no workflow list

Projects are user-facing task lists/projects owned by Openza. Local tasks can have no project. The default Inbox workflow status means newly captured, not yet organized; project absence and workflow status are separate fields. Provider project/list names are source context, not primary Openza projects. Statuses remain the fixed set `none`, `inbox`, `next`, `waiting`, `someday`; custom statuses are not planned without concrete workflow evidence.

Planner-ready fields are present even when the UI only exposes part of them:

- `planned_on` / `planned_at`
- `deadline_on` / `deadline_at`
- `scheduled_start` / `scheduled_end`
- `duration_minutes`
- `recurrence_rule`

## Dated Work Facets

Openza treats dates, deadlines, scheduled blocks, and repeating work as facets on the same task model. They are not separate task stores and they are not copied from any one external workflow implementation.

- `planned_on` is the user-facing **Date**: the day the user intends to work on or see the task.
- `planned_at` is the optional exact time for that Date, when the user adds one.
- `deadline_on` is the user-facing **Deadline**: the last acceptable day for the outcome.
- `deadline_at` is the optional exact time for that Deadline, when the user adds one.
- `recurrence_rule` marks repeating/routine work.
- `scheduled_start` and `scheduled_end` are reserved for stronger time-block/calendar semantics.

Smart lists such as Today, Overdue, and Calendar show all dated work together. The everyday UI does not expose a schema-shaped picker for Date versus Deadline versus scheduled blocks; users should not need to know the database fields to trust the list.

- Today includes work dated for today, deadlines due today, and scheduled blocks today.
- Calendar includes all dated work.
- Overdue includes dated or deadline work before today.

Repeating work is an independent facet. Users can choose whether a smart list includes repeating tasks:

- Include repeating
- Exclude repeating
- Only repeating

This lets Todoist, Microsoft To Do, Notion, Obsidian, and local Openza tasks flow into one task model while still letting users separate hard commitments from routines when they need to. For example, a user who treats recurring Todoist dates as real calendar work can include them, while a user who separates hard landscape from routines can filter repeating tasks out.

## List Grouping

Grouping is a display preference, not a workflow rule. It does not change tasks, projects, labels, statuses, spaces, or provider links.

Users can group the current task list by:

- date
- project
- status
- priority
- label
- source
- repeating
- created date
- completed date

Sorting still applies inside each group. A task with multiple labels can appear in multiple label groups, because labels are flexible user-owned facets rather than a single hierarchy.

Default grouping stays light:

- Calendar and Overdue group by date.
- Tasks groups by project.
- Completed groups by completed date.
- Inbox, Next Actions, Today, Waiting For, and Someday start ungrouped.

The user can override grouping per smart list. These preferences live in app settings rather than the SQLite task schema.

## Wrapper Pattern

Connected tasks use a wrapper pattern:

- provider sync writes `provider_source_items`
- users add selected source items to Inbox
- adding creates a local Openza task wrapper linked back to the source item
- source items are shown only in their suggested/current space intake

Provider-owned source fields are refreshed into source snapshots and the wrapper's source context. Do not treat this as permission to overwrite every similarly named local field:

- source identity: `source_integration_id`, `source_connection_id`, `source_external_id`, `source_provider_task_id`
- provider task content: title, description, source project/list name, source priority, source Date/Deadline and completion state (subject to pending local intent)
- provider snapshot data in `provider_source_items.snapshot_json` and task `source_metadata`

Openza enhancements generally survive provider refresh, subject to the explicit date/hierarchy exceptions in the [behavior baseline](behavior-contracts.md):

- `workflow_status`
- local Openza `project_id`
- local Openza `priority`
- planner fields: `planned_on`, `planned_at`, `deadline_on`, `deadline_at`, `scheduled_start`, `scheduled_end`, `duration_minutes`
- local notes and local metadata
- local labels

This keeps Todoist and Microsoft To Do useful as sources while preserving Openza enhancements. Current write-back includes completion/reopen and a bounded Todoist Date update path for non-recurring source tasks; see `ProviderWriteBackPlanner` and the [behavior contracts](behavior-contracts.md). Do not generalize it into arbitrary provider field editing.

## Sync Routes

The V3 schema already includes route-level sync tables:

- `provider_connections`
- `provider_source_items`
- `sync_routes`
- `sync_route_mappings`
- `sync_item_links`
- `sync_field_state`
- `sync_operations`
- `sync_runs`

The current Todoist and Microsoft To Do integrations do not yet populate the full route ledger. They use source snapshots, explicit add-to-Inbox, and the completion/reopen outbox. Future routes such as Todoist to Obsidian should use `sync_item_links`, `sync_field_state`, and `sync_operations` instead of adding provider-specific columns to `tasks`.
