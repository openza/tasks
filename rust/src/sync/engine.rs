use std::collections::{HashMap, HashSet};

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
    pub fn initial_sync(
        &self,
        provider: &str,
        remote_tasks: Vec<Task>,
        remote_projects: Vec<Project>,
        remote_labels: Vec<Label>,
    ) -> SyncResult<SyncSummary> {
        let repo = Repository::new(&self.db_path)?;

        // Ensure required tables/rows exist
        repo.ensure_pending_completions_table()?;
        repo.ensure_integration_exists(provider)?;

        // Clear existing data for this provider
        let tasks_deleted = repo.delete_tasks_by_provider(provider)?;
        repo.delete_projects_by_provider(provider)?;
        repo.delete_labels_by_provider(provider)?;

        // Insert projects first (foreign key dependency)
        for project in &remote_projects {
            repo.insert_project(project)?;
        }

        // Insert labels
        for label in &remote_labels {
            repo.insert_label(label)?;
        }

        // Build set of valid project IDs
        let valid_project_ids: HashSet<String> =
            remote_projects.iter().map(|p| p.id.clone()).collect();

        // Sort tasks for proper insertion order and validate references
        let sorted_tasks = Self::prepare_tasks_for_insert(remote_tasks, &valid_project_ids);

        // Insert tasks in sorted order
        for task in &sorted_tasks {
            repo.insert_task(task)?;
        }

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
    pub fn incremental_sync(
        &self,
        provider: &str,
        remote_tasks: Vec<Task>,
        remote_projects: Vec<Project>,
        remote_labels: Vec<Label>,
        sync_token: Option<String>,
    ) -> SyncResult<SyncSummary> {
        let repo = Repository::new(&self.db_path)?;

        // Ensure required tables/rows exist
        repo.ensure_pending_completions_table()?;
        repo.ensure_integration_exists(provider)?;

        // Sync projects first (upsert)
        for project in &remote_projects {
            repo.insert_project(project)?;
        }

        // Sync labels
        for label in &remote_labels {
            repo.insert_label(label)?;
        }

        // Build set of valid project IDs
        let valid_project_ids: HashSet<String> =
            remote_projects.iter().map(|p| p.id.clone()).collect();

        // Prepare tasks (sort and validate references)
        let sorted_tasks = Self::prepare_tasks_for_insert(remote_tasks, &valid_project_ids);

        // Get existing local tasks for this provider
        let local_tasks = repo.get_tasks_by_provider(provider)?;
        let local_task_map: HashMap<String, Task> =
            local_tasks.into_iter().map(|t| (t.id.clone(), t)).collect();

        let mut tasks_added = 0;
        let mut tasks_updated = 0;
        let mut tasks_deleted = 0;

        // Build set of remote task IDs for deletion detection
        let remote_task_ids: HashSet<String> =
            sorted_tasks.iter().map(|t| t.id.clone()).collect();

        // Process remote tasks in sorted order
        for remote_task in sorted_tasks {
            if local_task_map.contains_key(&remote_task.id) {
                // Task exists locally - update with remote data (remote wins)
                // But preserve local-only fields (handled in repository)
                repo.update_task(&remote_task)?;
                tasks_updated += 1;
            } else {
                // New task from remote
                repo.insert_task(&remote_task)?;
                tasks_added += 1;
            }
        }

        // Detect deleted tasks (in local but not in remote)
        // Only for full sync (when sync_token indicates full refresh)
        if sync_token.as_deref() == Some("*") || sync_token.is_none() {
            for (local_id, _) in &local_task_map {
                if !remote_task_ids.contains(local_id) {
                    repo.delete_task(local_id)?;
                    tasks_deleted += 1;
                }
            }
        }

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
        let repo = Repository::new(&self.db_path)?;
        let deleted = repo.delete_tasks_by_provider(provider)?;
        repo.delete_projects_by_provider(provider)?;
        repo.delete_labels_by_provider(provider)?;
        Ok(deleted)
    }

    /// Get pending completions to push to provider
    pub fn get_pending_completions(
        &self,
        provider: &str,
    ) -> SyncResult<Vec<crate::domain::PendingCompletion>> {
        let repo = Repository::new(&self.db_path)?;
        repo.ensure_pending_completions_table()?;
        repo.get_pending_completions(provider)
    }

    /// Mark a completion as synced (remove from queue)
    pub fn mark_completion_synced(&self, completion_id: &str) -> SyncResult<()> {
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
