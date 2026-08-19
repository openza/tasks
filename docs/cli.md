# Openza CLI

The CLI is a first-party interface to the same local task data used by Openza Tasks. Production is the only public channel; source commands always use the maintainer-only Dev channel.

```bash
./dev-cli.sh status
./dev-cli.sh task list --format json
./dev-cli.sh task add "Prepare release notes" --priority high --label release
./dev-cli.sh task update TASK_ID --status next --date 2026-08-16
./dev-cli.sh task complete TASK_ID
```

Commands:

```text
openza status
openza search <query>
openza task list|show|add|update|complete|reopen|delete
openza space list
openza project list
openza label list
openza sync status --provider todoist --direction push --scope pending
openza sync run --provider todoist --direction push --scope pending --yes
```

Use `--format text|json|tsv` on any leaf command. JSON responses have a stable `schemaVersion` and `data` envelope. Results go to stdout and diagnostics go to stderr.

The current JSON contract is `schemaVersion: 2` for successful results and structured errors. Version 2 reflects the finalized task detail/mutation shape and priority fields; the previously installed 0.1.0 CLI emitted version 1. Consumers must branch on `schemaVersion` rather than treating versions 1 and 2 as interchangeable. Successful responses use `data`; failures write an `error` object with the same schema version to stderr.

Single-task commands (`task show`, `add`, `update`, `complete`, and `reopen`)
return one object in `data`; list and search commands return arrays. With
`--format json`, failures use the same versioned envelope on stderr with an
`error` object containing `code`, `message`, `exitCode`, and `details`.

Exit codes are `0` success, `1` unexpected failure, `2` invalid arguments, `3` missing or ambiguous reference, `4` concurrent-edit conflict, `5` missing destructive confirmation, and `6` an operation restricted by the task's provider link. Task deletion requires `--yes` and accepts `--revision` for optimistic concurrency.

## Versioned TSV schemas

Every leaf command that accepts `--format tsv` writes a header even when there are no result rows. The first column is always `schema_version`, and every v1 record contains `1` in that column. Consumers should select a parser by that value and field names rather than relying on display text.

TSV v1 uses reversible backslash escaping for every string field. Decode fields from left to right after splitting on literal tab characters:

- `\\` represents one backslash.
- `\t` represents a tab.
- `\r` represents a carriage return.
- `\n` represents a line feed.

The encoder always escapes backslashes first, so the sequences are unambiguous. The `labels` field is a JSON array of label-name strings before TSV escaping; decode TSV escaping and then parse that field as JSON. This preserves commas and other punctuation inside individual label names. Text and JSON formats do not use the TSV escaping rules.

The stable v1 columns are:

| Command | Columns after `schema_version` |
| --- | --- |
| `status` | `channel`, `data_directory`, `database_path`, `open`, `completed` |
| `search` | `kind`, `id`, `space_id`, `title`, `subtitle`, `snippet` |
| `task list` | `id`, `title`, `status`, `priority`, `planned_on`, `completed` |
| `task show` | `id`, `title`, `space_id`, `project_id`, `status`, `completed`, `priority`, `planned_on`, `deadline_on`, `notes`, `labels`, `revision` |
| `task add`, `task update`, `task complete`, `task reopen` | same as `task show` |
| `task delete` | `deleted`, `id` |
| `space list`, `project list`, `label list` | `id`, `name`, `detail` |
| `sync status` | `provider`, `direction`, `scope`, `configured`, `active`, `credential_available`, `pending_completions`, `pending_reopens`, `pending_date_updates`, `total_pending`, `last_full_sync_at` |
| `sync run` | `provider`, `direction`, `scope`, `planned`, `applied`, `remaining`, `success` |

For example, `task show --format tsv` begins with:

```text
schema_version  id  title  space_id  project_id  status  completed  priority  planned_on  deadline_on  notes  labels  revision
```

The text form exposes the same fields as key/value rows. JSON uses the versioned envelope and corresponding camel-case properties. Label arguments resolve an exact label ID first, then a unique case-insensitive name. An unmatched label value intentionally creates a local label; an ambiguous name must be replaced with an exact ID.

Task JSON renders `priority` as `highest`, `high`, `normal`, or `low`, and also
includes `priorityValue` as `1`, `2`, `3`, or `4` respectively. Workflow status
accepts `inbox`, `next`, `waiting`, or `someday`. Task-list views accept `open`,
`inbox`, `next` (`next-actions` is an alias), `waiting`, `someday`, `today`,
`calendar`, `overdue`, `completed`, or `all`. Dates use `YYYY-MM-DD`.

Task JSON from both `task list` and single-task commands includes
`isRecurring` and `recurrenceRule`. `isRecurring` is `true` exactly when the
canonical domain `recurrence_rule` contains a non-blank provider pattern;
`recurrenceRule` contains that provider pattern or is `null` for a one-time
task. Openza has no separate canonical next-occurrence field: the current
occurrence date remains available through the existing `plannedOn` and
`deadlineOn` fields. This is an additive JSON v2 field extension. TSV v1 stays
unchanged for compatibility; agents that need recurrence metadata should use
JSON.

Task lists contain top-level tasks by default so their totals match `status`.
Use `--include-subtasks` to return nested tasks too. `search --limit` caps the
combined task and project result set, rather than applying separately to each
kind. Label filters accept the exact ID returned by `label list` or a unique
case-insensitive name; matching by the resolved name also keeps legacy/provider
label records logically grouped.

Provider connection and full synchronization remain GUI workflows. The CLI
supports one deliberately narrow Todoist operation: pushing task changes that
Openza has already placed in its pending provider-write queue. It can send
completion, reopen, and planned-date changes; it does not fetch a provider
snapshot, import tasks, apply routing rules, move Todoist tasks, or configure a
provider. CLI provider sync is currently supported only on Linux, where it reads
the existing channel-specific Secret Service credential. Windows provider sync
remains a GUI workflow until the Windows credential integration is implemented.

All three sync selectors are required even though only one contract is
currently supported:

```text
--provider todoist --direction push --scope pending
```

Run `sync status` first. It reads only local integration state, Secret Service
credential availability, last full-sync time, and aggregate pending counts; it
does not reveal task IDs/titles/tokens and makes no provider request. When work
is queued, `sync run` requires `--yes`; that flag authorizes all pending writes
present when that invocation acquires the provider-sync lease. A prior status or
confirmation count is advisory because new local changes may be queued later.
The execution serializes with the Linux Avalonia app's Todoist sync. A zero-work
run succeeds without `--yes` and makes no provider request. Missing confirmation
for a non-empty queue exits `5`; a provider that is not already configured,
active, and credentialed exits `6`. Pull and
bidirectional sync remain GUI-only because the existing full engine can also
apply routing-driven remote project moves. No CLI sync command configures or
activates a provider implicitly.

## Linux installation

The public Linux distribution is the single strict `openza-tasks` Snap. It
contains the desktop app and CLI over the same refresh-stable Production data
directory. Until the Snap Store grants the requested automatic alias, the CLI
command is namespaced as `openza-tasks.openza`; the public release is intended
to expose the normal `openza` alias.

Maintainers can build the ignored local Snap with:

```bash
snapcraft pack --output artifacts/snap/
```

Registration, installation, alias creation, upload, and channel promotion are
separate maintainer actions and are never performed by a source run.

Read-only commands (`status`, `search`, task/reference `list`, and `task show`)
open an existing database in SQLite read-only mode and do not run schema
migrations. They accept the current schema and the immediately preceding
schema when it contains the fields those commands require, so installing a new
CLI does not force a desktop migration merely to keep reading existing data.
`sync status` and `sync run` without `--yes` use the same read-only path; the
latter checks only aggregate pending counts before returning confirmation or a
zero-work result and does not access credentials or construct a provider.
All commands take a shared coordination lease stored outside the
data directory, so a read cannot overlap a coordinated database replacement and a
read-only data directory remains usable. Linux always uses the same hardened
`/tmp/openza-runtime-<uid>` coordination root for an OS user, independent of
`XDG_RUNTIME_DIR`, `TMPDIR`, or sandbox visibility, so the GUI and an agent CLI
cannot select different locks. Mutating commands retain the normal writable
store. Snap launchers use separate private extraction caches below
`$SNAP_USER_COMMON` for the single-file desktop and CLI bundles.
