-- Per-exam provenance and import retry receipts; no server identity or credentials.
CREATE TABLE exam.exam_archive_origins (
    exam_id text PRIMARY KEY REFERENCES exam.exam_entry_access(exam_id) ON DELETE RESTRICT,
    origin_exam_id text NOT NULL,
    archive_id text NOT NULL,
    imported_at timestamptz NOT NULL,
    imported_by text NOT NULL
);
CREATE INDEX ix_exam_archive_origins_origin ON exam.exam_archive_origins(origin_exam_id);
CREATE TABLE exam.exam_archive_device_history (
    exam_id text NOT NULL,
    device_id text NOT NULL REFERENCES exam.activation_devices(device_id) ON DELETE RESTRICT,
    source_device_id text NOT NULL,
    source_client_number text NOT NULL,
    PRIMARY KEY(exam_id,device_id)
);
CREATE TABLE exam.exam_archive_imports (
    request_id text PRIMARY KEY,
    archive_id text NOT NULL,
    mode text NOT NULL,
    target_exam_id text NOT NULL,
    actor_id text NOT NULL,
    result jsonb NOT NULL,
    imported_at timestamptz NOT NULL
);
-- Pending A-side device decisions remain historical evidence, not actionable B-side authorizations.
CREATE TABLE exam.exam_archive_pending_device_conflicts (
    exam_id text NOT NULL,
    participation_id text NOT NULL REFERENCES exam.participations(participation_id) ON DELETE RESTRICT,
    original_device_id text NOT NULL REFERENCES exam.activation_devices(device_id) ON DELETE RESTRICT,
    pending_device_id text NOT NULL REFERENCES exam.activation_devices(device_id) ON DELETE RESTRICT,
    detected_at timestamptz NOT NULL,
    PRIMARY KEY(exam_id,participation_id,original_device_id,pending_device_id,detected_at)
);
