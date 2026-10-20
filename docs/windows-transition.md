# Windows continuity

Continuity development uses Windows-compatible C# and synthetic integration tests
on Ubuntu. OS-specific behavior stays behind platform services. WinUI remains the
Windows Store host in maintenance; native packaging, MSIX upgrade validation and
Store replacement are later release work.

The transition covers the matching WinUI-to-Avalonia package only. Automatic import
of Flutter-era or other legacy stores is excluded.

## Package acceptance requirements

| Boundary | Evidence required before replacement |
| --- | --- |
| Data and settings | Identify the actual package LocalFolder and opened database under MSIX virtualization. Preserve consistent originals and restore points in package LocalState. Verify settings meaning, account selections, view/filter/grouping preferences and restart behavior. JSON deserialization alone is insufficient. |
| Provider credentials | Prove package/channel ownership and native vault read/upsert/failure behavior without enumerating unrelated secrets, deleting originals first, logging tokens or writing plaintext credentials. |
| Microsoft sign-in | Decide Windows sign-in UX and reconnect policy. Verify account/client matching, DPAPI/MSAL/broker behavior, silent renewal, expiry, interactive fallback and disconnect on an approved test account. Cache recognition is not live sign-in continuity. |
| Personal Cloud | Preserve database, profile/provisioning files, exact device credential, instance/device identity, replica/outbox/sequence state, tombstones and checkpoints as a consistent set. Verify server acceptance and missing/revoked identity recovery without replay under another device. |
| Activation and UI | Verify old/new single-instance ownership, second launch, foregrounding and GUI/CLI coordination. Test title bar/system buttons, resize/snap, dialogs, scaling, high contrast, keyboard and accessibility on supported Windows versions. |
| Packaging and recovery | Verify signed Dev MSIX replacement, exact identity/publisher, executable/capabilities/dependencies, increasing version, interruption and recovery. An older binary must not open a migrated writable database without proven compatibility. Recovery may require an isolated safety copy or forward fix, not a package downgrade. |

Keep test fixtures separate from live installations, and never point both hosts at
the same writable database. Preview/Production installation, replacement and Store
release require maintainer release approval. WinUI retirement also depends on
resolving the reconnect and initial-sync policies below.

## Dev implementation and open policies

- `OpenzaWindowsContinuity=Dev` checks package name/publisher and uses
  `ApplicationData.Current.LocalFolder`. Preview/Production continuity is rejected.
  The existing WinUI `AppInstance` key `Openza.Tasks.Main` is acquired before
  file/vault access and retained for the session. A runtime lease alone does not
  establish single-instance activation.
- `WindowsContinuityCoordinator` retains immutable originals, a SQLite online
  backup, fingerprints and a versioned journal. Retry validates source/destination
  state; changed or corrupt files stop recovery without overwriting diagnostics.
  Orphan staging is preserved. Converted settings use `settings.avalonia.json`,
  leaving WinUI `settings.json` unchanged, with unsupported values and receipts kept.
- Scoped `PasswordVault` upsert replaces through supported `Add`, without removing
  the original first. Unscoped legacy provider credentials lack channel ownership
  proof and are not automatically copied. Provider reconnection and initially
  disabled automatic sync/OneDrive upload are provisional **Dev-only** policies;
  they need native package/vault validation and a production product decision.
- Microsoft cache handling uses package-local DPAPI CurrentUser and supported MSAL
  callbacks/account APIs for the configured client/account. It retains originals,
  reports unusable/missing caches and makes no startup token request. Native silent
  sign-in, renewal and broker behavior need separate validation.
- Personal Cloud transfer checks exact origin/instance/device, replica metadata
  and pending-device intent before reading the origin-specific legacy key and
  verifying destination storage. Completion prevents legacy re-import after
  disconnect. Identity recovery retains local tasks, Settings and pending intents;
  explicit repair respects the pending-intent guard. These recovery/sync-off choices
  remain provisional until pairing continuity is validated.
- After a completed transition and valid profile/replica checks, transient vault
  COM/access failures allow ordinary local startup. A session barrier blocks
  automatic/manual cloud sync, provider delivery, account operations and credential
  writes, including direct service calls. Local authoring keeps the saved profile
  and pending intents. Restoring credential access requires a fresh startup;
  settings toggles or repeated preparation cannot lift the barrier. Failed
  validation changes no saved preference, recovery flag, identity or pending intent.
  Initial-transition failures, file/metadata failures and cancellation remain
  separate from this fallback; missing/corrupt identity follows its recovery path.
  Native vault construction is lazy so activation errors reach that boundary.

The Dev build gate remains in place. Managed C# compilation and fake-platform tests
are useful development checks; they do not validate native Windows SDK packaging,
real vault/MSAL behavior, installed upgrades or live-provider integration.

## Service composition and regression coverage

The view model resolves `_credentials` from
`WindowsContinuitySession.AppCredentials` before default Microsoft authentication
and sync resolution. It forwards resolved `_microsoftAuth` and guarded
`_personalCloudSetup`, including progress subscription. `EnsureCloudAccess` and
recovery checks run before save/provider resolution in manual and automatic sync.
Cloud error codes remain available so unauthorized identity recovery stays distinct
from transient credential inhibition.

[Dependency-composition tests](../src/Openza.Tasks.Tests/GuardedDependencyCompositionTests.cs)
and [session tests](../src/Openza.Tasks.Tests/WindowsContinuityCompositionTests.cs)
cover these requirements with disposable data and fake platform/vault/providers:

- Completed startup with transient credential failure opens the local window;
  inhibited manual/automatic sync never enters authentication or provider work.
- Guarded vault, provider, cloud/bridge and progress operations remain blocked.
  Local edits preserve pending intent, checkpoints, pairing and preferences.
- A healthy next session revalidates access without persisting a sync-off choice.
  Unauthorized cloud identity continues to use its distinct recovery policy.

## Settings conversion

WinUI [TasksPage](../src/Openza.Tasks/Pages/TasksPage.xaml.cs) exposes only `All`
for DateScopeFilter; [AppShell](../src/Openza.Tasks/Shell/AppShell.xaml.cs) resets
and persists that getter, and the [Core enum](../src/Openza.Tasks.Core/Data/TaskQuery.cs)
contains only `All`. However, generic
[settings persistence](../src/Openza.Tasks/Services/AppSettingsService.cs) can
round-trip an existing unknown string. Retain the original and reject unsupported
DateScope values rather than silently reinterpreting them; current UI behavior
does not establish every historical or manually edited settings value.

Validate converted sort/group settings through the Avalonia view model's actual
ordering and group headers for each supported choice. Converter indices alone
are insufficient. Keep synthetic unsupported-DateScope cases alongside these
checks; native UI/package acceptance remains separate.

## Identity and version constraints

| Lane | Required identity | Agreed version lane | Baseline WinUI manifest |
| --- | --- | --- | --- |
| Dev | Openza.OpenzaTasks.Dev | 0.0.N.0 | 0.1.0.0 |
| Preview | Openza.OpenzaTasks.Preview | 0.N.B.0 | 0.2.3.0 |
| Production | Openza.OpenzaTasks | 1.N.P.0 | 1.1.0.0 |

These are source manifest values, not installed or latest Store versions. The Dev
manifest differs from its intended lane; resolve increasing update eligibility
before packaging without silently lowering a version. Preserve publisher/name
association, architecture and OS constraints; keep private Store metadata out of Git.

## Native package validation

1. Select an isolated Windows fixture and the reconnect/sync-off/sign-in policies.
   Preserve consistent originals; start with synthetic data and test accounts.
2. Build/sign/install a Dev WinUI fixture and update it with a real Dev Avalonia
   package. Record source, package identities/versions and before/after state.
3. Validate local task behavior, settings, activation, interrupted recovery,
   credentials and pairing. Use disposable provider accounts and cloud deployments.
4. Resolve failures before a Preview trial or Store replacement. Source builds and
   Web round trips are not substitutes for package acceptance.

See [Windows platform notes](windows-continuation.md) for additional implementation
context. Reminders and notification delivery are separate product work.

## Platform references

- Microsoft: [packaged AppData behavior](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes), [PasswordVault](https://learn.microsoft.com/uwp/api/windows.security.credentials.passwordvault), [MSAL cache serialization](https://learn.microsoft.com/en-us/entra/msal/dotnet/how-to/token-cache-serialization), [single-instance activation](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance), [package/version requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements).
- Avalonia: [Windows guidance](https://docs.avaloniaui.net/docs/platform-specific-guides/windows/). Use these references when validating an actual package; documentation alone does not establish installed behavior.
