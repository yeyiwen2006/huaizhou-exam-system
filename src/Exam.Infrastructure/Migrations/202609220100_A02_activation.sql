-- A02 activation and daily client-number allocation.
-- Up migration only. This script intentionally contains no seed devices, credentials,
-- candidate data, or real examination material.

CREATE SCHEMA IF NOT EXISTS exam;

CREATE TABLE IF NOT EXISTS exam.activation_daily_sequences
(
    activation_date date PRIMARY KEY,
    last_sequence integer NOT NULL,
    CONSTRAINT ck_activation_daily_sequences_range
        CHECK (last_sequence BETWEEN 0 AND 999)
);

CREATE TABLE IF NOT EXISTS exam.device_activation_requests
(
    activation_request_id text PRIMARY KEY,
    device_id text NOT NULL,
    protected_credential text NOT NULL,
    activation_status text NOT NULL,
    requested_at timestamptz NOT NULL,
    client_number text NULL,
    credential_version bigint NOT NULL,
    CONSTRAINT uq_device_activation_requests_device UNIQUE (device_id),
    CONSTRAINT ck_device_activation_requests_status
        CHECK (activation_status IN ('pending', 'active', 'revoked', 'rejected')),
    CONSTRAINT ck_device_activation_requests_credential_version
        CHECK (credential_version > 0),
    CONSTRAINT ck_device_activation_requests_client_number
        CHECK (client_number IS NULL OR client_number ~ '^[0-9]{11}$')
);

CREATE INDEX IF NOT EXISTS ix_device_activation_requests_device_requested
    ON exam.device_activation_requests (device_id, requested_at DESC);

CREATE TABLE IF NOT EXISTS exam.activation_devices
(
    device_id text PRIMARY KEY,
    client_number text NOT NULL,
    activation_date date NOT NULL,
    activation_status text NOT NULL,
    room_group_id text NULL,
    protected_credential text NOT NULL,
    credential_version bigint NOT NULL,
    created_at timestamptz NOT NULL,
    last_seen_at timestamptz NULL,
    version bigint NOT NULL,
    CONSTRAINT uq_activation_devices_client_number UNIQUE (client_number),
    CONSTRAINT ck_activation_devices_client_number
        CHECK (client_number ~ '^[0-9]{11}$'),
    CONSTRAINT ck_activation_devices_status
        CHECK (activation_status IN ('active', 'revoked')),
    CONSTRAINT ck_activation_devices_credential_version
        CHECK (credential_version > 0),
    CONSTRAINT ck_activation_devices_version
        CHECK (version > 0)
);

CREATE INDEX IF NOT EXISTS ix_activation_devices_date
    ON exam.activation_devices (activation_date, client_number);

CREATE TABLE IF NOT EXISTS exam.activation_idempotency
(
    request_id text PRIMARY KEY,
    operation text NOT NULL,
    payload_hash text NOT NULL,
    response_type text NOT NULL,
    response_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_activation_idempotency_payload_hash
        CHECK (payload_hash ~ '^[0-9A-F]{64}$')
);

COMMENT ON COLUMN exam.device_activation_requests.protected_credential IS
    'One-way credential verifier. Raw device credentials must never be stored here.';
COMMENT ON TABLE exam.activation_daily_sequences IS
    'One locked row per server-local activation date; values stop at 999.';
