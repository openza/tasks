# Openza Tasks follow-up work

[Behavior contracts](behavior-contracts.md) state intended meanings and separate current behavior/open decisions. [Architecture](architecture.md) defines ownership. Avalonia uses its built-in Fluent baseline; WinUI remains the Store host in necessary maintenance.

## Local source branches and integration

| Branch | Source head/base | Scope |
| --- | --- | --- |
| docs/task-contract-baseline | 725849b | Direction, contracts and instructions; no continuity implementation in this source slice. |
| fix/task-scheduling-policies | 8124c6d | Scheduling/exact-date/clock fixes, list/editor extraction and equivalent label no-op. |
| refactor/desktop-responsibility-boundaries | 8c1b96e, descended from scheduling | Connection-scoped reads/search, connected-sync orchestration, navigation state and resolved-credential composition. |
| feat/windows-continuity | 86dc3cd | Windows-compatible continuity on Ubuntu, behind the Dev gate. |
| integration/architecture-continuity | 83d6089; starts at 8c1b96e, merges 725849b and 86dc3cd | Combines all four source slices through ancestry-preserving local merges; scheduling is already an ancestor. |
| fix/cloud-upgrade-compatibility | Runtime e7378d7, atop 83d6089; documentation follow-up | Fail-closed sync readiness, capability retention, ordered migration provenance, target-resource preflight and upgrade lifecycle guards; [source/schema/evidence matrix](cloud-upgrade-compatibility.md). |

The original source refs and main at 5637e3a remain unchanged. Earlier sibling-replayed instruction corrections are reconciled against the accepted current contracts in the integration branch. Original docs/avalonia-architecture-baseline at 2795eb7 remains a recovery reference. Local integration is separate from publishing, merging main, package installation or rollout.

The combined source retains the [scheduling contract](scheduling-and-recurrence.md), exact-time/clock fixes, read/search/sync/navigation ownership and SQL transaction/editor sequencing. It also contains the Dev-gated Windows session implementation and fake-platform composition regressions, including local operation during transient vault failure. Label rename remains parked for a future dedicated label list; equivalent task-editor label changes remain a no-op. Native Windows packaging, live vault/sign-in/provider acceptance and the provisional policy decisions remain separate later work.

The compatibility branch and matching Personal Cloud runtime `853551f` have passed
the recorded local full suites, artifact-loader checks, builds and independent
review. The [matrix](cloud-upgrade-compatibility.md) separates source allowlists,
the unchanged preview.9/schema-20 pin, feature tests and the local schema-25
artifact. The companion repository documents Web module/state/API ownership and
extension rules. [Infrastructure ownership](architecture.md#personal-cloud-infrastructure-boundary)
is confirmed without another runtime refactor. Desktop cleanup is complete for
this round; additional splitting needs a concrete boundary problem. Engineering
readiness on these local branches does not establish live-cloud, real-provider or
native package acceptance, and does not authorize publishing or rollout.

## Bounded follow-ups

- Preserve the decided independent project/status contract. Inbox is default, None is explicit no-workflow-status, the Inbox view omits project-assigned tasks, and project-assigned Inbox-status tasks intentionally keep their correct Inbox badge.
- Further persistence, setup or presentation separation needs a concrete responsibility benefit beyond this read/sync/navigation slice. Preserve SQL write/outbox/checkpoint boundaries, provider authority and editor save/refresh/selection behavior; do not split files merely for size or treat the remaining direct host/store call sites as a mandate for a generic framework.
- Before wider planning features, settle scheduled-session shape, recurrence grammar/occurrence identity and completion/skip/rollover. Reminder definitions and delivery attempts need separate design.
- Preserve the verified compatibility/migration/preflight and artifact-provenance contracts in both repositories. Live-cloud upgrades and persisted Desktop/Web round trips require their own authorized evidence; the recorded local checks do not imply them. Keep infrastructure setup separate from task semantics.
- Validate independent-user onboarding and sustained use; source tests alone do not establish real-provider acceptance.

## Windows continuity: active on Ubuntu

Continue Windows-compatible continuity design, C# compilation and synthetic/integration tests on Ubuntu. Keep OS-specific behavior behind platform services and retain the Dev gate. Native build/validation, packaging/distribution, MSIX upgrade testing and Store replacement are not current priorities. The [continuity plan](windows-transition.md) keeps later package/vault/sign-in/pairing evidence separate from current source checks. Upgraded-user reconnect and initial sync-off policy remain user decisions; the Inbox contract is already decided. Do not disrupt Preview or Production.

Deferred ideas need a concrete case: macOS, custom statuses, time tracking, external capture/automation, additional adapters, task tabs and broader search/project-tree changes. A CLI already exists. Flutter/Rust-era notes are historical.
