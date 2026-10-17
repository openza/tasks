-- Additive local replica metadata; contains no provider credentials or raw settings.
CREATE TABLE IF NOT EXISTS personal_cloud_workspace_local_migration(id INTEGER PRIMARY KEY);
-- Former source recurrence lived in the local overlay. Move it once, preserving a
-- source descriptor fallback, then let subsequent local overlays remain independent.
UPDATE tasks SET source_metadata=json_set(CASE WHEN json_valid(source_metadata) THEN
 CASE WHEN json_type(source_metadata)='object' THEN source_metadata ELSE json_object('legacySourceMetadata',json(source_metadata)) END
 ELSE CASE WHEN source_metadata IS NULL THEN '{}' ELSE json_object('legacySourceMetadata',source_metadata) END END,
 '$.recurrenceRule',recurrence_rule),recurrence_rule=NULL
 WHERE integration_id='openza_tasks' AND source_integration_id IS NOT NULL AND recurrence_rule IS NOT NULL
 AND NOT EXISTS(SELECT 1 FROM personal_cloud_workspace_local_migration WHERE id=11);
INSERT OR IGNORE INTO personal_cloud_workspace_local_migration(id) VALUES(11);
CREATE TABLE IF NOT EXISTS personal_cloud_workspace_records (
 instance_id TEXT NOT NULL, kind TEXT NOT NULL, entity_id TEXT NOT NULL,
 entity_version INTEGER NOT NULL, canonical_json TEXT NOT NULL,
 PRIMARY KEY(instance_id,kind,entity_id)
);
-- Atomic workspace operations also advance dependent entities. Keep their
-- predicted versions beside the immutable outbox, including ACK-before-echo.
CREATE TABLE IF NOT EXISTS personal_cloud_workspace_indirect_versions (
 instance_id TEXT NOT NULL, kind TEXT NOT NULL, entity_id TEXT NOT NULL,
 mutation_id TEXT NOT NULL, entity_version INTEGER NOT NULL,
 PRIMARY KEY(instance_id,kind,entity_id,mutation_id)
);
CREATE TABLE IF NOT EXISTS personal_cloud_workspace_capabilities (
 instance_id TEXT PRIMARY KEY NOT NULL, workspace INTEGER NOT NULL,
 provider_catalog INTEGER NOT NULL, hydrated INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS personal_cloud_workspace_bootstraps (
 id TEXT PRIMARY KEY NOT NULL, instance_id TEXT NOT NULL, device_id TEXT NOT NULL,
 checkpoint INTEGER, page_count INTEGER NOT NULL DEFAULT 0, record_count INTEGER NOT NULL DEFAULT 0,
 json_bytes INTEGER NOT NULL DEFAULT 0, next_cursor TEXT, complete INTEGER NOT NULL DEFAULT 0,
 last_section INTEGER NOT NULL DEFAULT -1
);
CREATE TABLE IF NOT EXISTS personal_cloud_workspace_bootstrap_pages (
 bootstrap_id TEXT NOT NULL REFERENCES personal_cloud_workspace_bootstraps(id) ON DELETE CASCADE,
 page_number INTEGER NOT NULL, snapshot_json TEXT NOT NULL,
 PRIMARY KEY(bootstrap_id,page_number)
);
CREATE TABLE IF NOT EXISTS personal_cloud_workspace_bootstrap_keys (
 bootstrap_id TEXT NOT NULL REFERENCES personal_cloud_workspace_bootstraps(id) ON DELETE CASCADE,
 section TEXT NOT NULL, entity_id TEXT NOT NULL,
 PRIMARY KEY(bootstrap_id,section,entity_id)
);
CREATE TABLE IF NOT EXISTS personal_cloud_catalog_mappings (
 instance_id TEXT NOT NULL, catalog_id TEXT NOT NULL, source_item_id TEXT NOT NULL,
 PRIMARY KEY(instance_id,catalog_id), UNIQUE(instance_id,source_item_id)
);
CREATE TABLE IF NOT EXISTS personal_cloud_provider_intent_actions (
 instance_id TEXT NOT NULL, intent_id TEXT NOT NULL, canonical_json TEXT NOT NULL,
 phase TEXT NOT NULL, adopted_task_id TEXT, account_binding TEXT NOT NULL,
 source_connection_id TEXT, move_to_project_id TEXT,
 PRIMARY KEY(instance_id,intent_id)
);
CREATE TABLE IF NOT EXISTS personal_cloud_project_choice_mappings (
 instance_id TEXT NOT NULL, choice_id TEXT NOT NULL, integration_id TEXT NOT NULL,
 source_connection_id TEXT NOT NULL, provider_project_id TEXT NOT NULL,
 PRIMARY KEY(instance_id,choice_id)
);
CREATE TABLE IF NOT EXISTS personal_cloud_provider_scope_mappings (
 instance_id TEXT NOT NULL, scope_id TEXT NOT NULL, integration_id TEXT NOT NULL,
 source_connection_id TEXT NOT NULL,
 PRIMARY KEY(instance_id,scope_id), UNIQUE(instance_id,source_connection_id)
);
CREATE TABLE IF NOT EXISTS personal_cloud_route_mappings (
 instance_id TEXT NOT NULL, route_id TEXT NOT NULL, scope_id TEXT NOT NULL, local_rule_id TEXT NOT NULL, last_requested_json TEXT,
 PRIMARY KEY(instance_id,route_id), UNIQUE(instance_id,scope_id,local_rule_id)
);
