# Openza Tasks follow-up work

[Behavior contracts](behavior-contracts.md) state intended meanings and separate current behavior/open decisions. [Architecture](architecture.md) defines ownership. Avalonia uses its built-in Fluent baseline; WinUI remains the Store host in necessary maintenance.

## Local review slices

| Branch | Base | Scope |
| --- | --- | --- |
| docs/task-contract-baseline | Original main 5637e3a + docs-only commit 5562e7a; append-only policy corrections | Direction, contracts and instructions only; feature implementations are not implied. |
| fix/task-scheduling-policies | Shared ancestor 5c6bae9; equivalent policy corrections appended | Scheduling fixes and SQL-free list/editor policy extraction; independent of continuity code. |
| feat/windows-continuity | Shared ancestor 5c6bae9; equivalent policy corrections appended | Active Windows-compatible continuity development on Ubuntu, behind the Dev gate. |

The feature branches remain independent siblings at 5c6bae9. Shared instruction corrections are replayed append-only, without merge/rebase; their equivalent patches do not make the latest docs head a new shared ancestor. Original docs/avalonia-architecture-baseline at 2795eb7 preserves the combined four-commit history and recovery reference. Local branch preparation authorizes no PR, push, merge, installation or rollout.

## Bounded follow-ups

- Preserve the decided independent project/status contract. Inbox is default, None is explicit no-workflow-status, the Inbox view omits project-assigned tasks, and project-assigned Inbox-status tasks intentionally keep their correct Inbox badge.
- Continue cohesive storage/presentation separation only where it improves ownership. Preserve SQL write/outbox/checkpoint boundaries, provider authority and editor save/refresh/selection behavior; do not split files merely for size.
- Before wider planning features, settle scheduled-session shape, recurrence grammar/occurrence identity and completion/skip/rollover. Reminder definitions and delivery attempts need separate design.
- In the owning Personal Cloud repository, verify compatibility, migration/preflight, artifact provenance, recovery and desktop/Web round trips. Keep infrastructure setup separate from task semantics.
- Validate independent-user onboarding and sustained use; source tests alone do not establish real-provider acceptance.

## Windows continuity: active on Ubuntu

Continue Windows-compatible continuity design, C# compilation and synthetic/integration tests on Ubuntu. Keep OS-specific behavior behind platform services and retain the Dev gate. Native build/validation, packaging/distribution, MSIX upgrade testing and Store replacement are not current priorities. The [continuity plan](windows-transition.md) keeps later package/vault/sign-in/pairing evidence separate from current source checks. Upgraded-user reconnect and initial sync-off policy remain user decisions; the Inbox contract is already decided. Do not disrupt Preview or Production.

Deferred ideas need a concrete case: macOS, custom statuses, time tracking, external capture/automation, additional adapters, task tabs and broader search/project-tree changes. A CLI already exists. Flutter/Rust-era notes are historical.
