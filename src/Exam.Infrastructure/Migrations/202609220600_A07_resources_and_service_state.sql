CREATE TABLE IF NOT EXISTS exam.versioned_resources
(
    resource_id text PRIMARY KEY,
    exam_id text NOT NULL,
    resource_kind text NOT NULL,
    resource_version bigint NOT NULL CHECK (resource_version > 0),
    encrypted_content bytea NOT NULL,
    encryption_envelope jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    created_by text NOT NULL,
    UNIQUE (exam_id, resource_kind, resource_version)
);

CREATE INDEX IF NOT EXISTS ix_versioned_resources_exam_kind_version
    ON exam.versioned_resources (exam_id, resource_kind, resource_version DESC);

CREATE TABLE IF NOT EXISTS exam.server_state
(
    server_id text PRIMARY KEY,
    active_node_id text NOT NULL,
    server_epoch bigint NOT NULL CHECK (server_epoch > 0),
    protection_mode text NOT NULL CHECK
        (protection_mode IN ('SynchronousDualNode', 'EmergencySingleNode', 'Recovering')),
    state_version bigint NOT NULL CHECK (state_version > 0),
    updated_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS exam.server_nodes
(
    server_id text NOT NULL REFERENCES exam.server_state (server_id) ON DELETE CASCADE,
    node_id text NOT NULL,
    node_role text NOT NULL CHECK
        (node_role IN ('Primary', 'Standby', 'Isolated', 'Rebuilding')),
    reachable boolean NOT NULL,
    database_in_recovery boolean NOT NULL,
    replay_lag_bytes bigint NOT NULL CHECK (replay_lag_bytes >= 0),
    key_material_ready boolean NOT NULL,
    certificate_ready boolean NOT NULL,
    observed_at timestamptz NOT NULL,
    PRIMARY KEY (server_id, node_id)
);

ALTER TABLE exam.server_state
    DROP CONSTRAINT IF EXISTS fk_server_state_active_node;
ALTER TABLE exam.server_state
    ADD CONSTRAINT fk_server_state_active_node
    FOREIGN KEY (server_id, active_node_id)
    REFERENCES exam.server_nodes (server_id, node_id)
    DEFERRABLE INITIALLY DEFERRED;

CREATE TABLE IF NOT EXISTS exam.failover_audit
(
    audit_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    server_id text NOT NULL,
    action text NOT NULL,
    actor_id text NOT NULL,
    reason text NOT NULL,
    node_id text NOT NULL,
    server_epoch bigint NOT NULL,
    occurred_at timestamptz NOT NULL,
    UNIQUE (server_id, action, actor_id, node_id, server_epoch, occurred_at)
);

CREATE INDEX IF NOT EXISTS ix_failover_audit_server_time
    ON exam.failover_audit (server_id, occurred_at, audit_id);

CREATE TABLE IF NOT EXISTS exam.backup_records
(
    backup_id text PRIMARY KEY,
    request_id text NOT NULL UNIQUE,
    destination_directory text NOT NULL,
    completed_at timestamptz NOT NULL,
    includes_database boolean NOT NULL,
    includes_protected_configuration boolean NOT NULL,
    includes_versioned_resources boolean NOT NULL,
    actor_id text NOT NULL
);

CREATE TABLE IF NOT EXISTS exam.restore_records
(
    request_id text PRIMARY KEY,
    backup_id text NOT NULL,
    source_directory text NOT NULL,
    staging_directory text NOT NULL,
    actor_id text NOT NULL,
    completed_at timestamptz NOT NULL
);
