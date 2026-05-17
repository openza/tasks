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

    /// Perform initial sync: clear existing integration data and insert fresh data
    /// All operations are wrapped in a transaction for atomicity
    pub fn initial_sync(
        &self,
        integration_id: &str,
        remote_tasks: Vec<Task>,
        remote_projects: Vec<Project>,
        remote_labels: Vec<Label>,
    ) -> SyncResult<SyncSummary> {
        info!(
            "[{}] Starting initial sync: {} tasks, {} projects, {} labels",
            integration_id,
            remote_tasks.len(),
            remote_projects.len(),
            remote_labels.len()
        );

        let mut repo = Repository::new(&self.db_path)?;

        // Ensure required tables/rows exist (outside transaction - DDL)
        repo.ensure_pending_completions_table()?;
        repo.ensure_integration_exists(integration_id)?;

        // Build set of valid project IDs before transaction
        let valid_project_ids: HashSet<String> =
            remote_projects.iter().map(|p| p.id.clone()).collect();

        // Sort tasks for proper insertion order and validate references
        let sorted_tasks = Self::prepare_tasks_for_insert(remote_tasks, &valid_project_ids);

        // Wrap all sync operations in a transaction for atomicity
        let tx = repo.begin_transaction()?;

        // Clear existing data for this integration
        debug!("[{}] Clearing existing data before initial sync", integration_id);
        let tasks_deleted = {
            tx.execute(
                "DELETE FROM tasks WHERE integration_id = ?1",
                rusqlite::params![integration_id],
            )? as i32
        };
        debug!("[{}] Cleared {} existing tasks", integration_id, tasks_deleted);
        tx.execute(
            "DELETE FROM projects WHERE integration_id = ?1",
            rusqlite::params![integration_id],
        )?;
        tx.execute(
            "DELETE FROM labels WHERE integration_id = ?1",
            rusqlite::params![integration_id],
        )?;

        // Insert projects first (foreign key dependency)
        for project in &remote_projects {
            let provider_metadata_json = project
                .provider_metadata
                .as_ref()
                .map(|v| serde_json::to_string(v))
                .transpose()?;

            tx.execute(
                "INSERT OR REPLACE INTO projects (id, external_id, integration_id, name, description,
                                                  color, icon, parent_id, sort_order, is_favorite,
                                                  is_archived, provider_metadata, created_at, updated_at)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14)",
                rusqlite::params![
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
        }

        // Insert labels
        for label in &remote_labels {
            let provider_metadata_json = label
                .provider_metadata
                .as_ref()
                .map(|v| serde_json::to_string(v))
                .transpose()?;

            tx.execute(
                "INSERT OR REPLACE INTO labels (id, external_id, integration_id, name, color,
                                                description, sort_order, is_favorite,
                                                provider_metadata, created_at)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)",
                rusqlite::params![
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
        }

        // Insert tasks in sorted order
        for task in &sorted_tasks {
            let provider_metadata_json = task
                .provider_metadata
                .as_ref()
                .map(|v| serde_json::to_string(v))
                .transpose()?;

            tx.execute(
                "INSERT INTO tasks (id, external_id, integration_id, title, description, project_id,
                                   parent_id, priority, status, due_date, due_time, notes,
                                   provider_metadata, created_at, updated_at, completed_at)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16)",
                rusqlite::params![
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
            integration_id,
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
    ///
    /// When delete_orphans is false, tasks missing from remote are NOT deleted locally.
    /// This is used for sources like Obsidian where the app owns task existence.
    pub fn incremental_sync(
        &self,
        integration_id: &str,
        remote_tasks: Vec<Task>,
        remote_projects: Vec<Project>,
        remote_labels: Vec<Label>,
        sync_token: Option<String>,
        delete_orphans: bool,
    ) -> SyncResult<SyncSummary> {
        info!(
            "[{}] Starting incremental sync: {} tasks, {} projects, {} labels (token: {:?})",
            integration_id,
            remote_tasks.len(),
            remote_projects.len(),
            remote_labels.len(),
            sync_token.as_deref().map(|s| if s.len() > 20 { &s[..20] } else { s })
        );

        let mut repo = Repository::new(&self.db_path)?;

        // Ensure required tables/rows exist (outside transaction - DDL)
        repo.ensure_pending_completions_table()?;
        repo.ensure_integration_exists(integration_id)?;

        // Build set of valid project IDs: include both remote projects and existing local projects
        // This prevents orphaning tasks that reference projects synced in previous batches
        let local_project_ids = repo.get_project_ids_by_integration(integration_id)?;
        let mut valid_project_ids: HashSet<String> =
            remote_projects.iter().map(|p| p.id.clone()).collect();
        valid_project_ids.extend(local_project_ids);

        // Prepare tasks (sort and validate references)
        let sorted_tasks = Self::prepare_tasks_for_insert(remote_tasks, &valid_project_ids);

        // Get existing local tasks BEFORE transaction (read operation)
        let local_tasks = repo.get_tasks_by_integration(integration_id)?;
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
            let provider_metadata_json = project
                .provider_metadata
                .as_ref()
                .map(|v| serde_json::to_string(v))
                .transpose()?;

            tx.execute(
                "INSERT OR REPLACE INTO projects (id, external_id, integration_id, name, description,
                                                  color, icon, parent_id, sort_order, is_favorite,
                                                  is_archived, provider_metadata, created_at, updated_at)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14)",
                rusqlite::params![
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
        }

        // Sync labels
        for label in &remote_labels {
            let provider_metadata_json = label
                .provider_metadata
                .as_ref()
                .map(|v| serde_json::to_string(v))
                .transpose()?;

            tx.execute(
                "INSERT OR REPLACE INTO labels (id, external_id, integration_id, name, color,
                                                description, sort_order, is_favorite,
                                                provider_metadata, created_at)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)",
                rusqlite::params![
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
        }

        // Process remote tasks in sorted order
        let is_wrapper_provider = matches!(integration_id, "todoist" | "msToDo" | "obsidian");
        for task in &sorted_tasks {
            let provider_metadata_json = task
                .provider_metadata
                .as_ref()
                .map(|v| serde_json::to_string(v))
                .transpose()?;

            if local_task_map.contains_key(&task.id) {
                if is_wrapper_provider {
                    // Task exists locally - refresh provider snapshot only.
                    // Wrapper pattern: after initial import, local fields are authoritative.
                    tx.execute(
                        "UPDATE tasks SET
                            provider_metadata = ?2
                         WHERE id = ?1",
                        rusqlite::params![task.id, provider_metadata_json],
                    )?;
                } else {
                    // Non-wrapper providers can update local fields from source.
                    tx.execute(
                        "UPDATE tasks SET
                            title = ?2,
                            description = ?3,
                            parent_id = ?4,
                            priority = ?5,
                            status = ?6,
                            due_date = ?7,
                            due_time = ?8,
                            provider_metadata = ?9,
                            updated_at = ?10,
                            completed_at = ?11
                         WHERE id = ?1",
                        rusqlite::params![
                            task.id,
                            task.title,
                            task.description,
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
                }
                tasks_updated += 1;
            } else {
                // New task from remote
                // project_id comes from remote (null for wrapper pattern)
                // User will organize into local projects later
                // Use INSERT OR REPLACE to handle duplicate IDs in the same batch
                // (e.g., Obsidian tasks with same title under same heading)
                tx.execute(
                    "INSERT OR REPLACE INTO tasks (id, external_id, integration_id, title, description, project_id,
                                       parent_id, priority, status, due_date, due_time, notes,
                                       provider_metadata, created_at, updated_at, completed_at)
                     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16)",
                    rusqlite::params![
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
        // When delete_orphans is true (e.g., Todoist):
        //   - Todoist REST API only returns active tasks, so any task not in
        //     the response was either deleted remotely or completed.
        //   - Preserve locally-completed tasks for history - only delete if
        //     the task was NOT completed locally (status != 'completed')
        // When delete_orphans is false (e.g., Obsidian):
        //   - App owns task existence, so missing tasks are NOT deleted
        //   - User must manually delete if desired
        if delete_orphans {
            for (local_id, local_task) in &local_task_map {
                if !remote_task_ids.contains(local_id) {
                    // Skip deletion for locally-completed tasks to preserve history
                    if local_task.status != "completed" {
                        tx.execute("DELETE FROM tasks WHERE id = ?1", rusqlite::params![local_id])?;
                        tasks_deleted += 1;
                    }
                }
            }
        }

        // Commit transaction - if this fails, all changes are rolled back
        tx.commit()?;

        info!(
            "[{}] Incremental sync complete: +{} tasks, ~{} updated, -{} deleted",
            integration_id, tasks_added, tasks_updated, tasks_deleted
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

    /// Clear all data for an integration (for re-sync)
    pub fn clear_integration_data(&self, integration_id: &str) -> SyncResult<i32> {
        info!("[{}] Clearing all integration data for re-sync", integration_id);
        let repo = Repository::new(&self.db_path)?;
        let deleted = repo.delete_tasks_by_integration(integration_id)?;
        repo.delete_projects_by_integration(integration_id)?;
        repo.delete_labels_by_integration(integration_id)?;
        info!("[{}] Cleared {} tasks", integration_id, deleted);
        Ok(deleted)
    }

    /// Get pending completions to push to provider
    pub fn get_pending_completions(
        &self,
        integration_id: &str,
    ) -> SyncResult<Vec<crate::domain::PendingCompletion>> {
        let repo = Repository::new(&self.db_path)?;
        repo.ensure_pending_completions_table()?;
        let completions = repo.get_pending_completions(integration_id)?;
        if !completions.is_empty() {
            debug!("[{}] Found {} pending completions to sync", integration_id, completions.len());
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
    pub fn update_sync_token(&self, integration_id: &str, sync_token: &str) -> SyncResult<()> {
        let repo = Repository::new(&self.db_path)?;
        repo.update_sync_token(integration_id, sync_token)
    }

    /// Get current sync token for an integration
    pub fn get_sync_token(&self, integration_id: &str) -> SyncResult<Option<String>> {
        let repo = Repository::new(&self.db_path)?;
        repo.get_sync_token(integration_id)
    }
}
