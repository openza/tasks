use chrono::{TimeZone, Utc};
use rusqlite::{params, Connection, OptionalExtension};

use crate::domain::{Label, PendingCompletion, Project, SyncResult, Task};

/// Database repository for sync operations
pub struct Repository {
    conn: Connection,
}

impl Repository {
    pub fn new(db_path: &str) -> SyncResult<Self> {
        let conn = Connection::open(db_path)?;
        // Configure SQLite for safe concurrent access with Dart/Drift
        // WAL mode allows concurrent readers during writes
        // busy_timeout waits instead of failing immediately on lock contention
        conn.execute_batch(
            "PRAGMA journal_mode = WAL;
             PRAGMA busy_timeout = 5000;
             PRAGMA synchronous = NORMAL;
             PRAGMA foreign_keys = ON;",
        )?;
        Ok(Self { conn })
    }

    /// Begin a transaction
    pub fn begin_transaction(&mut self) -> SyncResult<rusqlite::Transaction<'_>> {
        Ok(self.conn.transaction()?)
    }

    // ============ TASK OPERATIONS ============

    /// Get all tasks for a specific integration
    pub fn get_tasks_by_integration(&self, integration_id: &str) -> SyncResult<Vec<Task>> {
        let mut stmt = self.conn.prepare(
            "SELECT id, external_id, integration_id, title, description, project_id, parent_id,
                    priority, status, due_date, due_time, notes, provider_metadata,
                    created_at, updated_at, completed_at
             FROM tasks
             WHERE integration_id = ?1",
        )?;

        let tasks = stmt
            .query_map(params![integration_id], |row| {
                Ok(Task {
                    id: row.get(0)?,
                    external_id: row.get(1)?,
                    integration_id: row.get(2)?,
                    title: row.get(3)?,
                    description: row.get(4)?,
                    project_id: row.get(5)?,
                    parent_id: row.get(6)?,
                    priority: row.get(7)?,
                    status: row.get(8)?,
                    due_date: row.get::<_, Option<i64>>(9)?.map(|ts| Utc.timestamp_opt(ts, 0).unwrap()),
                    due_time: row.get(10)?,
                    notes: row.get(11)?,
                    provider_metadata: row
                        .get::<_, Option<String>>(12)?
                        .and_then(|s| serde_json::from_str(&s).ok()),
                    created_at: Utc.timestamp_opt(row.get::<_, i64>(13)?, 0).unwrap(),
                    updated_at: row.get::<_, Option<i64>>(14)?.map(|ts| Utc.timestamp_opt(ts, 0).unwrap()),
                    completed_at: row.get::<_, Option<i64>>(15)?.map(|ts| Utc.timestamp_opt(ts, 0).unwrap()),
                    labels: Vec::new(), // Labels loaded separately via task_labels junction table
                })
            })?
            .collect::<Result<Vec<_>, _>>()?;

        Ok(tasks)
    }

    /// Delete all tasks for a specific integration
    pub fn delete_tasks_by_integration(&self, integration_id: &str) -> SyncResult<i32> {
        let deleted = self.conn.execute(
            "DELETE FROM tasks WHERE integration_id = ?1",
            params![integration_id],
        )?;
        Ok(deleted as i32)
    }

    /// Insert a task
    pub fn insert_task(&self, task: &Task) -> SyncResult<()> {
        let provider_metadata_json = task
            .provider_metadata
            .as_ref()
            .map(|v| serde_json::to_string(v))
            .transpose()?;

        self.conn.execute(
            "INSERT INTO tasks (id, external_id, integration_id, title, description, project_id,
                               parent_id, priority, status, due_date, due_time, notes,
                               provider_metadata, created_at, updated_at, completed_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16)",
            params![
                task.id,
                task.external_id,
                task.integration_id,
                task.title,
                task.description,
                task.project_id,
                task.parent_id,
                task.priority,
                task.status,
                task.due_date.map(|dt| dt.timestamp()),
                task.due_time,
                task.notes,
                provider_metadata_json,
                task.created_at.timestamp(),
                task.updated_at.unwrap_or(task.created_at).timestamp(),
                task.completed_at.map(|dt| dt.timestamp()),
            ],
        )?;
        Ok(())
    }

    /// Update a task
    pub fn update_task(&self, task: &Task) -> SyncResult<()> {
        let provider_metadata_json = task
            .provider_metadata
            .as_ref()
            .map(|v| serde_json::to_string(v))
            .transpose()?;

        self.conn.execute(
            "UPDATE tasks SET
                title = ?2,
                description = ?3,
                project_id = ?4,
                parent_id = ?5,
                priority = ?6,
                status = ?7,
                due_date = ?8,
                due_time = ?9,
                provider_metadata = ?10,
                updated_at = ?11,
                completed_at = ?12
             WHERE id = ?1",
            params![
                task.id,
                task.title,
                task.description,
                task.project_id,
                task.parent_id,
                task.priority,
                task.status,
                task.due_date.map(|dt| dt.timestamp()),
                task.due_time,
                provider_metadata_json,
                task.updated_at.unwrap_or(task.created_at).timestamp(),
                task.completed_at.map(|dt| dt.timestamp()),
            ],
        )?;
        Ok(())
    }

    /// Delete a task by ID
    pub fn delete_task(&self, task_id: &str) -> SyncResult<()> {
        self.conn
            .execute("DELETE FROM tasks WHERE id = ?1", params![task_id])?;
        Ok(())
    }

    // ============ PROJECT OPERATIONS ============

    /// Delete all projects for a specific integration
    pub fn delete_projects_by_integration(&self, integration_id: &str) -> SyncResult<i32> {
        let deleted = self.conn.execute(
            "DELETE FROM projects WHERE integration_id = ?1",
            params![integration_id],
        )?;
        Ok(deleted as i32)
    }

    /// Insert a project
    pub fn insert_project(&self, project: &Project) -> SyncResult<()> {
        let provider_metadata_json = project
            .provider_metadata
            .as_ref()
            .map(|v| serde_json::to_string(v))
            .transpose()?;

        self.conn.execute(
            "INSERT OR REPLACE INTO projects (id, external_id, integration_id, name, description,
                                              color, icon, parent_id, sort_order, is_favorite,
                                              is_archived, provider_metadata, created_at, updated_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14)",
            params![
                project.id,
                project.external_id,
                project.integration_id,
                project.name,
                project.description,
                project.color,
                project.icon,
                project.parent_id,
                project.sort_order,
                project.is_favorite,
                project.is_archived,
                provider_metadata_json,
                project.created_at.timestamp(),
                project.updated_at.map(|dt| dt.timestamp()),
            ],
        )?;
        Ok(())
    }

    // ============ LABEL OPERATIONS ============

    /// Delete all labels for a specific integration
    pub fn delete_labels_by_integration(&self, integration_id: &str) -> SyncResult<i32> {
        let deleted = self.conn.execute(
            "DELETE FROM labels WHERE integration_id = ?1",
            params![integration_id],
        )?;
        Ok(deleted as i32)
    }

    /// Insert a label
    pub fn insert_label(&self, label: &Label) -> SyncResult<()> {
        let provider_metadata_json = label
            .provider_metadata
            .as_ref()
            .map(|v| serde_json::to_string(v))
            .transpose()?;

        self.conn.execute(
            "INSERT OR REPLACE INTO labels (id, external_id, integration_id, name, color,
                                            description, sort_order, is_favorite,
                                            provider_metadata, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)",
            params![
                label.id,
                label.external_id,
                label.integration_id,
                label.name,
                label.color,
                label.description,
                label.sort_order,
                label.is_favorite,
                provider_metadata_json,
                label.created_at.timestamp(),
            ],
        )?;
        Ok(())
    }

    // ============ SYNC METADATA OPERATIONS ============

    /// Get sync token for an integration
    pub fn get_sync_token(&self, integration_id: &str) -> SyncResult<Option<String>> {
        let token = self
            .conn
            .query_row(
                "SELECT sync_token FROM integrations WHERE id = ?1",
                params![integration_id],
                |row| row.get(0),
            )
            .optional()?;
        Ok(token)
    }

    /// Ensure an integration row exists
    pub fn ensure_integration_exists(&self, integration_id: &str) -> SyncResult<()> {
        // Get display name and color based on integration
        let (display_name, color, icon) = match integration_id {
            "todoist" => ("Todoist", "#E44332", "check-circle"),
            "msToDo" => ("Microsoft To-Do", "#00A4EF", "layout-grid"),
            _ => (integration_id, "#808080", "database"),
        };

        self.conn.execute(
            "INSERT OR IGNORE INTO integrations (id, name, display_name, color, icon, is_active, is_configured, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, 1, 1, ?6)",
            params![integration_id, integration_id, display_name, color, icon, Utc::now().timestamp()],
        )?;
        Ok(())
    }

    /// Update sync token for an integration
    pub fn update_sync_token(&self, integration_id: &str, sync_token: &str) -> SyncResult<()> {
        // Ensure the integration row exists first
        self.ensure_integration_exists(integration_id)?;
        self.conn.execute(
            "UPDATE integrations SET sync_token = ?2, last_sync_at = ?3 WHERE id = ?1",
            params![integration_id, sync_token, Utc::now().timestamp()],
        )?;
        Ok(())
    }

    // ============ PENDING COMPLETIONS OPERATIONS ============

    /// Ensure pending_completions table exists
    pub fn ensure_pending_completions_table(&self) -> SyncResult<()> {
        self.conn.execute(
            "CREATE TABLE IF NOT EXISTS pending_completions (
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL,
                provider TEXT NOT NULL,
                provider_task_id TEXT NOT NULL,
                completed INTEGER NOT NULL,
                completed_at INTEGER,
                created_at INTEGER NOT NULL,
                retry_count INTEGER DEFAULT 0
            )",
            [],
        )?;
        Ok(())
    }

    /// Queue a completion for sync
    pub fn queue_completion(&self, completion: &PendingCompletion) -> SyncResult<()> {
        self.conn.execute(
            "INSERT OR REPLACE INTO pending_completions
             (id, task_id, provider, provider_task_id, completed, completed_at, created_at, retry_count)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
            params![
                completion.id,
                completion.task_id,
                completion.provider,
                completion.provider_task_id,
                completion.completed,
                completion.completed_at.map(|dt| dt.timestamp()),
                completion.created_at.timestamp(),
                completion.retry_count,
            ],
        )?;
        Ok(())
    }

    /// Get pending completions for an integration
    pub fn get_pending_completions(&self, integration_id: &str) -> SyncResult<Vec<PendingCompletion>> {
        let mut stmt = self.conn.prepare(
            "SELECT id, task_id, provider, provider_task_id, completed, completed_at, created_at, retry_count
             FROM pending_completions
             WHERE provider = ?1
             ORDER BY created_at ASC",
        )?;

        let completions = stmt
            .query_map(params![integration_id], |row| {
                Ok(PendingCompletion {
                    id: row.get(0)?,
                    task_id: row.get(1)?,
                    provider: row.get(2)?,
                    provider_task_id: row.get(3)?,
                    completed: row.get(4)?,
                    completed_at: row.get::<_, Option<i64>>(5)?.map(|ts| Utc.timestamp_opt(ts, 0).unwrap()),
                    created_at: Utc.timestamp_opt(row.get::<_, i64>(6)?, 0).unwrap(),
                    retry_count: row.get(7)?,
                })
            })?
            .collect::<Result<Vec<_>, _>>()?;

        Ok(completions)
    }

    /// Remove a pending completion after successful sync
    pub fn remove_pending_completion(&self, completion_id: &str) -> SyncResult<()> {
        self.conn.execute(
            "DELETE FROM pending_completions WHERE id = ?1",
            params![completion_id],
        )?;
        Ok(())
    }

    // ============ TASK LABELS OPERATIONS ============

    /// Delete all label associations for a task
    pub fn delete_task_labels(&self, task_id: &str) -> SyncResult<()> {
        self.conn.execute(
            "DELETE FROM task_labels WHERE task_id = ?1",
            params![task_id],
        )?;
        Ok(())
    }

    /// Insert a task-label association
    pub fn insert_task_label(&self, task_id: &str, label_id: &str) -> SyncResult<()> {
        self.conn.execute(
            "INSERT OR IGNORE INTO task_labels (task_id, label_id) VALUES (?1, ?2)",
            params![task_id, label_id],
        )?;
        Ok(())
    }

    /// Sync labels for a task: delete existing associations and insert new ones
    pub fn sync_task_labels(&self, task_id: &str, label_ids: &[String]) -> SyncResult<()> {
        self.delete_task_labels(task_id)?;
        for label_id in label_ids {
            self.insert_task_label(task_id, label_id)?;
        }
        Ok(())
    }
}
