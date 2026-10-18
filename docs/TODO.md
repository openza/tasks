# Openza Tasks follow-up work

[Behavior contracts](behavior-contracts.md) state intended meanings and separate current behavior/open decisions. [Architecture](architecture.md) defines ownership. Avalonia uses its built-in Fluent baseline; WinUI remains the Store host in necessary maintenance.

## Local review slices

| Branch | Base | Scope |
| --- | --- | --- |
| docs/task-contract-baseline | Original main 5637e3a + docs-only commit 5562e7a; append-only policy corrections | Direction, contracts and instructions only; feature implementations are not implied. |
| fix/task-scheduling-policies | Shared ancestor 5c6bae9; equivalent policy corrections appended | Scheduling fixes and SQL-free list/editor policy extraction; independent of continuity code. |
| refactor/desktop-responsibility-boundaries | Scheduling baseline 8124c6d | Connection-scoped reads/search, connected-sync application orchestration and pure navigation context/selection state; behavior preserved. |
| feat/windows-continuity | Shared ancestor 5c6bae9; equivalent policy corrections appended | Active Windows-compatible continuity development on Ubuntu, behind the Dev gate. |

The original docs, scheduling and Windows slices remain independent siblings at 5c6bae9; the new desktop refactor descends from scheduling baseline 8124c6d. Shared instruction corrections were replayed append-only, without merge/rebase; their equivalent patches do not make the latest docs head a new shared ancestor. Original docs/avalonia-architecture-baseline at 2795eb7 preserves the combined four-commit history and recovery reference. Local branch preparation authorizes no PR, push, merge, installation or rollout.

The scheduling baseline contains the [current scheduling contract](scheduling-and-recurrence.md), exact-time preservation/display fixes, DST-safe SQL count bounds and the first list/editor policy extraction. The desktop refactor is its local descendant; it separates read/search ownership, the connected-sync sequence and navigation context/selection memory. It retains SQL write transactions and async editor/navigation sequencing, and contains no Windows continuity implementation. Label rename remains parked for a future dedicated label list; equivalent task-editor label changes remain a no-op.

## Bounded follow-ups

- Preserve the decided independent project/status contract. Inbox is default, None is explicit no-workflow-status, the Inbox view omits project-assigned tasks, and project-assigned Inbox-status tasks intentionally keep their correct Inbox badge.
- Further persistence, setup or presentation separation needs a concrete responsibility benefit beyond this read/sync/navigation slice. Preserve SQL write/outbox/checkpoint boundaries, provider authority and editor save/refresh/selection behavior; do not split files merely for size or treat the remaining direct host/store call sites as a mandate for a generic framework.
- Before wider planning features, settle scheduled-session shape, recurrence grammar/occurrence identity and completion/skip/rollover. Reminder definitions and delivery attempts need separate design.
- In the owning Personal Cloud repository, verify compatibility, migration/preflight, artifact provenance, recovery and desktop/Web round trips. Keep infrastructure setup separate from task semantics.
- Validate independent-user onboarding and sustained use; source tests alone do not establish real-provider acceptance.

## Windows continuity: active on Ubuntu

Continue Windows-compatible continuity design, C# compilation and synthetic/integration tests on Ubuntu. Keep OS-specific behavior behind platform services and retain the Dev gate. Native build/validation, packaging/distribution, MSIX upgrade testing and Store replacement are not current priorities. The [continuity plan](windows-transition.md) keeps later package/vault/sign-in/pairing evidence separate from current source checks. Upgraded-user reconnect and initial sync-off policy remain user decisions; the Inbox contract is already decided. Do not disrupt Preview or Production.

Deferred ideas need a concrete case: macOS, custom statuses, time tracking, external capture/automation, additional adapters, task tabs and broader search/project-tree changes. A CLI already exists. Flutter/Rust-era notes are historical.
