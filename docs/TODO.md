# Openza Tasks follow-up order

Direction adopted 2026-10-18: Avalonia is the desktop implementation reference, Windows is the major target and Linux is supported. WinUI is the current Store host in necessary maintenance until deliberate retirement. [Architecture](architecture.md) and [behavior contracts](behavior-contracts.md) define the baseline; [Windows transition](windows-transition.md) defines the upgrade gates.

Each item is a bounded follow-up, not authorization to change live apps or deploy. Preserve behavior and deterministic evidence between chunks.

1. **Windows continuity contract and Dev harness.** Resolve packaged data paths, settings mapping, credential/MSAL/pairing continuity and activation ownership. Build an isolated WinUI Dev → Avalonia Dev upgrade fixture with synthetic data and a documented recovery path before changing distribution.
2. **Windows platform and packaging slices.** Implement/test one service boundary at a time, then isolated MSIX upgrade and native interaction acceptance. Keep necessary WinUI fixes scoped; retirement follows explicit gates, not a successful source build.
3. **Storage responsibility separation.** Characterize transaction boundaries and provider/cloud ownership in `SqliteTaskStore.cs`; extract cohesive persistence responsibilities while keeping atomic writes, outbox, tombstones, idempotency and checkpoint invariants. Existing store partials are a starting point, not a reason for a rewrite. No schema changes merely to shorten files.
4. **Presentation responsibility separation.** Characterize `MainWindowViewModel.cs` flows, then separate task editing, sync/setup and navigation state in bounded changes. Use existing Application operations where semantics match; keep UI-framework dependencies out of Core/Application. Preserve save, refresh, selection and error behavior.
5. **Personal Cloud compatibility and companion structure.** In the owning repository, define a small web structure around task workflows before adding more screens. Verify old deployment compatibility, migration/preflight, pinned artifact provenance, retry/recovery and desktop/web round trips. Keep Cloudflare provisioning separate from task semantics without a generic multi-cloud framework.
6. **Planning semantics before wider use.** Settle scheduled sessions and recurring completion/rollover with concrete cases. Design reminders separately from notification delivery; decide server visibility/encryption and background capabilities before implementing them.

Independent-user onboarding and sustained-use feedback remain acceptance work. Existing Windows Avalonia/Web evidence does not establish Store upgrades or real-provider acceptance. Full native interaction, keyboard/accessibility and packaging checks remain scoped acceptance requirements.

Deferred ideas need a concrete case: macOS, custom statuses, time tracking, external capture/automation, additional adapters, task tabs and broader search/project-tree changes. A CLI already exists; do not carry forward the obsolete blanket “standalone CLI deferred” item. Flutter/Rust-era notes remain historical.
