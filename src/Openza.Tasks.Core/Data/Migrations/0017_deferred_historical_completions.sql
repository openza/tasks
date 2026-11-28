CREATE TABLE IF NOT EXISTS personal_cloud_deferred_historical_completions (
  instance_id TEXT NOT NULL,
  device_id TEXT NOT NULL,
  mutation_id TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  canonical_json TEXT NOT NULL,
  canonical_hash TEXT NOT NULL CHECK(length(canonical_hash)=64),
  created_at INTEGER NOT NULL,
  blocked_artifact_version TEXT,
  PRIMARY KEY(instance_id,device_id,mutation_id)
);

CREATE INDEX IF NOT EXISTS ix_deferred_historical_completions_pending
  ON personal_cloud_deferred_historical_completions(instance_id,device_id,blocked_artifact_version,created_at);
