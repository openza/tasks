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
```

Use `--format text|json|tsv` on any leaf command. JSON responses have a stable `schemaVersion` and `data` envelope. Results go to stdout and diagnostics go to stderr.

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

For example, `task show --format tsv` begins with:

```text
schema_version  id  title  space_id  project_id  status  completed  priority  planned_on  deadline_on  notes  labels  revision
```

The text form exposes the same fields as key/value rows. JSON uses the versioned envelope and corresponding camel-case properties. Label arguments resolve an exact label ID first, then a unique case-insensitive name. An unmatched label value intentionally creates a local label; an ambiguous name must be replaced with an exact ID.

Provider connection and synchronization remain GUI workflows in CLI v1.
