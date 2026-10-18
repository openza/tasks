# Windows transition: deferred

Windows transition execution is deferred. WinUI remains the current Store host in necessary maintenance; Avalonia is the active desktop reference. Replacing the Store host requires explicit authorization, the evidence below and acceptance of unresolved user policies.

The fresh-start rule excludes automatic import of legacy Flutter-era stores. A separately authorized, guarded WinUI-to-Avalonia package transition is a different operation. It does not authorize discovering or importing other old stores.

## Required acceptance

| Boundary | Evidence required before replacement |
| --- | --- |
| Data and settings | Identify the actual package LocalFolder and opened database under MSIX virtualization. Preserve consistent originals and restore points in package LocalState. Verify settings meaning, account selections, view/filter/grouping preferences and restart behavior. JSON deserialization alone is insufficient. |
| Provider credentials | Prove package/channel ownership and native vault read/upsert/failure behavior without enumerating unrelated secrets, deleting originals first, logging tokens or writing plaintext credentials. |
| Microsoft sign-in | Decide Windows sign-in UX and reconnect policy. Verify account/client matching, DPAPI/MSAL/broker behavior, silent renewal, expiry, interactive fallback and disconnect on an approved test account. Cache recognition is not live sign-in continuity. |
| Personal Cloud | Preserve database, profile/provisioning files, exact device credential, instance/device identity, replica/outbox/sequence state, tombstones and checkpoints as a consistent set. Verify server acceptance and missing/revoked identity recovery without replay under another device. |
| Activation and UI | Verify old/new single-instance ownership, second launch, foregrounding and GUI/CLI coordination. Test title bar/system buttons, resize/snap, dialogs, scaling, high contrast, keyboard and accessibility on supported Windows versions. |
| Packaging and recovery | Verify signed Dev MSIX replacement, exact identity/publisher, executable/capabilities/dependencies, increasing version, interruption and recovery. An older binary must not open a migrated writable database without proven compatibility. Recovery may require an isolated safety copy or forward fix, not a package downgrade. |

Never compare hosts against the same writable live database. Preview and Production are daily apps; their disruption, installation, Store submission and release require separate authorization. WinUI retirement needs these gates, approved real-integration evidence and the user's replacement/rollout decision.

## Preserved Dev experiment and provisional policies

The local deferred/windows-continuity branch preserves the earlier Dev experiment from dddbc5a and 04b2599; the original combined docs/avalonia-architecture-baseline branch at 2795eb7 remains a recovery reference. The docs-only and scheduling branches do not contain this continuity implementation. Paths below describe that deferred branch, not code present in every branch.

- An explicit OpenzaWindowsContinuity=Dev build checks package identity/name/publisher and uses ApplicationData.Current.LocalFolder. Preview/Production continuity is rejected. The old WinUI AppInstance key Openza.Tasks.Main is acquired before file/vault access and retained for the session; the shared runtime lease alone is not single-instance activation proof.
- Before app initialization, WindowsContinuityCoordinator retains immutable originals, a SQLite online-backup copy, fingerprints and a versioned journal. Retry checks originals and destinations; changed/corrupt state stops without overwriting recovery evidence. Orphan staging remains evidence. Settings use settings.avalonia.json, leaving WinUI settings.json unchanged; unsupported settings and completion receipts are retained.
- Scoped PasswordVault upsert uses supported Add replacement without remove-first. Unscoped legacy provider credentials lack channel ownership proof and are not automatically copied. The experiment asks for provider reconnection and initially disables automatic sync/OneDrive upload. These are **Dev-only provisional policies**, pending real package/vault evidence and a user decision, not the accepted production experience.
- Microsoft cache handling uses package-local DPAPI CurrentUser and supported MSAL callbacks/account APIs for the configured client/account. It retains originals and reports unusable/missing caches. No startup token request is made. This does not prove silent sign-in, live renewal or broker continuity.
- Personal Cloud transfer checks exact origin/instance/device, matching replica metadata and pending-device intent before reading the origin-specific legacy key and verifying destination storage. Completed preparation prevents legacy re-import after disconnect. Missing identity can stop preparation or pause sync with local tasks/Settings and pending intent retained; explicit repair still respects the pending-intent guard. These recovery/sync-off choices are provisional Dev behavior, not proof of accepted pairing continuity.
- Existing synthetic/injected tests and earlier Ubuntu C# compilation cover source-level behavior only. The complete Windows build invokes Windows SDK tooling such as mt.exe. **C# compilation is not a successful native package build, installed-vault test, MSIX upgrade or user acceptance.** No new Windows build, launch, install or native validation is part of this documentation/split work.

The deferred branch contains the continuity Application contracts, Desktop Services/Continuity adapters/coordinator, WinUiSettingsConverter and WindowsContinuity tests. No new implementation is added by this separation.

## Identity and version constraints

| Lane | Required identity | Agreed version lane | Baseline WinUI manifest |
| --- | --- | --- | --- |
| Dev | Openza.OpenzaTasks.Dev | 0.0.N.0 | 0.1.0.0 |
| Preview | Openza.OpenzaTasks.Preview | 0.N.B.0 | 0.2.3.0 |
| Production | Openza.OpenzaTasks | 1.N.P.0 | 1.1.0.0 |

These are checked-in manifest values, not installed/latest Store versions. The Dev manifest differs from its agreed lane; resolve increasing update eligibility during authorized packaging work without silently lowering a version. Preserve Store name/publisher association and supported architecture/OS constraints; do not publish private Store metadata.

## Future Dev-first sequence

1. Authorize an isolated Windows fixture and the provisional reconnect/sync-off/sign-in policies to test. Retain consistent originals; use synthetic data first and fresh human-controlled authentication where needed.
2. Build/sign/install a Dev WinUI fixture, then perform a real Dev Avalonia package update. Record source SHA, package identities/versions, actual file/vault paths and before/after state.
3. Verify local task behavior, settings, activation, interruption and recovery. Separately authorize real provider/OneDrive/Personal Cloud tests using disposable accounts/deployments.
4. Review evidence and unresolved cases with the user before any Preview trial or Store retirement. A successful source build or earlier Web round trip does not close these gates.

Earlier [Windows continuation evidence](windows-continuation.md) remains scoped to its named source/interaction snapshots; it does not establish this in-place upgrade or real-provider acceptance. Reminders and notification delivery are separate product work.

## Platform references

- Microsoft: [packaged AppData behavior](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes), [PasswordVault](https://learn.microsoft.com/uwp/api/windows.security.credentials.passwordvault), [MSAL cache serialization](https://learn.microsoft.com/en-us/entra/msal/dotnet/how-to/token-cache-serialization), [single-instance activation](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance), [package/version requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements).
- Avalonia: [Windows guidance](https://docs.avaloniaui.net/docs/platform-specific-guides/windows/). Apply references to a concrete package during future authorized work; documentation is not installed-app evidence.
