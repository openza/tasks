# Avalonia parity and integration validation

WinUI remains the behavioral reference. Shared Core behavior, Linux builds and automated tests do not establish Windows/Linux runtime equivalence by themselves.

## Desktop integration configuration

The Avalonia host uses the same public Microsoft/GitHub application identities as the reference host, with a separate Dev Microsoft client ID. Public client IDs are configuration, not credentials. Build-time overrides are `MicrosoftGraphClientId`, `MicrosoftGraphTenantId` and `GitHubClientId`. Runtime overrides are `OPENZA_TASKS_MS_GRAPH_CLIENT_ID`, `OPENZA_TASKS_MS_GRAPH_TENANT_ID` and `OPENZA_TASKS_GITHUB_CLIENT_ID`. The legacy Microsoft To Do environment names remain recognized.

Microsoft sign-in uses MSAL device authorization on Linux. The Microsoft application registration must allow the public-client device flow and the selected account type. Validate the registration with a test account before release; compiling this flow cannot verify the server-side configuration. The required scopes match the reference host: task/mailbox/user scopes for Microsoft To Do and app-folder/user scopes for OneDrive. MSAL silently renews tokens from a credential-store cache; an expired or revoked authorization requires signing in again. Microsoft To Do and OneDrive maintain separate feature caches/account choices, within the runtime channel's credential namespace.

GitHub supports device sign-in and token entry. Issue creation presents repository, title, body and label selection before publishing. Removing a local link must never delete the remote issue. A failed local link save after a successful remote issue creation must identify the created URL so the user can recover without creating a duplicate.

Both hosts use one Markdown-export query: all tasks, including completed tasks and subtasks, restricted to the selected Space when one is selected. This retains the Avalonia export population and makes the WinUI export explicit as well.

OneDrive backup remains opt-in. Encryption stores its passphrase in the credential store, not preferences. Changing or disabling encryption affects future uploads; old encrypted backups still require the passphrase used when they were created. Restoring either local or cloud backups creates a local safety restore point first. Cloud backup storage is separated by channel.

MSAL implementation references: [token cache callbacks](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identity.client.itokencache) and [device authorization](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identity.client.publicclientapplication.acquiretokenwithdevicecode).

## Acceptance before declaring complete parity

- Use separate, equivalent isolated databases in WinUI and Avalonia. Include active/completed/archived projects, multiple Spaces, tasks without a project, labels, dates, subtasks and provider tasks.
- Validate capture/edit/save/cancel, assignment preservation, navigation from focused editors, project commands, filters/sort/group persistence, per-list selected-task restoration and restart restoration.
- Exercise Microsoft and GitHub sign-in, cancellation, account changes, renewal, revoked access and offline recovery using test accounts. Verify both providers' sync results when one fails, and responsiveness during network delays.
- Validate OneDrive upload/list/restore, opt-out, encryption changes and wrong-passphrase handling against a disposable database. Verify historical local backup export preserves the selected snapshot.
- Check keyboard/focus, screen-reader output, narrow windows and large display/text scaling on native Linux. Run corresponding WinUI checks on Windows.
- Preserve Dev/Preview/Production identities. Source launches use Dev. Do not substitute live databases or install a release merely to run acceptance.

Use the repository's direct Avalonia build and shared test project. Headless desktop tests should run with `OPENZA_TASKS_DEV_DATA_DIR` pointing to a disposable directory so they cannot read or write the user's Dev settings or restore points.
