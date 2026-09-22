-- A06 versioned schedule, paper and grading-rule event stream.
-- Apply with a database role that owns these application tables. Runtime roles only need DML rights.

CREATE SCHEMA IF NOT EXISTS exam;

CREATE TABLE IF NOT EXISTS exam.version_streams
(
    exam_id          text        PRIMARY KEY,
    stream_revision  bigint      NOT NULL CHECK (stream_revision >= 1),
    created_at        timestamptz NOT NULL,
    updated_at        timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS exam.version_events
(
    exam_id          text        NOT NULL REFERENCES exam.version_streams (exam_id) ON DELETE RESTRICT,
    stream_revision  bigint      NOT NULL CHECK (stream_revision >= 1),
    operation_type   text        NOT NULL,
    payload           jsonb       NOT NULL,
    recorded_at       timestamptz NOT NULL,
    PRIMARY KEY (exam_id, stream_revision)
);

CREATE TABLE IF NOT EXISTS exam.version_state
(
    exam_id            text        PRIMARY KEY REFERENCES exam.version_streams (exam_id) ON DELETE RESTRICT,
    stream_revision    bigint      NOT NULL CHECK (stream_revision >= 1),
    schedule_version   bigint      NOT NULL CHECK (schedule_version >= 1),
    paper_version      bigint      NOT NULL CHECK (paper_version >= 0),
    grading_version    bigint      NOT NULL CHECK (grading_version >= 0),
    starts_at          timestamptz NOT NULL,
    ends_at            timestamptz NOT NULL,
    lifecycle          text        NOT NULL CHECK (lifecycle IN ('Scheduled', 'Running', 'Ended')),
    actual_started_at  timestamptz NULL,
    actual_ended_at    timestamptz NULL,
    snapshot           jsonb       NOT NULL,
    updated_at         timestamptz NOT NULL,
    CHECK (ends_at > starts_at)
);

CREATE INDEX IF NOT EXISTS ix_exam_version_events_operation
    ON exam.version_events (operation_type, recorded_at);

CREATE INDEX IF NOT EXISTS ix_exam_version_state_versions
    ON exam.version_state (schedule_version, paper_version, grading_version);

CREATE TABLE IF NOT EXISTS exam.version_idempotency
(
    request_id text PRIMARY KEY,
    operation_type text NOT NULL,
    payload_hash text NOT NULL CHECK (payload_hash ~ '^[0-9A-F]{64}$'),
    response_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
