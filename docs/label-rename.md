# Task-editor label behavior

The task editor attaches and removes labels; it does not rename shared labels.

Capital-only changes and other label lists equivalent under the editor's existing case-insensitive, order-independent comparison are a no-op. The change check and save-request builder use the same comparison. An equivalent label edit alone produces no save request. When another field changes, its save request omits labels, preserving their stored names and links.

Deliberate shared-label renaming will belong to a dedicated label list screen. That screen does not exist yet and is outside this change.

Shared-label rename remains deferred. Its design is retained on
`parked/label-rename-desktop` and the companion `fix/atomic-shared-label-rename`
branch for future label-list work.
