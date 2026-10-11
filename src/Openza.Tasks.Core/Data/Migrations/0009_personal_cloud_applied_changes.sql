-- Execute through the store compatibility migration, with foreign keys disabled
-- and a transaction. Replica receipts are retained independently of task rows.
CREATE TABLE personal_cloud_applied_changes_v9 (
  instance_id TEXT NOT NULL,
  product TEXT NOT NULL CHECK (product = 'tasks'),
  change_seq INTEGER NOT NULL CHECK (change_seq > 0),
  mutation_id TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  applied_at INTEGER NOT NULL,
  PRIMARY KEY (instance_id, product, change_seq),
  UNIQUE (instance_id, product, mutation_id)
);
INSERT INTO personal_cloud_applied_changes_v9
SELECT instance_id, product, change_seq, mutation_id, entity_id, applied_at
FROM personal_cloud_applied_changes;
DROP TABLE personal_cloud_applied_changes;
ALTER TABLE personal_cloud_applied_changes_v9 RENAME TO personal_cloud_applied_changes;
