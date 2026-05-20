# GitHub Integration

Openza Tasks treats GitHub as an outbound publishing target. Todoist and Microsoft To Do bring tasks into Openza; GitHub receives a selected Openza task as a new issue.

## V1 behavior

- A user explicitly creates a GitHub issue from a task.
- Openza stores the created issue link on the task.
- The Openza task stays open after the issue is created.
- If a task already has a GitHub issue link, Openza shows the existing issue as the primary action to avoid accidental duplicates.

V1 does not import GitHub issues, sync GitHub issue state, close/reopen GitHub issues, or sync comments.

## Access

GitHub OAuth sign-in uses the Openza Tasks OAuth app client ID embedded in the app build. GitHub tokens are saved in Windows Credential Locker through the app credential store.

For private repositories, the token or OAuth grant needs private repository access. For public repositories only, public issue access is enough.

## Data model

GitHub issue links are stored as task external links, not as source tasks and not as provider sync records. This keeps outbound publishing separate from inbound task sync.
