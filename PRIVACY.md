# Privacy Policy

Openza Tasks does not collect telemetry, analytics, or personal usage data. Openza Tasks is maintained by Deependra Solanky; contact: `deependra@solanky.dev`.

Task data, provider snapshots, account identifiers, and preferences are stored locally on your device. The Windows Store app uses its package data folder, with restore points in `LocalState`. The Linux Snap stores its database, settings, and restore points under `$SNAP_USER_COMMON/tasks`; Snap revisions share this directory. Source Dev runs use a separate data directory. Local restore points support rollback, but durable backup means optional OneDrive app-folder backup or an explicit user export to a location you retain independently of the app.

If you connect Todoist or Microsoft To Do, Openza Tasks reads task information and sends supported changes to that provider as part of synchronization. Automatic sync can run at startup and periodically when enabled. Windows stores provider credentials in Credential Locker and protects its MSAL token cache for the current Windows user. The Linux desktop stores provider tokens, its feature-specific MSAL caches, and backup passphrases through Secret Service; the Snap accesses the desktop Secret Portal. Microsoft To Do and OneDrive can use separate accounts. Account names and identifiers used to display and select those accounts are stored in local settings; tokens and passphrases are not stored in the settings JSON.

GitHub integration is optional. Sign-in or token entry allows Openza Tasks to read account, repository, label, and issue information for the connected account. Creating an issue sends the title, body, and label you select to the chosen repository, where its visibility follows that repository's access settings. The local task can retain the issue URL and metadata. Removing the local link does not delete the issue on GitHub.

OneDrive backup is optional and disabled by default. When enabled, Openza uploads immutable database backup snapshots to the app folder in the selected OneDrive account. If passphrase encryption is enabled, snapshots are encrypted before upload and require the original passphrase to restore. Changing encryption settings affects future uploads; it does not rewrite previously uploaded backups.

Openza Tasks does not operate a hosted sync service or sell your data. Optional integrations communicate directly with their respective providers, whose privacy policies and account permissions also apply.
