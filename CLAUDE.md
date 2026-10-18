# Openza Tasks context

[AGENTS.md](AGENTS.md) is the authoritative repository instruction source. Use the [behavior contracts](docs/behavior-contracts.md), [architecture](docs/architecture.md) and [design guidance](docs/design-guidelines.md) for product meaning and host ownership.

Avalonia receives active desktop development with built-in Fluent controls; all active code must remain Windows-compatible, with OS specifics behind platform services. WinUI is the current Store host in necessary maintenance. [Windows continuity](docs/windows-transition.md) is active on Ubuntu through C# compilation and synthetic/integration work. Native builds/validation, packaging/distribution, MSIX upgrades and Store replacement are not current priorities. The Dev gate stays; upgraded-user reconnect/sync-off policies remain open.

The fresh-start rule excludes automatic import of Flutter-era stores. Any guarded WinUI-to-Avalonia package transition is a separate explicitly authorized scope. Preserve live-app safeguards, channel identities, credentials and pending intent.

Project and workflow status are independent: assigning a project never changes status and choosing status never changes project. Inbox is the default; None is explicit no-workflow-status. Inbox view membership is open + Inbox status + no project. A project-assigned Inbox-status task remains unprocessed within its project; its Inbox badge is intentional and correct. Preserve this decided contract during cleanup.
