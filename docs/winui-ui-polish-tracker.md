# Openza Tasks WinUI UI Polish Tracker

This tracker exists so the Store V1 UI polish work does not drift into vague claims. Update the checklist only after the implementation is present in code and the app project builds.

Last updated: 2026-05-14

## Status Legend

- `[ ]` Not started
- `[~]` In progress or partially done
- `[x]` Implemented and build-verified
- `[!]` Implemented but still needs manual visual review

## Native WinUI Guardrails

- Keep the current WinUI workbench architecture: left app navigation, optional projects pane, task list, and right detail/intake pane.
- Use native WinUI controls and Fluent patterns instead of custom web-style UI.
- Keep `NavigationView`, `ListView`, `CommandBar`, `AutoSuggestBox`, `ComboBox`, `CalendarDatePicker`, `SettingsCard`, and `ContentDialog` where they fit.
- Prefer quiet Windows surfaces, subtle row separation, clear keyboard focus, and restrained semantic color.
- Do not implement planner/calendar/time-block UI in this pass.

## Checklist

### 1. Task List Outline Rows

- [!] Use lightweight outline rows instead of card-heavy task cards.
- [x] Keep checkbox/status control on the left.
- [x] Keep title as the only strong text.
- [x] Use a single quiet metadata line.
- [x] Hide row actions until hover/focus.
- [x] Use native soft selected state, not a thick card.
- [x] Make metadata pieces visually deliberate rather than a single dense string.

### 2. Metadata Rules

- [x] Hide `Normal` and `Low` priority from the row.
- [x] Show only `High` and `Urgent` priority as quiet cues.
- [x] Hide project name in project-filtered views.
- [x] Hide redundant Date/Deadline text in Today and Overdue views.
- [x] Show source quietly for connected tasks.
- [x] Show labels max 2, then `+N`.
- [x] Hide status chip when already inside that smart list.
- [!] In Inbox, make connected source/list context clear without clutter.

### 3. Task Details Pane

- [x] Add a completion-circle/title-led header.
- [x] Keep description and notes as the main editable content.
- [x] Keep organize fields grouped: Status, Project, Date, Deadline, Priority, Labels.
- [x] Put source/provider fields in a separate quiet section.
- [x] Keep Save primary and Cancel secondary.
- [x] Keep Delete and Complete separate from Save.
- [x] Reduce provider/source explanation from a prominent top block to quiet source context unless there is an error.

### 4. Inbox Intake

- [x] Remove the connected-app task banner from the main task list.
- [x] Use right-side review drawer.
- [x] Support Add, Skip, Unskip, and Show skipped.
- [x] Search within intake.
- [x] Filter intake by source app.
- [x] Filter intake by source list/project.
- [x] Make row hierarchy: title, source app, source list, due, priority.
- [x] Make Add primary and Skip secondary/quiet per row.

### 5. Sidebar Counts

- [x] Replace noisy blue `InfoBadge` counts.
- [x] Inbox count should use the same quiet treatment as other non-danger counts.
- [x] Overdue count should be danger-colored only when > 0.
- [x] Today count should be muted.
- [x] Waiting/Someday should be quiet.
- [x] Hide broad Tasks and Completed total badges.

### 6. Filters And Sorting

- [x] Keep search visible.
- [x] Make filter controls compact enough not to compete with the task list.
- [x] Show active filters as a concise summary.
- [x] Move inactive/advanced filters behind a filter button or overflow.

### 7. Empty States

- [x] Use calm native empty state styling.
- [x] Tailor copy per smart list.
- [x] For Inbox, mention connected-app intake only when relevant.
- [x] Avoid duplicate “Add task” noise when the first-run setup panel already has the primary action.

## Verification Checklist

- [x] `dotnet test src\Openza.Tasks.Tests\Openza.Tasks.Tests.csproj -c Release --no-restore`
- [x] `dotnet build src\Openza.Tasks\Openza.Tasks.csproj -c Release --no-restore`
- [ ] Manual visual check: Inbox with connected-app intake.
- [ ] Manual visual check: Today, Tasks, project-filtered view, empty state.
- [ ] Manual visual check: provider-linked task details.

## Visual Feedback Log

- [x] Sidebar count pills were clipped at the pane edge after replacing `InfoBadge`; nav item content width was reduced and badges now reserve visible space inside the pane.
- [x] Sidebar count text alignment looked uneven; count badges now use fixed 22px circular geometry for one/two digit counts, centered text metrics, and a capped `99+` wider pill for large counts.
- [x] Sidebar count pills still felt visually off after the custom badge pass; counts now use native `NavigationViewItem.InfoBadge` placement again with quiet app styling, and Inbox no longer uses accent blue.
- [x] The connected-app intake banner consumed Inbox vertical space; Inbox now opens the right-side connected-app task drawer instead of rendering a banner above local tasks.
