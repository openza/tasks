# Backup And Release Channels

Openza Tasks is local-first. The working database lives on the user's device, and Openza does not operate a server-side backup service. The app therefore separates fast local rollback from durable backup.

## Data Protection Model

- **Working database**: stored in the package `LocalState` folder for the installed app identity.
- **Restore points**: package-local SQLite copies stored under `LocalState\restore-points`. They support rollback before startup, import, restore, and schema migration events. They should not be described as durable backups because package data can be removed by uninstall, reset, or package identity changes.
- **OneDrive backups**: durable app-folder backups. This is the recommended protection path for reinstall, reset, device loss, and new PC restore.
- **User exports**: durable user-owned backup files created through explicit export. The user chooses the destination, such as Documents, OneDrive, Dropbox, or an external drive.

Do not fight MSIX AppData virtualization to place automatic files in `%LOCALAPPDATA%\Openza`. Packaged app data belongs in the package app data store; durable backups belong in OneDrive or user-selected files.

## Cloud Backup Lanes

OneDrive backups are isolated by app channel:

- Production: `v1/production`
- Preview: `v1/preview`
- Dev: `v1/dev`

Channels do not share cloud backups automatically. This prevents development and dogfood builds from modifying production backup history.

Preview is a maintainer dogfood channel, not a user-specific product path. Production-to-Preview seeding can be handled by local tooling or manual restore when needed; do not add user-specific routing or restore flows to the product.

## Package Channels And Version Lanes

| Channel | Package identity | Display name | Version lane |
| --- | --- | --- | --- |
| Dev | `Openza.OpenzaTasks.Dev` | Openza Tasks Dev | `0.0.N.0` |
| Preview | `Openza.OpenzaTasks.Preview` | Openza Tasks Preview | `0.N.B.0` |
| Production | `Openza.OpenzaTasks` | Openza Tasks | `1.N.P.0` |

Current Preview version: `0.2.3.0`.

Preview versions intentionally use the second version component so they never collide with the production `1.x.x.x` lane.
