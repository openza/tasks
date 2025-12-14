use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

/// Task model matching the SQLite schema
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(default)]
pub struct Task {
    pub id: String,
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

    // Enhanced local-only fields
    #[serde(default)]
    pub estimated_duration: Option<i32>,
    #[serde(default)]
    pub actual_duration: Option<i32>,
    #[serde(default = "default_energy_level")]
    pub energy_level: i32,
    #[serde(default = "default_context")]
    pub context: String,
    #[serde(default)]
    pub focus_time: bool,
    #[serde(default)]
    pub notes: Option<String>,

    // Integration fields (stored as JSON TEXT in SQLite)
    #[serde(default)]
    pub source_task: Option<serde_json::Value>,
    #[serde(default)]
    pub integrations: Option<serde_json::Value>,

    #[serde(default = "Utc::now")]
    pub created_at: DateTime<Utc>,
    #[serde(default)]
    pub updated_at: Option<DateTime<Utc>>,
    #[serde(default)]
    pub completed_at: Option<DateTime<Utc>>,
}

fn default_priority() -> i32 { 2 }
fn default_status() -> String { "pending".to_string() }
fn default_energy_level() -> i32 { 2 }
fn default_context() -> String { "work".to_string() }

/// Project model matching the SQLite schema
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(default)]
pub struct Project {
    pub id: String,
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
    pub integrations: Option<serde_json::Value>,
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
    pub integrations: Option<serde_json::Value>,
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
