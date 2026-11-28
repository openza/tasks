CREATE TABLE IF NOT EXISTS personal_cloud_provider_command_cancellations (
  instance_id TEXT NOT NULL,
  device_id TEXT NOT NULL,
  task_id TEXT NOT NULL,
  requested_seq INTEGER NOT NULL CHECK(requested_seq > 0),
  reason TEXT NOT NULL CHECK(reason IN ('missing_source','cancelled_attempt','initial_seed')),
  mutation_id TEXT,
  blocked_artifact_version TEXT,
  created_at INTEGER NOT NULL,
  PRIMARY KEY(instance_id,task_id,requested_seq),
  UNIQUE(instance_id,mutation_id)
);

CREATE INDEX IF NOT EXISTS ix_provider_command_cancellations_pending
  ON personal_cloud_provider_command_cancellations(instance_id,device_id,mutation_id,blocked_artifact_version);

ALTER TABLE personal_cloud_outbox ADD COLUMN blocked_artifact_version TEXT;
