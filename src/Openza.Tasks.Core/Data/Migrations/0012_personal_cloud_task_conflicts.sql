CREATE TABLE IF NOT EXISTS personal_cloud_task_conflicts (
    mutation_id TEXT PRIMARY KEY,
    instance_id TEXT NOT NULL,
    device_id TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    device_sequence INTEGER NOT NULL,
    canonical_json TEXT NOT NULL,
    base_record_json TEXT
);
CREATE INDEX IF NOT EXISTS ix_personal_cloud_task_conflicts_entity
    ON personal_cloud_task_conflicts(instance_id, entity_id);
