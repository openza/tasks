ALTER TABLE personal_cloud_outbox ADD COLUMN server_change_seq INTEGER;

CREATE TABLE IF NOT EXISTS provider_completion_confirmations (
  confirmation_id TEXT PRIMARY KEY NOT NULL,
  instance_id TEXT,
  task_id TEXT NOT NULL,
  requested_change_seq INTEGER,
  confirmed_at INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_provider_completion_confirmations_current
  ON provider_completion_confirmations(instance_id, task_id, requested_change_seq);

CREATE TABLE IF NOT EXISTS personal_cloud_provider_observed_reopens (
  instance_id TEXT NOT NULL,
  task_id TEXT NOT NULL,
  observation_id TEXT NOT NULL,
  observed_at INTEGER NOT NULL,
  blocked_artifact_version TEXT,
  PRIMARY KEY(instance_id, observation_id)
);

CREATE INDEX IF NOT EXISTS ix_provider_observed_reopens_pending
  ON personal_cloud_provider_observed_reopens(instance_id, blocked_artifact_version, observed_at);
