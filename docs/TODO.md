# Openza Tasks follow-up work

[Behavior contracts](behavior-contracts.md) state intended meanings and separate current behavior/open decisions. [Architecture](architecture.md) defines ownership. Avalonia uses its built-in Fluent baseline; WinUI remains the Store host in necessary maintenance.

## Local review slices

| Branch | Base | Scope |
| --- | --- | --- |
| docs/task-contract-baseline | Original main 5637e3a + docs-only commit 5562e7a | Direction, contracts and instructions only; feature implementations are not implied. |
| fix/task-scheduling-policies | docs/task-contract-baseline | Scheduling fixes and SQL-free list/editor policy extraction; independent of continuity code. |
| deferred/windows-continuity | docs/task-contract-baseline | Preserved Dev continuity experiment only; Windows execution and production policy acceptance remain deferred. |

The latter two branches are siblings. Original docs/avalonia-architecture-baseline at 2795eb7 preserves the combined four-commit history and recovery reference. Local branch preparation authorizes no PR, push, merge, installation or rollout.

This scheduling branch contains the [current scheduling contract](scheduling-and-recurrence.md), exact-time preservation/display fixes, DST-safe SQL count bounds and the first list/editor policy extraction. It contains no Windows continuity implementation. Broader persistence, sync/setup and navigation separation remain future bounded work.

## Bounded follow-ups

- Resolve the Inbox view/status-badge discrepancy explicitly before changing its policy. Current view membership is open + inbox + no Openza project; badges follow workflow status.
- Continue cohesive storage/presentation separation only where it improves ownership. Preserve SQL write/outbox/checkpoint boundaries, provider authority and editor save/refresh/selection behavior; do not split files merely for size.
- Before wider planning features, settle scheduled-session shape, recurrence grammar/occurrence identity and completion/skip/rollover. Reminder definitions and delivery attempts need separate design.
- In the owning Personal Cloud repository, verify compatibility, migration/preflight, artifact provenance, recovery and desktop/Web round trips. Keep infrastructure setup separate from task semantics.
- Validate independent-user onboarding and sustained use; source tests alone do not establish real-provider acceptance.

## Windows: deferred

All Windows continuity/platform/packaging/native execution is deferred. The [transition plan](windows-transition.md) records required package/vault/sign-in/pairing/recovery evidence and provisional Dev policies. An earlier C# compile or Avalonia/Web interaction does not establish a native build or in-place package upgrade. Keep the current Store host until the user accepts replacement gates; do not disrupt Preview or Production.

Deferred ideas need a concrete case: macOS, custom statuses, time tracking, external capture/automation, additional adapters, task tabs and broader search/project-tree changes. A CLI already exists. Flutter/Rust-era notes are historical.
