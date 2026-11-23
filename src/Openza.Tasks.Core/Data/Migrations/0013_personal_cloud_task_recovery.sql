CREATE TABLE IF NOT EXISTS personal_cloud_task_recovery (
    instance_id TEXT NOT NULL,
    task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    PRIMARY KEY (instance_id, task_id)
);
