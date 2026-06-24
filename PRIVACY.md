# Privacy Policy

Openza Tasks does not collect telemetry, analytics, or personal usage data. Openza Tasks is maintained by Deependra Solanky; contact: `deependra@solanky.dev`.

Task data is stored locally on your Windows device in the app data folder. Local restore points are stored in the app's package `LocalState` folder and support rollback before startup, import, restore, and schema migration events. Because package data can be removed by uninstall, reset, or package identity changes, durable backup means optional OneDrive app-folder backup or an explicit user export.

If you connect Todoist or Microsoft To Do, Openza Tasks uses provider access only for the feature you enable. Todoist tokens are stored locally using Windows Credential Locker. Microsoft sign-in uses MSAL; the MSAL token cache is stored locally and encrypted for the current Windows user.

OneDrive backup is optional and disabled by default. When enabled, Openza uploads immutable backup snapshots to the app folder in the selected OneDrive account. If passphrase encryption is enabled, backup files are encrypted before upload and require the passphrase to restore on another PC.

Openza Tasks does not operate a hosted sync service in V1 and does not sell or share data.
