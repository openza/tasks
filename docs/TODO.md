# Openza Flutter - Future Development Roadmap

This document outlines features that need to be implemented to achieve full parity with the Electron desktop version, plus additional enhancements.

**Migration Status:** ~70-75% complete

---

## Recently Completed Improvements

### Riverpod v3 Upgrade (December 2024)
- Upgraded from flutter_riverpod ^2.6.1 to ^3.0.3
- Upgraded riverpod_annotation and riverpod_generator to v3.0.3
- Added legacy imports for backward compatibility with StateNotifierProvider

### Freezed v3 Migration (December 2024)
- Upgraded from freezed ^2.5.7 to ^3.0.0
- Converted all entities to use `@freezed` with `sealed class` pattern:
  - `TaskEntity` - Auto-generated copyWith, equality, toString
  - `ProjectEntity` - With custom `isInbox` getter preserved
  - `LabelEntity` - Full JSON serialization support
- Eliminated ~100+ lines of manual boilerplate code

### P0 Features Implementation (December 2024)
- **OAuth Authentication** - Connected login screen to OAuthService with loading states and error toasts
- **Token Refresh** - Created TokenManager with automatic refresh, exponential backoff, and 401 handling
- **API Error Handling** - Added ApiErrorHandler, ApiErrorStream, and ApiErrorListener for toast notifications

---

## Critical Priority (P0) - COMPLETED

### ~~1. OAuth Authentication Flows~~
- [x] Login screen now connects to OAuthService
- [x] Loading states and error toasts during auth
- [x] Added oauthServiceProvider and tokenManagerProvider
- [x] Auth state persistence via SecureStorageService

Note: Deep links (openza://) for OAuth callbacks not implemented. Using local HTTP server callback instead (works on desktop).

---

### ~~2. MS To-Do Token Refresh & Auth Resilience~~
- [x] Created TokenManager (`lib/data/datasources/remote/auth/token_manager.dart`)
- [x] Automatic token refresh before expiry (5 min threshold)
- [x] Exponential backoff for retry attempts (1s → 2s → 4s → max 30s, 3 attempts)
- [x] 401 response handling with auto-refresh in MsToDoApi interceptor
- [x] Debounced refresh to prevent concurrent requests
- [x] Token expiry metadata tracking via getMsToDoTokenExpiry()

---

### ~~3. API Error Handling & User Feedback~~
- [x] Created ApiErrorHandler (`lib/core/services/api_error_handler.dart`)
- [x] Toast notifications on API errors via ApiErrorListener
- [x] Structured error types (network, timeout, unauthorized, forbidden, etc.)
- [x] Task providers updated with proper error handling
- [ ] Loading indicators during sync (partial - providers handle async)
- [ ] Retry buttons on failure (not implemented)
- [ ] Offline state detection (not implemented)

---

## High Priority

### ~~4. Rich Task Card Details~~ - DEPRECATED

These fields were removed from Electron in commit `52fc4e4` to simplify task creation:
- Energy Level, Focus Time, Context, Estimated Duration

**Remaining (optional):**
- [ ] Task source indicator label (Local/Todoist/MS To-Do)

---

### 4. Next Actions Label Filter Buttons

**Problem:** Can't filter Next Actions screen by individual labels.

**What's Missing:**
- [ ] "All Labels" button with total task count
- [ ] Individual label filter buttons with:
  - Label color
  - Label name
  - Task count badge
  - Hover/selection effects

**Impact:** Users must scroll through all labeled tasks instead of filtering.

**Files to Modify:**
- `lib/presentation/screens/next_actions/next_actions_screen.dart`

**Electron Reference:**
- `openza-desktop/src/routes/NextAction.tsx`

---

### 6. Advanced Sorting UI

**Problem:** Limited sorting options in task lists.

**What's Missing:**
- [ ] Visual sort dropdown component
- [ ] Sort options:
  - By labels (alphabetical)
  - By project name
  - By priority (1=highest)
  - By due date
  - By created date

**Files to Modify:**
- `lib/presentation/widgets/tasks/task_list.dart`
- `lib/presentation/screens/tasks/tasks_screen.dart`

**Electron Reference:**
- `openza-desktop/src/components/TasksWithTabs.tsx` (sort dropdown)

---

### 7. Task Detail Inline Editing

**Problem:** Task detail panel is view-only. Users cannot edit tasks from the detail view.

**What's Missing:**
- [ ] Edit mode toggle button
- [ ] Inline title editing (click to edit)
- [ ] Description text area editing
- [ ] Priority selector dropdown
- [ ] Due date picker
- [ ] Label editor (add/remove labels)
- [ ] Energy level selector
- [ ] Context selector
- [ ] Estimated duration input
- [ ] Save/cancel buttons
- [ ] Unsaved changes warning

**Impact:** Users must use the create dialog for all task edits.

**Files to Modify:**
- `lib/presentation/widgets/tasks/task_detail.dart`

**Electron Reference:**
- `openza-desktop/src/components/TaskDetail.tsx`

---

## Medium Priority

### 8. Task Tabs System

**Problem:** Users can only view one task detail at a time.

**What's Missing:**
- [ ] Multi-tab interface for viewing multiple task details
- [ ] Ctrl+click to open task in new tab
- [ ] Tab close buttons (X icon)
- [ ] Tab switching with keyboard
- [ ] Main "Tasks" tab that cannot be closed

**Impact:** Users lose context when switching between tasks.

**Files to Create:**
- `lib/presentation/widgets/tasks/tasks_with_tabs.dart`

**Note:** Widget partially exists but not integrated.

**Electron Reference:**
- `openza-desktop/src/components/TasksWithTabs.tsx`

---

### 9. Search Functionality

**Problem:** No way to search for tasks.

**What's Needed:**
- [ ] Search bar in header/toolbar
- [ ] Full-text search using SQLite FTS5
- [ ] Search across title, description, and notes
- [ ] Real-time search results
- [ ] Search result highlighting

**Files to Create:**
- `lib/presentation/widgets/common/search_bar.dart`
- Update `lib/data/datasources/local/database/database.dart` with FTS triggers

**Note:** Database `searchTasks()` method exists but no UI.

**Database Schema Update:**
```sql
CREATE VIRTUAL TABLE task_search USING fts5(
  title, description, notes,
  content='tasks', content_rowid='rowid'
);
```

---

### 10. Nested Project Hierarchy

**Problem:** Project sidebar shows flat list. Electron has nested tree structure.

**What's Missing:**
- [ ] Tree view for nested projects (projects have parent_id)
- [ ] Expand/collapse indicators
- [ ] Indent for child projects
- [ ] Drag-and-drop reordering (optional)

**Impact:** Users can't organize projects hierarchically.

**Files to Modify:**
- `lib/presentation/widgets/layout/dashboard_layout.dart`

**Electron Reference:**
- `openza-desktop/src/components/Projects.tsx`

---

### 11. Window State Persistence

**Problem:** App window always starts centered with default size.

**What's Missing:**
- [ ] Save window position on close
- [ ] Save window size on close
- [ ] Restore window state on startup
- [ ] Remember maximized state

**Files to Modify:**
- `lib/main.dart`

**Dependencies to Add:**
- `shared_preferences` for window state storage

---

## Low Priority

### 12. Single Instance Lock

**Problem:** Multiple app instances can run simultaneously.

**What's Needed:**
- [ ] Prevent launching second instance
- [ ] Focus existing window when user tries to open again
- [ ] Handle deep link URLs in running instance

**Implementation:** Use `window_manager` package or platform channels.

---

### 13. Keyboard Shortcuts

**Problem:** No keyboard navigation.

**Common Shortcuts Needed:**
- [ ] `Ctrl+N` / `Cmd+N`: Create new task
- [ ] `Ctrl+F` / `Cmd+F`: Focus search
- [ ] `Escape`: Close dialogs/panels
- [ ] `Ctrl+Enter`: Save task
- [ ] Arrow keys: Navigate task list
- [ ] `Ctrl+1-4`: Set priority
- [ ] `Ctrl+D`: Mark complete/incomplete

**Files to Create:**
- `lib/presentation/widgets/common/keyboard_shortcuts.dart`

---

### 14. System Notifications

**Problem:** No task reminders or notifications.

**What's Needed:**
- [ ] Task due date reminders
- [ ] Overdue task notifications
- [ ] Desktop notification integration

**Dependencies to Add:**
- `flutter_local_notifications`

---

### 15. MS To-Do Categories/Colors

**Problem:** Not fetching Outlook category colors for labels.

**What's Missing:**
- [ ] Fetch `getOutlookCategories()` from MS Graph API
- [ ] Map category colors to label colors
- [ ] Display category colors in label badges

**Electron Reference:**
- `openza-desktop/src/utils/msToDoClient.ts` - `getOutlookCategories()` method

---

### 16. Build & Release Configuration

**Problem:** Can't create distributable packages.

**What's Needed:**
- [ ] Windows: MSIX installer configuration
- [ ] macOS: DMG configuration
- [ ] Linux: AppImage and DEB configuration
- [ ] Code signing setup
- [ ] Auto-update mechanism (optional)

**Files to Create:**
- Update `pubspec.yaml` with build metadata
- Create platform-specific configuration files

---

## Features Flutter Has That Electron Doesn't

These features were added during the Flutter migration:

1. **Enhanced Profile Screen**
   - Weekly progress tracking
   - Priority distribution charts
   - Task statistics visualization

2. **Data & Sync Section in Settings**
   - Clear Cache button
   - Export Data button
   - Manual Sync Now button

3. **More Detailed About Section**
   - Platform information
   - Framework details
   - Extended app description

---

## Reference: Electron File Structure

Key Electron source files for reference when implementing:

```
openza-desktop/
├── src/
│   ├── utils/
│   │   ├── tokenManager.ts      # Token refresh logic (394 lines)
│   │   ├── msToDoClient.ts      # MS To-Do API with 401 handling (497 lines)
│   │   ├── msToDoAuth.ts        # MSAL auth integration (823 lines)
│   │   ├── todoistClient.ts     # Todoist API client
│   │   ├── auth.ts              # Auth state management
│   │   └── secureStorage.ts     # Token storage
│   ├── components/
│   │   ├── TaskCard.tsx         # Rich task card display
│   │   ├── TasksWithTabs.tsx    # Tab system
│   │   ├── TaskList.tsx         # Task list with sorting
│   │   ├── TaskDetail.tsx       # Task detail with editing
│   │   ├── LabelBadge.tsx       # Label display
│   │   └── Projects.tsx         # Sidebar projects (nested)
│   └── routes/
│       ├── NextAction.tsx       # Label filtering UI
│       ├── Dashboard.tsx        # Statistics display
│       └── Tasks.tsx            # Task list view
└── electron/
    └── modules/
        ├── msal.js              # MSAL node integration
        ├── oauth.js             # OAuth callback handling
        ├── protocol.js          # Custom URL scheme
        └── storage.js           # Secure storage handlers
```

---

## Migration Comparison Summary

| Category | Items | Complete | Incomplete | Missing |
|----------|-------|----------|------------|---------|
| Authentication | 6 | 1 | 2 | 3 |
| Database | 10 | 10 | 0 | 0 |
| API Integrations | 9 | 8 | 0 | 1 |
| State Management | 7 | 6 | 1 | 0 |
| Screens | 9 | 5 | 4 | 0 |
| UI Components | 10 | 5 | 3 | 2 |
| Enhanced Features | 7 | 2 | 5 | 0 |
| Infrastructure | 6 | 5 | 1 | 0 |
| **Total** | **64** | **42** | **16** | **6** |

**Overall: 65% Complete, 25% Incomplete, 10% Missing**

---

## Contributing

When implementing these features:

1. Follow existing code patterns in the Flutter project
2. Reference the Electron implementation for behavior details
3. Add tests for new functionality
4. Update this document when features are completed

---

*Last updated: December 2024*
