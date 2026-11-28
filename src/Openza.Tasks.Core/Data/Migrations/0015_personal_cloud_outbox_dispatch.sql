ALTER TABLE personal_cloud_outbox ADD COLUMN dispatched_at INTEGER;
UPDATE personal_cloud_outbox SET dispatched_at=created_at WHERE acknowledged_at IS NULL;
