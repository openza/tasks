# Task-editor label behavior

The task editor attaches and removes labels; it does not rename shared labels.

Capital-only changes and other label lists equivalent under the editor's existing case-insensitive, order-independent comparison are a no-op. The change check and save-request builder use the same comparison. An equivalent label edit alone produces no save request. When another field changes, its save request omits labels, preserving their stored names and links.

Deliberate shared-label renaming will belong to a dedicated label list screen. That screen does not exist yet and is outside this change.

The earlier desktop rename implementation is preserved unmerged on `parked/label-rename-desktop` at `83e0deb`, including `e4a8dbe`. Its atomic mutation and negotiation design is future reference only; it is not the active task-editor behavior. The corresponding Personal Cloud work remains separately parked and unmerged on `fix/atomic-shared-label-rename`.
