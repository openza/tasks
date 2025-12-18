use thiserror::Error;

#[derive(Error, Debug)]
pub enum SyncError {
    #[error("Database error: {0}")]
    Database(#[from] rusqlite::Error),

    #[error("JSON serialization error: {0}")]
    Serialization(#[from] serde_json::Error),

    #[error("Invalid sync state: {0}")]
    InvalidState(String),

    #[error("Task not found: {0}")]
    TaskNotFound(String),

    #[error("Provider not supported: {0}")]
    UnsupportedProvider(String),
}

pub type SyncResult<T> = Result<T, SyncError>;
