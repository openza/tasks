use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

/// Task model matching the SQLite schema
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(default)]
pub struct Task {
    pub id: String,
    #[serde(default)]
    pub external_id: Option<String>,
    #[serde(default = "default_integration_id")]
    pub integration_id: String,
    pub title: String,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub project_id: Option<String>,
    #[serde(default)]
    pub parent_id: Option<String>,
    #[serde(default = "default_priority")]
    pub priority: i32,
    #[serde(default = "default_status")]
    pub status: String,
    #[serde(default)]
    pub due_date: Option<DateTime<Utc>>,
    #[serde(default)]
    pub due_time: Option<String>,
    #[serde(default)]
    pub notes: Option<String>,

    // Provider-specific data (stored as JSON TEXT in SQLite)
    #[serde(default)]
    pub provider_metadata: Option<serde_json::Value>,

    // Label associations (synced via task_labels junction table)
    #[serde(default)]
    pub labels: Vec<String>,

    #[serde(default = "Utc::now")]
    pub created_at: DateTime<Utc>,
    #[serde(default)]
    pub updated_at: Option<DateTime<Utc>>,
    #[serde(default)]
    pub completed_at: Option<DateTime<Utc>>,
}

fn default_priority() -> i32 { 2 }
fn default_status() -> String { "pending".to_string() }
fn default_integration_id() -> String { "openza_tasks".to_string() }

/// Project model matching the SQLite schema
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(default)]
pub struct Project {
    pub id: String,
    #[serde(default)]
    pub external_id: Option<String>,
    #[serde(default = "default_integration_id")]
    pub integration_id: String,
    pub name: String,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default = "default_color")]
    pub color: String,
    #[serde(default)]
    pub icon: Option<String>,
    #[serde(default)]
    pub parent_id: Option<String>,
    #[serde(default, alias = "order")]
    pub sort_order: i32,
    #[serde(default)]
    pub is_favorite: bool,
    #[serde(default)]
    pub is_archived: bool,
    #[serde(default)]
    pub provider_metadata: Option<serde_json::Value>,
    #[serde(default = "Utc::now")]
    pub created_at: DateTime<Utc>,
    #[serde(default)]
    pub updated_at: Option<DateTime<Utc>>,
}

fn default_color() -> String { "#808080".to_string() }

/// Label model matching the SQLite schema
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(default)]
pub struct Label {
    pub id: String,
    #[serde(default)]
    pub external_id: Option<String>,
    #[serde(default = "default_integration_id")]
    pub integration_id: String,
    pub name: String,
    #[serde(default = "default_color")]
    pub color: String,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default, alias = "order")]
    pub sort_order: i32,
    #[serde(default)]
    pub is_favorite: bool,
    #[serde(default)]
    pub provider_metadata: Option<serde_json::Value>,
    #[serde(default = "Utc::now")]
    pub created_at: DateTime<Utc>,
}

/// Pending completion for offline queue
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PendingCompletion {
    pub id: String,
    pub task_id: String,
    pub provider: String,
    pub provider_task_id: String,
    pub completed: bool,
    pub completed_at: Option<DateTime<Utc>>,
    pub created_at: DateTime<Utc>,
    pub retry_count: i32,
}

/// Sync result summary
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct SyncSummary {
    pub tasks_added: i32,
    pub tasks_updated: i32,
    pub tasks_deleted: i32,
    pub projects_synced: i32,
    pub labels_synced: i32,
    pub success: bool,
    pub error: Option<String>,
    pub new_sync_token: Option<String>,
}
