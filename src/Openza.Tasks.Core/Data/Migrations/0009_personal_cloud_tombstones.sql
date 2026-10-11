-- Execute through the store compatibility migration, with foreign keys disabled
-- and a transaction. Deletion history can outlive the physically removed task.
CREATE TABLE personal_cloud_tombstones_v9 (
  instance_id TEXT NOT NULL,
  product TEXT NOT NULL CHECK (product = 'tasks'),
  entity_id TEXT NOT NULL,
  entity_version INTEGER NOT NULL CHECK (entity_version > 0),
  deleted_at INTEGER NOT NULL,
  purge_after INTEGER,
  PRIMARY KEY (instance_id, product, entity_id)
);
INSERT INTO personal_cloud_tombstones_v9
SELECT instance_id, product, entity_id, entity_version, deleted_at, purge_after
FROM personal_cloud_tombstones;
DROP TABLE personal_cloud_tombstones;
ALTER TABLE personal_cloud_tombstones_v9 RENAME TO personal_cloud_tombstones;
