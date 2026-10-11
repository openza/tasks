-- Additive local schema 9 provider journal. No private provider IDs or credentials
-- belong in these tables; account_binding is a local-only identity/fingerprint.
CREATE TABLE IF NOT EXISTS personal_cloud_provider_sources (
  instance_id TEXT NOT NULL, task_id TEXT NOT NULL, source_json TEXT NOT NULL,
  PRIMARY KEY(instance_id,task_id)
);
CREATE TABLE IF NOT EXISTS personal_cloud_provider_commands (
  instance_id TEXT NOT NULL, task_id TEXT NOT NULL, requested_seq INTEGER NOT NULL CHECK(requested_seq>0),
  command TEXT NOT NULL CHECK(command IN ('complete','reopen')),
  state TEXT NOT NULL CHECK(state IN ('pending_desktop','confirmed','rejected','cancelled')),
  error_code TEXT, retry_id TEXT,
  PRIMARY KEY(instance_id,task_id)
);
CREATE TABLE IF NOT EXISTS personal_cloud_provider_attempts (
  claim_id TEXT PRIMARY KEY, instance_id TEXT NOT NULL, task_id TEXT NOT NULL,
  requested_seq INTEGER NOT NULL, device_id TEXT NOT NULL, account_binding TEXT NOT NULL,
  command TEXT NOT NULL, source_json TEXT NOT NULL,
  state TEXT NOT NULL CHECK(state IN ('claiming','reserved','starting','dispatching','uncertain','outcome','settled','cancelled')),
  result_mutation_id TEXT,
  UNIQUE(instance_id,task_id,requested_seq,device_id)
);
