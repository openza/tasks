# Openza Tasks follow-up work

[Behavior contracts](behavior-contracts.md) define task semantics and open product
decisions. [Architecture](architecture.md) defines implementation boundaries.
Avalonia remains cross-platform, with Linux Snap as the sole current focus until
Linux is stable. All Windows work and continuity policy decisions are parked
unless the maintainer explicitly asks. WinUI is frozen: its last
Microsoft Store release is final, with no further releases or maintenance. Keep
the project and leave existing Store users on that release until a future Avalonia
replacement passes acceptance.

## Current foundations

- Local task operations, connection-scoped reads/search, list projection and
  connected-sync orchestration have distinct responsibilities. Preserve SQL
  transaction boundaries, provider authority and editor/selection behavior.
- [Scheduling](scheduling-and-recurrence.md) distinguishes dates, exact instants,
  deadlines, schedule metadata and completion. Project and workflow status remain
  independent; project-assigned Inbox tasks intentionally retain their badge.
- Equivalent task-editor label changes are a no-op. Shared-label renaming is
  deferred to a future dedicated label list.
- [Cloud upgrades](cloud-upgrade-compatibility.md) validate readiness, capabilities,
  migration provenance and target identity. [Quota controls](personal-cloud-quota.md)
  preserve pending edits and limit daily dispatches across restart.
- Windows continuity is implemented behind its Dev gate. Native package, vault,
  sign-in and replacement acceptance remain separate; see the
  [transition plan](windows-transition.md).
- CI requires Linux build/tests and Snap validation. The optional Windows job is a
  nonblocking early warning only; do not act on its results unless explicitly asked.

## Release and validation work

- Follow the [Snap release plan](snap-release.md) for the proposed 1.2.0 release,
  artifact pinning, isolated upgrades, credentials/pairing and channel promotion.
  Public Store 1.1.0 and the development Cloud candidate are different releases.
- Preserve compatible Desktop/Cloud behavior across upgrades. Validate persisted
  round trips, recovery and packaged behavior on the target platform; unit tests
  and source compilation alone are insufficient.
- Complete independent-user onboarding and sustained-use feedback, including
  understandable source ownership, local capture and recovery paths.
- Windows reconnect and initial automatic-sync decisions, native testing, Avalonia
  packaging, MSIX and Store replacement are parked until Linux is stable and the
  maintainer explicitly resumes them. Retain the existing Dev gate and future
  transition reference; keep live installations separate from test fixtures.

## Product decisions before expansion

- Define scheduled-session shape, timezone/overlap rules, recurrence grammar,
  occurrence identity and missed/skip/completion handling before adding workflows.
- Design reminders, delivery attempts and server visibility separately.
- Further persistence or presentation extraction needs a concrete responsibility
  benefit; avoid generic frameworks or splitting solely by file size.
- Keep infrastructure setup separate from task semantics and preserve immutable
  pending envelopes during retry, restore and recovery.
- Future: safe cloud switching, fresh starts and retired-instance queue cleanup; no in-app flow yet.
  `EnsureNoPendingPersonalCloudIntentsAsync` counts all instances, so retired pending rows can block disconnect/reconnect.

Other future ideas include macOS, custom statuses, time tracking, external capture,
automation, additional adapters, task tabs and broader search/project-tree changes.
The CLI already exists. Flutter/Rust-era material remains historical reference.
