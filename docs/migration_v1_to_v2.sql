-- ============================================================
-- Openza Tasks Database Migration: Schema v1 to v2
-- ============================================================
--
-- This script migrates the database from schema v1 to v2.
--
-- Key Changes:
-- 1. Integrations table: Added display_name, color, icon, logo_path, is_configured
-- 2. Tasks/Projects/Labels: Added external_id, integration_id FK, renamed integrations->provider_metadata
-- 3. Tasks: Removed estimated_duration, actual_duration, energy_level, context, focus_time, source_task
-- 4. Labels: Added is_favorite
--
-- Database location change:
-- - OLD: ~/Documents/openza.db
-- - NEW: ~/.local/share/com.openza.tasks/openza_tasks.db (Linux)
--        ~/Library/Application Support/com.openza.tasks/openza_tasks.db (macOS)
--        %APPDATA%\com.openza.tasks\openza_tasks.db (Windows)
--
-- INSTRUCTIONS:
-- 1. Backup your current database first!
-- 2. Run this script against your existing openza.db
-- 3. After running, copy the database to the new location
-- 4. Rename to openza_tasks.db
--
-- ============================================================

-- Enable foreign keys
PRAGMA foreign_keys = OFF;

-- ============================================================
-- STEP 1: Update integrations table
-- ============================================================

-- Add new columns to integrations table
ALTER TABLE integrations ADD COLUMN display_name TEXT;
ALTER TABLE integrations ADD COLUMN color TEXT DEFAULT '#808080';
ALTER TABLE integrations ADD COLUMN icon TEXT;
ALTER TABLE integrations ADD COLUMN logo_path TEXT;
ALTER TABLE integrations ADD COLUMN is_configured INTEGER DEFAULT 0;

-- Insert default integrations
INSERT OR IGNORE INTO integrations (id, name, display_name, color, icon, is_active, is_configured, created_at)
VALUES
  ('openza_tasks', 'openza_tasks', 'Openza Tasks', '#6366f1', 'database', 1, 1, strftime('%s', 'now')),
  ('todoist', 'todoist', 'Todoist', '#E44332', 'check-circle', 0, 0, strftime('%s', 'now')),
  ('msToDo', 'msToDo', 'Microsoft To-Do', '#00A4EF', 'layout-grid', 0, 0, strftime('%s', 'now'));

-- Update existing todoist integration with new columns
UPDATE integrations SET
  display_name = 'Todoist',
  color = '#E44332',
  icon = 'check-circle',
  logo_path = 'assets/logos/todoist.svg',
  is_configured = 1
WHERE id = 'todoist' AND display_name IS NULL;

-- Update existing msToDo integration with new columns
UPDATE integrations SET
  display_name = 'Microsoft To-Do',
  color = '#00A4EF',
  icon = 'layout-grid',
  logo_path = 'assets/logos/microsoft.svg',
  is_configured = 1
WHERE id = 'msToDo' AND display_name IS NULL;

-- ============================================================
-- STEP 2: Migrate tasks table
-- ============================================================

-- Add new columns
ALTER TABLE tasks ADD COLUMN external_id TEXT;
ALTER TABLE tasks ADD COLUMN integration_id TEXT DEFAULT 'openza_tasks';
ALTER TABLE tasks ADD COLUMN provider_metadata TEXT;

-- Copy existing integrations JSON to provider_metadata
UPDATE tasks SET provider_metadata = integrations WHERE integrations IS NOT NULL;

-- Migrate Todoist tasks: extract external_id and set integration_id
UPDATE tasks SET
  external_id = SUBSTR(id, 9),
  integration_id = 'todoist'
WHERE id LIKE 'todoist_%';

-- Migrate MS To-Do tasks: extract external_id and set integration_id
UPDATE tasks SET
  external_id = SUBSTR(id, 8),
  integration_id = 'msToDo'
WHERE id LIKE 'mstodo_%';

-- Set integration_id for local tasks (ones without prefix)
UPDATE tasks SET
  integration_id = 'openza_tasks'
WHERE integration_id IS NULL OR integration_id = 'openza_tasks';

-- ============================================================
-- STEP 3: Migrate projects table
-- ============================================================

-- Add new columns
ALTER TABLE projects ADD COLUMN external_id TEXT;
ALTER TABLE projects ADD COLUMN integration_id TEXT DEFAULT 'openza_tasks';
ALTER TABLE projects ADD COLUMN provider_metadata TEXT;

-- Copy existing integrations JSON to provider_metadata
UPDATE projects SET provider_metadata = integrations WHERE integrations IS NOT NULL;

-- Migrate Todoist projects
UPDATE projects SET
  external_id = SUBSTR(id, 9),
  integration_id = 'todoist'
WHERE id LIKE 'todoist_%';

-- Migrate MS To-Do projects
UPDATE projects SET
  external_id = SUBSTR(id, 8),
  integration_id = 'msToDo'
WHERE id LIKE 'mstodo_%';

-- Set integration_id for local projects
UPDATE projects SET
  integration_id = 'openza_tasks'
WHERE integration_id IS NULL OR integration_id = 'openza_tasks';

-- ============================================================
-- STEP 4: Migrate labels table
-- ============================================================

-- Add new columns
ALTER TABLE labels ADD COLUMN external_id TEXT;
ALTER TABLE labels ADD COLUMN integration_id TEXT DEFAULT 'openza_tasks';
ALTER TABLE labels ADD COLUMN is_favorite INTEGER DEFAULT 0;
ALTER TABLE labels ADD COLUMN provider_metadata TEXT;

-- Copy existing integrations JSON to provider_metadata
UPDATE labels SET provider_metadata = integrations WHERE integrations IS NOT NULL;

-- Migrate Todoist labels
UPDATE labels SET
  external_id = SUBSTR(id, 15),
  integration_id = 'todoist'
WHERE id LIKE 'todoist_label_%';

-- Set integration_id for local labels
UPDATE labels SET
  integration_id = 'openza_tasks'
WHERE integration_id IS NULL OR integration_id = 'openza_tasks';

-- ============================================================
-- STEP 5: Recreate tables to remove deprecated columns
-- (SQLite doesn't support DROP COLUMN in older versions)
-- ============================================================

-- Create new tasks table without deprecated columns
CREATE TABLE IF NOT EXISTS tasks_new (
  id TEXT PRIMARY KEY NOT NULL,
  external_id TEXT,
  integration_id TEXT NOT NULL DEFAULT 'openza_tasks' REFERENCES integrations(id),
  title TEXT NOT NULL,
  description TEXT,
  project_id TEXT REFERENCES projects(id),
  parent_id TEXT,
  priority INTEGER NOT NULL DEFAULT 2,
  status TEXT NOT NULL DEFAULT 'pending',
  due_date INTEGER,
  due_time TEXT,
  notes TEXT,
  provider_metadata TEXT,
  created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
  updated_at INTEGER,
  completed_at INTEGER
);

-- Copy data from old table to new table
INSERT INTO tasks_new (
  id, external_id, integration_id, title, description, project_id, parent_id,
  priority, status, due_date, due_time, notes, provider_metadata,
  created_at, updated_at, completed_at
)
SELECT
  id, external_id, COALESCE(integration_id, 'openza_tasks'), title, description, project_id, parent_id,
  priority, status, due_date, due_time, notes, provider_metadata,
  created_at, updated_at, completed_at
FROM tasks;

-- Drop old table and rename new table
DROP TABLE tasks;
ALTER TABLE tasks_new RENAME TO tasks;

-- Create new projects table with new schema
CREATE TABLE IF NOT EXISTS projects_new (
  id TEXT PRIMARY KEY NOT NULL,
  external_id TEXT,
  integration_id TEXT NOT NULL DEFAULT 'openza_tasks' REFERENCES integrations(id),
  name TEXT NOT NULL,
  description TEXT,
  color TEXT,
  icon TEXT,
  parent_id TEXT,
  sort_order INTEGER NOT NULL DEFAULT 0,
  is_favorite INTEGER NOT NULL DEFAULT 0,
  is_archived INTEGER NOT NULL DEFAULT 0,
  provider_metadata TEXT,
  created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
  updated_at INTEGER
);

-- Copy data
INSERT INTO projects_new (
  id, external_id, integration_id, name, description, color, icon, parent_id,
  sort_order, is_favorite, is_archived, provider_metadata, created_at, updated_at
)
SELECT
  id, external_id, COALESCE(integration_id, 'openza_tasks'), name, description, color, icon, parent_id,
  sort_order, is_favorite, is_archived, provider_metadata, created_at, updated_at
FROM projects;

-- Drop old table and rename
DROP TABLE projects;
ALTER TABLE projects_new RENAME TO projects;

-- Create new labels table with new schema
CREATE TABLE IF NOT EXISTS labels_new (
  id TEXT PRIMARY KEY NOT NULL,
  external_id TEXT,
  integration_id TEXT NOT NULL DEFAULT 'openza_tasks' REFERENCES integrations(id),
  name TEXT NOT NULL,
  color TEXT,
  description TEXT,
  sort_order INTEGER NOT NULL DEFAULT 0,
  is_favorite INTEGER NOT NULL DEFAULT 0,
  provider_metadata TEXT,
  created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now'))
);

-- Copy data
INSERT INTO labels_new (
  id, external_id, integration_id, name, color, description, sort_order, is_favorite,
  provider_metadata, created_at
)
SELECT
  id, external_id, COALESCE(integration_id, 'openza_tasks'), name, color, description, sort_order,
  COALESCE(is_favorite, 0), provider_metadata, created_at
FROM labels;

-- Drop old table and rename
DROP TABLE labels;
ALTER TABLE labels_new RENAME TO labels;

-- ============================================================
-- STEP 6: Create indexes
-- ============================================================

CREATE INDEX IF NOT EXISTS idx_tasks_integration_id ON tasks(integration_id);
CREATE INDEX IF NOT EXISTS idx_tasks_project_id ON tasks(project_id);
CREATE INDEX IF NOT EXISTS idx_tasks_status ON tasks(status);
CREATE INDEX IF NOT EXISTS idx_tasks_due_date ON tasks(due_date);
CREATE INDEX IF NOT EXISTS idx_projects_integration_id ON projects(integration_id);
CREATE INDEX IF NOT EXISTS idx_labels_integration_id ON labels(integration_id);

-- Re-enable foreign keys
PRAGMA foreign_keys = ON;

-- ============================================================
-- VERIFICATION
-- ============================================================

-- Run these queries to verify the migration:
-- SELECT COUNT(*) as task_count FROM tasks;
-- SELECT integration_id, COUNT(*) FROM tasks GROUP BY integration_id;
-- SELECT * FROM integrations;

-- ============================================================
-- POST-MIGRATION
-- ============================================================
--
-- After running this script:
-- 1. Copy the database to the new location:
--    - Linux: ~/.local/share/com.openza.tasks/openza_tasks.db
--    - macOS: ~/Library/Application Support/com.openza.tasks/openza_tasks.db
--    - Windows: %APPDATA%\com.openza.tasks\openza_tasks.db
--
-- 2. The old ~/Documents/openza.db can be kept as a backup or deleted
--
-- ============================================================
