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
        // Ensure foreign keys are enabled
        conn.execute("PRAGMA foreign_keys = ON", [])?;
        Ok(Self { conn })
    }

    /// Begin a transaction
    pub fn begin_transaction(&mut self) -> SyncResult<rusqlite::Transaction<'_>> {
        Ok(self.conn.transaction()?)
    }

    // ============ TASK OPERATIONS ============

    /// Get all tasks for a specific provider
    pub fn get_tasks_by_provider(&self, provider: &str) -> SyncResult<Vec<Task>> {
        let mut stmt = self.conn.prepare(
            "SELECT id, title, description, project_id, parent_id, priority, status,
                    due_date, due_time, estimated_duration, actual_duration,
                    energy_level, context, focus_time, notes, source_task, integrations,
                    created_at, updated_at, completed_at
             FROM tasks
             WHERE json_extract(integrations, '$.provider') = ?1
                OR id LIKE ?2",
        )?;

        let prefix = format!("{}_", provider);
        let tasks = stmt
            .query_map(params![provider, format!("{}%", prefix)], |row| {
                Ok(Task {
                    id: row.get(0)?,
                    title: row.get(1)?,
                    description: row.get(2)?,
                    project_id: row.get(3)?,
                    parent_id: row.get(4)?,
                    priority: row.get(5)?,
                    status: row.get(6)?,
                    due_date: row.get::<_, Option<i64>>(7)?.map(|ts| Utc.timestamp_opt(ts, 0).unwrap()),
                    due_time: row.get(8)?,
                    estimated_duration: row.get(9)?,
                    actual_duration: row.get(10)?,
                    energy_level: row.get(11)?,
                    context: row.get(12)?,
                    focus_time: row.get(13)?,
                    notes: row.get(14)?,
                    source_task: row
                        .get::<_, Option<String>>(15)?
                        .and_then(|s| serde_json::from_str(&s).ok()),
                    integrations: row
                        .get::<_, Option<String>>(16)?
                        .and_then(|s| serde_json::from_str(&s).ok()),
                    created_at: Utc.timestamp_opt(row.get::<_, i64>(17)?, 0).unwrap(),
                    updated_at: row.get::<_, Option<i64>>(18)?.map(|ts| Utc.timestamp_opt(ts, 0).unwrap()),
                    completed_at: row.get::<_, Option<i64>>(19)?.map(|ts| Utc.timestamp_opt(ts, 0).unwrap()),
                })
            })?
            .collect::<Result<Vec<_>, _>>()?;

        Ok(tasks)
    }

    /// Delete all tasks for a specific provider (for clear and re-sync)
    pub fn delete_tasks_by_provider(&self, provider: &str) -> SyncResult<i32> {
        let prefix = format!("{}_", provider);
        let deleted = self.conn.execute(
            "DELETE FROM tasks
             WHERE json_extract(integrations, '$.provider') = ?1
                OR id LIKE ?2",
            params![provider, format!("{}%", prefix)],
        )?;
        Ok(deleted as i32)
    }

    /// Insert a task
    pub fn insert_task(&self, task: &Task) -> SyncResult<()> {
        let source_task_json = task
            .source_task
            .as_ref()
            .map(|v| serde_json::to_string(v))
            .transpose()?;
        let integrations_json = task
            .integrations
            .as_ref()
            .map(|v| serde_json::to_string(v))
            .transpose()?;

        self.conn.execute(
            "INSERT INTO tasks (id, title, description, project_id, parent_id, priority, status,
                               due_date, due_time, estimated_duration, actual_duration,
                               energy_level, context, focus_time, notes, source_task, integrations,
                               created_at, updated_at, completed_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19, ?20)",
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
                task.estimated_duration,
                task.actual_duration,
                task.energy_level,
                task.context,
                task.focus_time,
                task.notes,
                source_task_json,
                integrations_json,
                task.created_at.timestamp(),
                task.updated_at.unwrap_or(task.created_at).timestamp(), // Use created_at as fallback
                task.completed_at.map(|dt| dt.timestamp()),
            ],
        )?;
        Ok(())
    }

    /// Update a task (preserving local-only fields)
    pub fn update_task(&self, task: &Task) -> SyncResult<()> {
        let source_task_json = task
            .source_task
            .as_ref()
            .map(|v| serde_json::to_string(v))
            .transpose()?;
        let integrations_json = task
            .integrations
            .as_ref()
            .map(|v| serde_json::to_string(v))
            .transpose()?;

        // Update synced fields only, preserve local-only fields (energy_level, context, notes, focus_time)
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
                source_task = ?10,
                integrations = ?11,
                updated_at = ?12,
                completed_at = ?13
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
                source_task_json,
                integrations_json,
                task.updated_at.unwrap_or(task.created_at).timestamp(), // Use created_at as fallback
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

    /// Delete all projects for a specific provider
    pub fn delete_projects_by_provider(&self, provider: &str) -> SyncResult<i32> {
        let prefix = format!("{}_", provider);
        let deleted = self.conn.execute(
            "DELETE FROM projects
             WHERE json_extract(integrations, '$.provider') = ?1
                OR id LIKE ?2",
            params![provider, format!("{}%", prefix)],
        )?;
        Ok(deleted as i32)
    }

    /// Insert a project
    pub fn insert_project(&self, project: &Project) -> SyncResult<()> {
        let integrations_json = project
            .integrations
            .as_ref()
            .map(|v| serde_json::to_string(v))
            .transpose()?;

        self.conn.execute(
            "INSERT OR REPLACE INTO projects (id, name, description, color, icon, parent_id,
                                              sort_order, is_favorite, is_archived, integrations,
                                              created_at, updated_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
            params![
                project.id,
                project.name,
                project.description,
                project.color,
                project.icon,
                project.parent_id,
                project.sort_order,
                project.is_favorite,
                project.is_archived,
                integrations_json,
                project.created_at.timestamp(),
                project.updated_at.map(|dt| dt.timestamp()),
            ],
        )?;
        Ok(())
    }

    // ============ LABEL OPERATIONS ============

    /// Delete all labels for a specific provider
    pub fn delete_labels_by_provider(&self, provider: &str) -> SyncResult<i32> {
        let prefix = format!("{}_", provider);
        let deleted = self.conn.execute(
            "DELETE FROM labels
             WHERE json_extract(integrations, '$.provider') = ?1
                OR id LIKE ?2",
            params![provider, format!("{}%", prefix)],
        )?;
        Ok(deleted as i32)
    }

    /// Insert a label
    pub fn insert_label(&self, label: &Label) -> SyncResult<()> {
        let integrations_json = label
            .integrations
            .as_ref()
            .map(|v| serde_json::to_string(v))
            .transpose()?;

        self.conn.execute(
            "INSERT OR REPLACE INTO labels (id, name, color, description, sort_order,
                                            integrations, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![
                label.id,
                label.name,
                label.color,
                label.description,
                label.sort_order,
                integrations_json,
                label.created_at.timestamp(),
            ],
        )?;
        Ok(())
    }

    // ============ SYNC METADATA OPERATIONS ============

    /// Get sync token for a provider
    pub fn get_sync_token(&self, provider: &str) -> SyncResult<Option<String>> {
        let token = self
            .conn
            .query_row(
                "SELECT sync_token FROM integrations WHERE id = ?1",
                params![provider],
                |row| row.get(0),
            )
            .optional()?;
        Ok(token)
    }

    /// Ensure an integration row exists for a provider
    pub fn ensure_integration_exists(&self, provider: &str) -> SyncResult<()> {
        self.conn.execute(
            "INSERT OR IGNORE INTO integrations (id, name, is_active, created_at)
             VALUES (?1, ?2, 1, ?3)",
            params![provider, provider, Utc::now().timestamp()],
        )?;
        Ok(())
    }

    /// Update sync token for a provider
    pub fn update_sync_token(&self, provider: &str, sync_token: &str) -> SyncResult<()> {
        // Ensure the integration row exists first
        self.ensure_integration_exists(provider)?;
        self.conn.execute(
            "UPDATE integrations SET sync_token = ?2, last_sync_at = ?3 WHERE id = ?1",
            params![provider, sync_token, Utc::now().timestamp()],
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

    /// Get pending completions for a provider
    pub fn get_pending_completions(&self, provider: &str) -> SyncResult<Vec<PendingCompletion>> {
        let mut stmt = self.conn.prepare(
            "SELECT id, task_id, provider, provider_task_id, completed, completed_at, created_at, retry_count
             FROM pending_completions
             WHERE provider = ?1
             ORDER BY created_at ASC",
        )?;

        let completions = stmt
            .query_map(params![provider], |row| {
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
}
