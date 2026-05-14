# Openza Tasks Project Trust Tracker

This tracker keeps the GTD/project-trust work clear and flexible. The goal is not to copy one Obsidian setup or hardcode one person's workflow. The goal is to make Openza trustworthy for different task systems by exposing factual project data, user-defined filters, labels, and views.

Last updated: 2026-05-14

## Product Direction

Openza Tasks should support GTD-style review without forcing a fixed review ritual.

- Spaces are user-owned visibility boundaries. They can separate Work, Personal, or any other context without making those concepts hardcoded app behavior.
- Projects remain user-facing lists/projects.
- Status remains a small workflow field: Inbox, Next, Waiting For, Someday, or none.
- Completion remains separate from status: open, completed, cancelled.
- Contexts, areas, energy, location, people, or custom grouping should be handled through user-defined labels and filters.
- Project review should emerge from the Projects pane and filters, not from a separate mandatory review screen.
- Openza may show factual fields and counts, but should not interpret those facts for the user with judgmental workflow labels.

This follows the flexible Todoist-style model: projects/lists are durable containers, labels are user-defined dimensions, and filters/custom views are how users shape their own workflow. A person name can be a label just like a context, location, energy level, or work area. Openza should not add a dedicated assignee/person field unless a future team-collaboration model needs true ownership semantics.

## Sources Reviewed

- Personal task dashboard: status, personal project, personal context, date/deadline, scheduled.
- Personal review dashboard: active project counts and open tasks grouped by project.
- Work task dashboard: status, work project, work area, assignee, date/deadline, scheduled.
- Work review dashboard: active/completed project state and open task counts.
- Todoist labels and filters: labels are flexible user-defined dimensions; filters can combine project, label, date, priority, and assignment criteria when collaboration needs it.

The shared intent across both personal and work dashboards is:

- Find unclarified inbox items.
- Find next actions.
- Find waiting and someday items.
- Review open tasks by project.
- Review project facts such as open count, next count, status, and labels, then decide what matters.

## Now

- [x] Add a minimal project lifecycle: active, completed, archived.
- [x] Default existing and new projects to active.
- [x] Add factual project-level counts:
  - open task count
  - next-action count
- [x] Add project pane filters:
  - All
  - Active
  - Completed
  - Archived
- [x] Keep project filters dynamic and data-driven, not tied to one user's labels.
- [x] Keep smart lists independent from selected project unless the user explicitly selects a project view.
- [x] Add tests for project lifecycle and project counts.

## Deliberately Not Doing

These are not deferred features. They are choices to keep the app flexible.

- Do not add a hardcoded Work Area field.
- Do not add a hardcoded Personal Context field.
- Do not add hardcoded context/location/energy/person buckets.
- Do not add a dedicated assignee/person field for single-user V1.
- Do not add a separate Project Review page as a fixed ritual.
- Do not add app-judgment signals such as "needs action"; users decide meaning from facts, labels, filters, and views.
- Do not make Obsidian dashboards the product model.
- Do not require users to follow GTD vocabulary to use the app.

## Future Direction

These may be useful later because they preserve user freedom instead of hardcoding a workflow.

- [ ] Better label UX for contexts, areas, people, energy, or any user-defined grouping.
- [ ] Saved filters or custom views.
- [ ] Optional grouping by label, project, source, date, or status.
- [ ] Optional user-created project filters using configurable rules.
- [ ] Optional review helpers inside the Projects pane, such as sort by count, status, label, or last updated.
- [ ] True assignee/owner semantics only if Openza later adds shared workspaces or team collaboration.

## Acceptance Notes

The implementation is correct only if:

- A user can use Openza with no GTD knowledge.
- A GTD user can still maintain Inbox, Next, Waiting, Someday, and project review trust.
- Personal and Work workflows can be represented without hardcoded Personal/Work fields.
- Switching spaces hides other-space projects, tasks, counts, and connected-app intake.
- Project review can be done from the Projects pane through factual counts, filters, and labels.
- Labels remain the flexible mechanism for contexts and other user-defined dimensions.
