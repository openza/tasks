use crate::domain::{Label, PendingCompletion, Project, SyncSummary, Task};
use crate::sync::SyncEngine;

/// Initialize sync engine and perform initial sync (clear + insert)
/// Called when user first connects a provider or requests re-sync
pub fn initial_sync(
    db_path: String,
    provider: String,
    remote_tasks_json: String,
    remote_projects_json: String,
    remote_labels_json: String,
) -> String {
    let result = (|| -> Result<SyncSummary, String> {
        let remote_tasks: Vec<Task> =
            serde_json::from_str(&remote_tasks_json).map_err(|e| e.to_string())?;
        let remote_projects: Vec<Project> =
            serde_json::from_str(&remote_projects_json).map_err(|e| e.to_string())?;
        let remote_labels: Vec<Label> =
            serde_json::from_str(&remote_labels_json).map_err(|e| e.to_string())?;

        let engine = SyncEngine::new(db_path);
        engine
            .initial_sync(&provider, remote_tasks, remote_projects, remote_labels)
            .map_err(|e| e.to_string())
    })();

    match result {
        Ok(summary) => serde_json::to_string(&summary).unwrap_or_else(|e| {
            serde_json::to_string(&SyncSummary {
                success: false,
                error: Some(e.to_string()),
                ..Default::default()
            })
            .unwrap()
        }),
        Err(e) => serde_json::to_string(&SyncSummary {
            success: false,
            error: Some(e),
            ..Default::default()
        })
        .unwrap(),
    }
}

/// Perform incremental sync with remote data
/// Uses "remote wins" strategy for conflicts
///
/// When delete_orphans is false, tasks missing from remote are NOT deleted locally.
/// This is used for sources like Obsidian where the app owns task existence.
pub fn incremental_sync(
    db_path: String,
    provider: String,
    remote_tasks_json: String,
    remote_projects_json: String,
    remote_labels_json: String,
    sync_token: Option<String>,
    delete_orphans: bool,
) -> String {
    let result = (|| -> Result<SyncSummary, String> {
        let remote_tasks: Vec<Task> =
            serde_json::from_str(&remote_tasks_json).map_err(|e| e.to_string())?;
        let remote_projects: Vec<Project> =
            serde_json::from_str(&remote_projects_json).map_err(|e| e.to_string())?;
        let remote_labels: Vec<Label> =
            serde_json::from_str(&remote_labels_json).map_err(|e| e.to_string())?;

        let engine = SyncEngine::new(db_path);
        engine
            .incremental_sync(
                &provider,
                remote_tasks,
                remote_projects,
                remote_labels,
                sync_token,
                delete_orphans,
            )
            .map_err(|e| e.to_string())
    })();

    match result {
        Ok(summary) => serde_json::to_string(&summary).unwrap_or_else(|e| {
            serde_json::to_string(&SyncSummary {
                success: false,
                error: Some(e.to_string()),
                ..Default::default()
            })
            .unwrap()
        }),
        Err(e) => serde_json::to_string(&SyncSummary {
            success: false,
            error: Some(e),
            ..Default::default()
        })
        .unwrap(),
    }
}

/// Clear all data for an integration (for re-sync)
pub fn clear_provider_data(db_path: String, provider: String) -> String {
    let engine = SyncEngine::new(db_path);
    match engine.clear_integration_data(&provider) {
        Ok(deleted) => serde_json::to_string(&serde_json::json!({
            "success": true,
            "deleted": deleted
        }))
        .unwrap(),
        Err(e) => serde_json::to_string(&serde_json::json!({
            "success": false,
            "error": e.to_string()
        }))
        .unwrap(),
    }
}

/// Get pending completions to push to provider
pub fn get_pending_completions(db_path: String, provider: String) -> String {
    let engine = SyncEngine::new(db_path);
    match engine.get_pending_completions(&provider) {
        Ok(completions) => serde_json::to_string(&completions).unwrap_or_else(|_| "[]".to_string()),
        Err(_) => "[]".to_string(),
    }
}

/// Mark a completion as synced (remove from queue)
pub fn mark_completion_synced(db_path: String, completion_id: String) -> bool {
    let engine = SyncEngine::new(db_path);
    engine.mark_completion_synced(&completion_id).is_ok()
}

/// Queue a task completion for sync
pub fn queue_completion(
    db_path: String,
    task_id: String,
    provider: String,
    provider_task_id: String,
    completed: bool,
) -> bool {
    use chrono::Utc;
    use uuid::Uuid;

    let completion = PendingCompletion {
        id: Uuid::new_v4().to_string(),
        task_id,
        provider: provider.clone(),
        provider_task_id,
        completed,
        completed_at: if completed { Some(Utc::now()) } else { None },
        created_at: Utc::now(),
        retry_count: 0,
    };

    // First ensure the table exists
    if let Ok(repo) = crate::db::Repository::new(&db_path) {
        let _ = repo.ensure_pending_completions_table();
        let _ = repo.queue_completion(&completion);
        true
    } else {
        false
    }
}

/// Update sync token after successful sync
pub fn update_sync_token(db_path: String, provider: String, sync_token: String) -> bool {
    let engine = SyncEngine::new(db_path);
    engine.update_sync_token(&provider, &sync_token).is_ok()
}

/// Get current sync token for a provider
pub fn get_sync_token(db_path: String, provider: String) -> Option<String> {
    let engine = SyncEngine::new(db_path);
    engine.get_sync_token(&provider).ok().flatten()
}
