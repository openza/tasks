use std::collections::{HashMap, HashSet};

use log::{debug, info};

use crate::db::Repository;
use crate::domain::{Label, Project, SyncResult, SyncSummary, Task};

/// Sync engine for processing remote data and updating local database
pub struct SyncEngine {
    db_path: String,
}

impl SyncEngine {
    pub fn new(db_path: String) -> Self {
        Self { db_path }
    }

    /// Sort tasks so that parent tasks come before child tasks (topological sort)
    /// Also validates project_id references against available projects
    fn prepare_tasks_for_insert(
        tasks: Vec<Task>,
        valid_project_ids: &HashSet<String>,
    ) -> Vec<Task> {
        let task_ids: HashSet<String> = tasks.iter().map(|t| t.id.clone()).collect();

        // Separate tasks into those without parents and those with parents
        let mut no_parent: Vec<Task> = Vec::new();
        let mut with_parent: Vec<Task> = Vec::new();

        for mut task in tasks {
            // Clear project_id if it doesn't exist in our projects
            if let Some(ref pid) = task.project_id {
                if !valid_project_ids.contains(pid) {
                    task.project_id = None;
                }
            }

            // Clear parent_id if the parent isn't in our task set
            if let Some(ref parent_id) = task.parent_id {
                if !task_ids.contains(parent_id) {
                    task.parent_id = None;
                }
            }

            if task.parent_id.is_none() {
                no_parent.push(task);
            } else {
                with_parent.push(task);
            }
        }

        // Build result: start with tasks that have no parent
        let mut result = no_parent;
        let mut inserted_ids: HashSet<String> = result.iter().map(|t| t.id.clone()).collect();

        // Iteratively add tasks whose parents have been inserted
        let mut remaining = with_parent;
        let mut max_iterations = remaining.len() + 1; // Prevent infinite loop

        while !remaining.is_empty() && max_iterations > 0 {
            max_iterations -= 1;
            let mut still_remaining = Vec::new();

            for task in remaining {
                if let Some(ref parent_id) = task.parent_id {
                    if inserted_ids.contains(parent_id) {
                        inserted_ids.insert(task.id.clone());
                        result.push(task);
                    } else {
                        still_remaining.push(task);
                    }
                } else {
                    inserted_ids.insert(task.id.clone());
                    result.push(task);
                }
            }

            remaining = still_remaining;
        }

        // Add any remaining tasks (orphans with broken parent references)
        for mut task in remaining {
            task.parent_id = None; // Clear invalid parent reference
            result.push(task);
        }

        result
    }

    /// Perform initial sync: clear existing provider data and insert fresh data
    /// All operations are wrapped in a transaction for atomicity
    pub fn initial_sync(
        &self,
        provider: &str,
        remote_tasks: Vec<Task>,
        remote_projects: Vec<Project>,
        remote_labels: Vec<Label>,
    ) -> SyncResult<SyncSummary> {
        info!(
            "[{}] Starting initial sync: {} tasks, {} projects, {} labels",
            provider,
            remote_tasks.len(),
            remote_projects.len(),
            remote_labels.len()
        );

        let mut repo = Repository::new(&self.db_path)?;

        // Ensure required tables/rows exist (outside transaction - DDL)
        repo.ensure_pending_completions_table()?;
        repo.ensure_integration_exists(provider)?;

        // Build set of valid project IDs before transaction
        let valid_project_ids: HashSet<String> =
            remote_projects.iter().map(|p| p.id.clone()).collect();

        // Sort tasks for proper insertion order and validate references
        let sorted_tasks = Self::prepare_tasks_for_insert(remote_tasks, &valid_project_ids);

        // Wrap all sync operations in a transaction for atomicity
        let tx = repo.begin_transaction()?;

        // Clear existing data for this provider
        debug!("[{}] Clearing existing data before initial sync", provider);
        let tasks_deleted = {
            let prefix = format!("{}_", provider);
            tx.execute(
                "DELETE FROM tasks WHERE json_extract(integrations, '$.provider') = ?1 OR id LIKE ?2",
                rusqlite::params![provider, format!("{}%", prefix)],
            )? as i32
        };
        debug!("[{}] Cleared {} existing tasks", provider, tasks_deleted);
        {
            let prefix = format!("{}_", provider);
            tx.execute(
                "DELETE FROM projects WHERE json_extract(integrations, '$.provider') = ?1 OR id LIKE ?2",
                rusqlite::params![provider, format!("{}%", prefix)],
            )?;
        }
        {
            let prefix = format!("{}_", provider);
            tx.execute(
                "DELETE FROM labels WHERE json_extract(integrations, '$.provider') = ?1 OR id LIKE ?2",
                rusqlite::params![provider, format!("{}%", prefix)],
            )?;
        }

        // Insert projects first (foreign key dependency)
        for project in &remote_projects {
            let integrations_json = project
                .integrations
                .as_ref()
                .map(|v| serde_json::to_string(v))
                .transpose()?;

            tx.execute(
                "INSERT OR REPLACE INTO projects (id, name, description, color, icon, parent_id,
                                                  sort_order, is_favorite, is_archived, integrations,
                                                  created_at, updated_at)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
                rusqlite::params![
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
        }

        // Insert labels
        for label in &remote_labels {
            let integrations_json = label
                .integrations
                .as_ref()
                .map(|v| serde_json::to_string(v))
                .transpose()?;

            tx.execute(
                "INSERT OR REPLACE INTO labels (id, name, color, description, sort_order,
                                                integrations, created_at)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
                rusqlite::params![
                    label.id,
                    label.name,
                    label.color,
                    label.description,
                    label.sort_order,
                    integrations_json,
                    label.created_at.timestamp(),
                ],
            )?;
        }

        // Insert tasks in sorted order
        for task in &sorted_tasks {
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

            tx.execute(
                "INSERT INTO tasks (id, title, description, project_id, parent_id, priority, status,
                                   due_date, due_time, estimated_duration, actual_duration,
                                   energy_level, context, focus_time, notes, source_task, integrations,
                                   created_at, updated_at, completed_at)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19, ?20)",
                rusqlite::params![
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
                    task.updated_at.unwrap_or(task.created_at).timestamp(),
                    task.completed_at.map(|dt| dt.timestamp()),
                ],
            )?;

            // Sync task labels: delete existing and insert new
            tx.execute("DELETE FROM task_labels WHERE task_id = ?1", rusqlite::params![task.id])?;
            for label_id in &task.labels {
                tx.execute(
                    "INSERT OR IGNORE INTO task_labels (task_id, label_id) VALUES (?1, ?2)",
                    rusqlite::params![task.id, label_id],
                )?;
            }
        }

        // Commit transaction - if this fails, all changes are rolled back
        tx.commit()?;

        info!(
            "[{}] Initial sync complete: +{} tasks, {} projects, {} labels (deleted {} old)",
            provider,
            sorted_tasks.len(),
            remote_projects.len(),
            remote_labels.len(),
            tasks_deleted
        );

        Ok(SyncSummary {
            tasks_added: sorted_tasks.len() as i32,
            tasks_updated: 0,
            tasks_deleted,
            projects_synced: remote_projects.len() as i32,
            labels_synced: remote_labels.len() as i32,
            success: true,
            error: None,
            new_sync_token: None,
        })
    }

    /// Perform incremental sync: diff remote vs local and apply changes
    /// Uses "remote wins" strategy for conflicts
    /// All write operations are wrapped in a transaction for atomicity
    pub fn incremental_sync(
        &self,
        provider: &str,
        remote_tasks: Vec<Task>,
        remote_projects: Vec<Project>,
        remote_labels: Vec<Label>,
        sync_token: Option<String>,
    ) -> SyncResult<SyncSummary> {
        info!(
            "[{}] Starting incremental sync: {} tasks, {} projects, {} labels (token: {:?})",
            provider,
            remote_tasks.len(),
            remote_projects.len(),
            remote_labels.len(),
            sync_token.as_deref().map(|s| if s.len() > 20 { &s[..20] } else { s })
        );

        let mut repo = Repository::new(&self.db_path)?;

        // Ensure required tables/rows exist (outside transaction - DDL)
        repo.ensure_pending_completions_table()?;
        repo.ensure_integration_exists(provider)?;

        // Build set of valid project IDs before transaction
        let valid_project_ids: HashSet<String> =
            remote_projects.iter().map(|p| p.id.clone()).collect();

        // Prepare tasks (sort and validate references)
        let sorted_tasks = Self::prepare_tasks_for_insert(remote_tasks, &valid_project_ids);

        // Get existing local tasks BEFORE transaction (read operation)
        let local_tasks = repo.get_tasks_by_provider(provider)?;
        let local_task_map: HashMap<String, Task> =
            local_tasks.into_iter().map(|t| (t.id.clone(), t)).collect();

        // Build set of remote task IDs for deletion detection
        let remote_task_ids: HashSet<String> =
            sorted_tasks.iter().map(|t| t.id.clone()).collect();

        let mut tasks_added = 0;
        let mut tasks_updated = 0;
        let mut tasks_deleted = 0;

        // Wrap all write operations in a transaction for atomicity
        let tx = repo.begin_transaction()?;

        // Sync projects first (upsert)
        for project in &remote_projects {
            let integrations_json = project
                .integrations
                .as_ref()
                .map(|v| serde_json::to_string(v))
                .transpose()?;

            tx.execute(
                "INSERT OR REPLACE INTO projects (id, name, description, color, icon, parent_id,
                                                  sort_order, is_favorite, is_archived, integrations,
                                                  created_at, updated_at)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
                rusqlite::params![
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
        }

        // Sync labels
        for label in &remote_labels {
            let integrations_json = label
                .integrations
                .as_ref()
                .map(|v| serde_json::to_string(v))
                .transpose()?;

            tx.execute(
                "INSERT OR REPLACE INTO labels (id, name, color, description, sort_order,
                                                integrations, created_at)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
                rusqlite::params![
                    label.id,
                    label.name,
                    label.color,
                    label.description,
                    label.sort_order,
                    integrations_json,
                    label.created_at.timestamp(),
                ],
            )?;
        }

        // Process remote tasks in sorted order
        for task in &sorted_tasks {
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

            if local_task_map.contains_key(&task.id) {
                // Task exists locally - update sync fields only, preserve local-only fields
                tx.execute(
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
                    rusqlite::params![
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
                        task.updated_at.unwrap_or(task.created_at).timestamp(),
                        task.completed_at.map(|dt| dt.timestamp()),
                    ],
                )?;
                tasks_updated += 1;
            } else {
                // New task from remote
                tx.execute(
                    "INSERT INTO tasks (id, title, description, project_id, parent_id, priority, status,
                                       due_date, due_time, estimated_duration, actual_duration,
                                       energy_level, context, focus_time, notes, source_task, integrations,
                                       created_at, updated_at, completed_at)
                     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19, ?20)",
                    rusqlite::params![
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
                        task.updated_at.unwrap_or(task.created_at).timestamp(),
                        task.completed_at.map(|dt| dt.timestamp()),
                    ],
                )?;
                tasks_added += 1;
            }

            // Sync task labels: delete existing and insert new
            tx.execute("DELETE FROM task_labels WHERE task_id = ?1", rusqlite::params![task.id])?;
            for label_id in &task.labels {
                tx.execute(
                    "INSERT OR IGNORE INTO task_labels (task_id, label_id) VALUES (?1, ?2)",
                    rusqlite::params![task.id, label_id],
                )?;
            }
        }

        // Detect deleted tasks (in local but not in remote)
        // Only for full sync (when sync_token indicates full refresh)
        if sync_token.as_deref() == Some("*") || sync_token.is_none() {
            for (local_id, _) in &local_task_map {
                if !remote_task_ids.contains(local_id) {
                    tx.execute("DELETE FROM tasks WHERE id = ?1", rusqlite::params![local_id])?;
                    tasks_deleted += 1;
                }
            }
        }

        // Commit transaction - if this fails, all changes are rolled back
        tx.commit()?;

        info!(
            "[{}] Incremental sync complete: +{} tasks, ~{} updated, -{} deleted",
            provider, tasks_added, tasks_updated, tasks_deleted
        );

        Ok(SyncSummary {
            tasks_added,
            tasks_updated,
            tasks_deleted,
            projects_synced: remote_projects.len() as i32,
            labels_synced: remote_labels.len() as i32,
            success: true,
            error: None,
            new_sync_token: sync_token,
        })
    }

    /// Clear all data for a provider (for re-sync)
    pub fn clear_provider_data(&self, provider: &str) -> SyncResult<i32> {
        info!("[{}] Clearing all provider data for re-sync", provider);
        let repo = Repository::new(&self.db_path)?;
        let deleted = repo.delete_tasks_by_provider(provider)?;
        repo.delete_projects_by_provider(provider)?;
        repo.delete_labels_by_provider(provider)?;
        info!("[{}] Cleared {} tasks", provider, deleted);
        Ok(deleted)
    }

    /// Get pending completions to push to provider
    pub fn get_pending_completions(
        &self,
        provider: &str,
    ) -> SyncResult<Vec<crate::domain::PendingCompletion>> {
        let repo = Repository::new(&self.db_path)?;
        repo.ensure_pending_completions_table()?;
        let completions = repo.get_pending_completions(provider)?;
        if !completions.is_empty() {
            debug!("[{}] Found {} pending completions to sync", provider, completions.len());
        }
        Ok(completions)
    }

    /// Mark a completion as synced (remove from queue)
    pub fn mark_completion_synced(&self, completion_id: &str) -> SyncResult<()> {
        debug!("Marking completion {} as synced", completion_id);
        let repo = Repository::new(&self.db_path)?;
        repo.remove_pending_completion(completion_id)
    }

    /// Update sync token after successful sync
    pub fn update_sync_token(&self, provider: &str, sync_token: &str) -> SyncResult<()> {
        let repo = Repository::new(&self.db_path)?;
        repo.update_sync_token(provider, sync_token)
    }

    /// Get current sync token for a provider
    pub fn get_sync_token(&self, provider: &str) -> SyncResult<Option<String>> {
        let repo = Repository::new(&self.db_path)?;
        repo.get_sync_token(provider)
    }
}
