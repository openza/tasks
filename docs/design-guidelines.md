# Openza Tasks Design Guidelines

Openza Tasks uses **Openza Calm Fluent**: a native Windows design language for a calm, focused, local-first task app. Its brand color language comes from the Tasks icon: charcoal, slate, and white.

## Product Personality

- **Native**: Prefer WinUI 3 and Fluent controls, typography, spacing, focus, and accessibility behavior.
- **Calm**: Keep the interface quiet. Do not turn the task list into a dashboard.
- **Focused**: The task title is the strongest element. Metadata, source, labels, and counts stay secondary.
- **Trustworthy**: Status, sync, and source ownership must be clear without alarm-like visual noise.
- **Flexible**: Avoid opinionated workflow judgments. Show user-owned fields and filters; let users decide meaning.

## Color

- Use the Openza Tasks icon palette as the brand anchor: charcoal `#1E293B`, slate `#475569`, and white.
- Reserve the charcoal/slate accent for primary actions, selected project indicators, and focus/selection moments.
- Selected navigation should stay readable and mostly neutral; avoid making the whole sidebar feel blue or over-accented.
- Use semantic colors only for meaning:
  - danger/critical for destructive states or overdue alerts
  - success for confirmed completion/sync success
  - warning for attention-needed states
  - info for neutral application feedback
- Sidebar counts are neutral by default. Do not use brand color for every count.
- Provider/source names are quiet text or neutral chips. Do not color the app by Todoist, Microsoft, or future provider brands.
- All app colors must come through semantic resources in `Styles/DesignTokens.xaml`, with Light, Dark, and High Contrast coverage.

Current palette anchors:

| Role | Light | Dark |
| --- | --- | --- |
| App canvas | `#F6F8FA` | `#0F172A` |
| Pane surface | `#F8FAFC` | `#111827` |
| Detail surface | `#FFFFFF` | `#182030` |
| Elevated surface | `#FFFFFF` | `#1E293B` |
| Row hover | `#F0F3F6` | `#172033` |
| Row selected | `#E9EDF2` | `#223044` |
| Accent | `#1E293B` | `#CBD5E1` |

## Typography

- Use WinUI text styles whenever possible.
- Page titles use `TitleTextBlockStyle`.
- Section titles use `BodyStrongTextBlockStyle` or the shared Openza section styles.
- Task titles use semibold body text and may wrap when useful.
- Metadata uses `OpenzaMetadataTextBlockStyle`.
- Captions, badges, and small chips use `OpenzaCaptionTextBlockStyle`.
- Avoid oversized labels inside dense panes and rows.

## Spacing And Surfaces

- Follow the 4px grid: 4, 8, 12, 16, 24, 32, 48.
- Keep pane padding stable and generous enough for repeated work.
- Use row separators and subtle hover/selection surfaces instead of per-task cards.
- Use elevated/card surfaces only for settings, empty states, intake panels, and source/provider detail groups.
- Avoid nested cards and dashboard-style color blocks.

## Task Rows

Default row anatomy:

```text
checkbox  Task title                         quiet row actions
          Project/list • Date/Deadline • Source • @label
```

Rules:

- No card border per row by default.
- Row hover is subtle; selected state is a soft surface.
- Title is the only strong text.
- Do not show notes or source-description previews in default task rows. Keep long context in the details pane.
- Do not render subtasks as inline rows by default; show only the parent row with quiet progress text such as `2/7 subtasks`.
- When search matches a subtask, surface the parent row with a quiet matching-subtask metadata hint.
- Show only useful metadata:
  - hide normal priority
  - hide redundant project in project views
  - show status only when it helps the current view
  - show at most the useful label subset
- Subtasks are source structure only in V1 and should live in task details, visually subordinate to the parent.
- Subtask rows should look like a compact checklist. Do not repeat the provider name on every child when the parent/source section already establishes the source.

## Details Pane

The details pane is a task inspector, not a form.

- Header: completion checkbox, title, quiet source line, close action.
- On wide panes, split content into two internal zones:
  - main content: local title editor when needed, subtasks, notes
  - property rail: status, project, date, deadline, priority, labels, source
- On narrow panes, stack the property rail below the main content instead of squeezing controls.
- Organize fields should appear as compact property rows: icon, label, current value, and chevron/plus. Do not show a visible grid of ComboBoxes and DatePickers by default.
- Project gets readable room because names are often long. Priority remains compact because values are short.
- Labels are a compact property section: selected chips plus a small add action. Do not use a full-width "Add label" field surface.
- Subtasks are source subtasks/steps, if the parent has any; show completed/total progress and cap long lists behind `Show all`.
- Notes are Openza-owned user text and belong in the main content area.
- Openza-owned fields autosave quietly. Show local `Saving...` / `Saved` state in the pane instead of a toast for every edit.
- Source section:
  - use a collapsed disclosure by default, titled by source, for example `Source: Todoist`
  - auto-expand only when source description or warning content needs attention
  - show provider-owned title/list/date/deadline/priority/source as read-only
  - show source description when available
- Bottom actions:
  - Delete is quiet/destructive
  - Complete/Reopen is explicit and separate from field edits

## Inbox And Intake

- Inbox is the clarify workspace.
- Connected-app tasks enter through review/intake, not directly into daily execution.
- Intake rows show source, source list/project, title, and a short preview when useful.
- Review actions must be close to the intake content and should not shift the main layout.

## Settings And Sync

- Use WinUI Gallery-style settings layouts: grouped sections, SettingsCard, SettingsExpander, icons, descriptions, and right-aligned controls.
- Keep integration language plain:
  - connected apps are source systems
  - Openza owns local planning fields
  - V1 writes back completion/reopen only where supported
- Avoid loose forms and raw technical fields unless unavoidable.

## Feedback And Motion

- Use floating in-app feedback for normal success/info/warning/error messages so the workspace does not move.
- Success/info auto-dismiss; warning/error stay until dismissed.
- Completion from a visible row should feel immediate: checkbox checks, row fades/slides out, list refreshes.
- Avoid celebratory or distracting animation.

## Theme Resources

- Add new visual decisions as semantic resources in `Styles/DesignTokens.xaml`.
- Usage sites should prefer `{ThemeResource Openza...Brush}` for theme-aware resources.
- Do not hardcode colors in XAML or code-behind unless the value is user data or a stored project/label color.
- High Contrast must use system brushes and remain readable without relying on opacity or accent color.
