CREATE TABLE IF NOT EXISTS personal_cloud_initial_sync (
    instance_id TEXT NOT NULL,
    device_id TEXT NOT NULL,
    seed_end_sequence INTEGER,
    phase TEXT NOT NULL DEFAULT 'Preparing',
    error_code TEXT,
    report_sequence INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (instance_id, device_id)
);

-- Local observation fencing does not pretend that unchanged facts were uploaded.
CREATE TABLE IF NOT EXISTS personal_cloud_source_observations (
    instance_id TEXT NOT NULL,
    task_id TEXT NOT NULL,
    integration_kind TEXT NOT NULL,
    external_id_hash TEXT NOT NULL,
    observed_at INTEGER NOT NULL,
    PRIMARY KEY (instance_id, task_id)
);
CREATE INDEX IF NOT EXISTS idx_personal_cloud_outbox_source_history
    ON personal_cloud_outbox(instance_id, entity_id, operation, local_sequence DESC);
