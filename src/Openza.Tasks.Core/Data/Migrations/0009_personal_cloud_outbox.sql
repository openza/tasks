-- Execute through the store compatibility migration, with foreign keys disabled
-- and a transaction. Preserve durable mutation history after physical task deletion.
CREATE TABLE personal_cloud_outbox_v9 (
  local_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
  mutation_id TEXT NOT NULL UNIQUE,
  product TEXT NOT NULL,
  instance_id TEXT NOT NULL,
  device_id TEXT NOT NULL,
  device_sequence INTEGER NOT NULL CHECK (device_sequence > 0),
  operation TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  canonical_json TEXT NOT NULL,
  canonical_hash TEXT NOT NULL CHECK (length(canonical_hash) = 64),
  created_at INTEGER NOT NULL,
  acknowledged_at INTEGER,
  UNIQUE (instance_id, device_id, device_sequence)
);
INSERT INTO personal_cloud_outbox_v9
SELECT local_sequence, mutation_id, product, instance_id, device_id, device_sequence,
       operation, entity_id, canonical_json, canonical_hash, created_at, acknowledged_at
FROM personal_cloud_outbox;
DROP TABLE personal_cloud_outbox;
ALTER TABLE personal_cloud_outbox_v9 RENAME TO personal_cloud_outbox;
