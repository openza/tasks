//! FFI exports for Dart interop
//!
//! This module provides C-compatible function exports that can be called
//! from Dart via FFI. All functions handle string conversion and memory
//! management for cross-language communication.

use std::ffi::{CStr, CString};
use std::os::raw::c_char;

use crate::api;

/// Helper to convert C string to Rust String
///
/// # Safety
/// The pointer must be a valid null-terminated C string or null
fn c_str_to_string(ptr: *const c_char) -> String {
    if ptr.is_null() {
        return String::new();
    }
    // SAFETY: We've checked ptr is not null, and caller guarantees it's a valid C string
    unsafe { CStr::from_ptr(ptr).to_string_lossy().into_owned() }
}

/// Helper to convert Rust String to C string (caller must free)
fn string_to_c_str(s: String) -> *mut c_char {
    CString::new(s).unwrap_or_default().into_raw()
}

/// Free a string allocated by Rust
///
/// # Safety
/// The pointer must have been allocated by Rust via CString::into_raw()
#[unsafe(no_mangle)]
pub unsafe extern "C" fn free_rust_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        // SAFETY: Caller guarantees ptr was allocated by CString::into_raw()
        unsafe {
            drop(CString::from_raw(ptr));
        }
    }
}

/// Perform initial sync (clear existing data and insert fresh)
///
/// # Safety
/// All string pointers must be valid null-terminated C strings
#[unsafe(no_mangle)]
pub unsafe extern "C" fn initial_sync(
    db_path: *const c_char,
    provider: *const c_char,
    tasks_json: *const c_char,
    projects_json: *const c_char,
    labels_json: *const c_char,
) -> *mut c_char {
    let db_path = c_str_to_string(db_path);
    let provider = c_str_to_string(provider);
    let tasks_json = c_str_to_string(tasks_json);
    let projects_json = c_str_to_string(projects_json);
    let labels_json = c_str_to_string(labels_json);

    // Debug: log received string lengths
    eprintln!("[FFI] initial_sync received - tasks: {} chars, projects: {} chars, labels: {} chars",
        tasks_json.len(), projects_json.len(), labels_json.len());

    // Debug: show context around position 2726 where parsing fails
    if tasks_json.len() > 2750 {
        let start = 2700.min(tasks_json.len());
        let end = 2760.min(tasks_json.len());
        eprintln!("[FFI] tasks_json[2700..2760]: {:?}", &tasks_json[start..end]);
    }

    let result = api::initial_sync(db_path, provider, tasks_json, projects_json, labels_json);
    string_to_c_str(result)
}

/// Perform incremental sync with remote data
///
/// # Safety
/// All string pointers must be valid null-terminated C strings
#[unsafe(no_mangle)]
pub unsafe extern "C" fn incremental_sync(
    db_path: *const c_char,
    provider: *const c_char,
    tasks_json: *const c_char,
    projects_json: *const c_char,
    labels_json: *const c_char,
    sync_token: *const c_char,
) -> *mut c_char {
    let db_path = c_str_to_string(db_path);
    let provider = c_str_to_string(provider);
    let tasks_json = c_str_to_string(tasks_json);
    let projects_json = c_str_to_string(projects_json);
    let labels_json = c_str_to_string(labels_json);
    let sync_token_str = c_str_to_string(sync_token);
    let sync_token = if sync_token_str.is_empty() {
        None
    } else {
        Some(sync_token_str)
    };

    let result = api::incremental_sync(
        db_path,
        provider,
        tasks_json,
        projects_json,
        labels_json,
        sync_token,
    );
    string_to_c_str(result)
}

/// Clear all data for a provider
///
/// # Safety
/// All string pointers must be valid null-terminated C strings
#[unsafe(no_mangle)]
pub unsafe extern "C" fn clear_provider_data(
    db_path: *const c_char,
    provider: *const c_char,
) -> *mut c_char {
    let db_path = c_str_to_string(db_path);
    let provider = c_str_to_string(provider);

    let result = api::clear_provider_data(db_path, provider);
    string_to_c_str(result)
}

/// Get pending completions as JSON array
///
/// # Safety
/// All string pointers must be valid null-terminated C strings
#[unsafe(no_mangle)]
pub unsafe extern "C" fn get_pending_completions(
    db_path: *const c_char,
    provider: *const c_char,
) -> *mut c_char {
    let db_path = c_str_to_string(db_path);
    let provider = c_str_to_string(provider);

    let result = api::get_pending_completions(db_path, provider);
    string_to_c_str(result)
}

/// Mark a completion as synced
///
/// # Safety
/// All string pointers must be valid null-terminated C strings
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mark_completion_synced(
    db_path: *const c_char,
    completion_id: *const c_char,
) -> bool {
    let db_path = c_str_to_string(db_path);
    let completion_id = c_str_to_string(completion_id);

    api::mark_completion_synced(db_path, completion_id)
}

/// Queue a task completion for sync
///
/// # Safety
/// All string pointers must be valid null-terminated C strings
#[unsafe(no_mangle)]
pub unsafe extern "C" fn queue_completion(
    db_path: *const c_char,
    task_id: *const c_char,
    provider: *const c_char,
    provider_task_id: *const c_char,
    completed: bool,
) -> bool {
    let db_path = c_str_to_string(db_path);
    let task_id = c_str_to_string(task_id);
    let provider = c_str_to_string(provider);
    let provider_task_id = c_str_to_string(provider_task_id);

    api::queue_completion(db_path, task_id, provider, provider_task_id, completed)
}

/// Update sync token for a provider
///
/// # Safety
/// All string pointers must be valid null-terminated C strings
#[unsafe(no_mangle)]
pub unsafe extern "C" fn update_sync_token(
    db_path: *const c_char,
    provider: *const c_char,
    sync_token: *const c_char,
) -> bool {
    let db_path = c_str_to_string(db_path);
    let provider = c_str_to_string(provider);
    let sync_token = c_str_to_string(sync_token);

    api::update_sync_token(db_path, provider, sync_token)
}

/// Get sync token for a provider (returns null if not found)
///
/// # Safety
/// All string pointers must be valid null-terminated C strings
/// Caller must free the returned string using free_rust_string
#[unsafe(no_mangle)]
pub unsafe extern "C" fn get_sync_token(
    db_path: *const c_char,
    provider: *const c_char,
) -> *mut c_char {
    let db_path = c_str_to_string(db_path);
    let provider = c_str_to_string(provider);

    match api::get_sync_token(db_path, provider) {
        Some(token) => string_to_c_str(token),
        None => std::ptr::null_mut(),
    }
}
