# Todoist Integration

Openza Tasks treats Todoist as a connected source, not as a workflow template. Todoist labels can be used for configurable routing, but the app must not hardcode labels such as `work` or `personal`.

## Source Links

Todoist source links use the modern app URL shape:

```text
https://app.todoist.com/app/task/{task-slug}-{task-id}
```

The slug is derived from the task title for readability. The task ID remains the durable identifier.

## Source Labels

Todoist labels are preserved as source metadata under `sourceTask.labels` in the provider snapshot JSON. They are not added to primary Openza task labels by default, because source labels can be noisy and may not match the user's Openza labeling system.

## Label Routing

Label routing is configured from **Settings > Integrations > Todoist > Rules**. The UI writes enabled `sync_routes` settings behind the scenes so the user's Todoist workflow can change without app code changes.

Example:

```json
{
  "labelRoutes": [
    {
      "labels": ["work"],
      "spaceId": "space_work"
    },
    {
      "labels": ["personal", "home"],
      "match": "any",
      "spaceId": "space_personal"
    }
  ]
}
```

Rules are evaluated in route order. A rule with `match: "all"` requires every listed label. The default is `any`.

## Post-Import Filing

Post-import filing is optional and configured from the same Todoist rule editor. It can move a Todoist task after the user imports the source item into Openza, but sync must not depend on that move. If the user later changes Todoist workflow or disables filing, Openza should still sync the task by provider ID.

Example:

```json
{
  "labelRoutes": [
    {
      "labels": ["openza"],
      "spaceId": "space_default",
      "postImport": {
        "moveToProjectId": "todoist_processed_project_id"
      }
    }
  ]
}
```

## REST API vs Sync API

The current provider keeps REST API v1 as the sync implementation because it is smaller, easier to reason about, and already covers the active task list, projects, labels, completion push, completion-history repair, and task move operations.

The Todoist Sync API remains a candidate for provider V2. It is stronger for incremental sync, richer item state, command batching, and workflows that need full Todoist structure such as sections and all completed history behavior. It is also a larger provider rewrite with token-state handling and command response reconciliation, so it should be handled as a separate migration rather than mixed into source metadata and routing work.
