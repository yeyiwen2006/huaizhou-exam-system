-- Versioned public instructions generated from structured rules by the exam configuration module.
-- This stores display text only; scoring never interprets these strings.
CREATE TABLE IF NOT EXISTS exam.student_exam_instructions
(
    exam_id text NOT NULL REFERENCES exam.exam_entry_access (exam_id) ON DELETE RESTRICT,
    instructions_version bigint NOT NULL CHECK (instructions_version > 0),
    lines jsonb NOT NULL CHECK (jsonb_typeof(lines) = 'array' AND jsonb_array_length(lines) > 0),
    created_at timestamptz NOT NULL,
    created_by text NOT NULL,
    PRIMARY KEY (exam_id, instructions_version)
);

CREATE TABLE IF NOT EXISTS exam.student_metadata_idempotency
(
    request_id text PRIMARY KEY,
    payload_hash text NOT NULL CHECK (payload_hash ~ '^[0-9A-F]{64}$'),
    response_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
