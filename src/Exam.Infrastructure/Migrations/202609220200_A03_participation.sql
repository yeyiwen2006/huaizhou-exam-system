-- A03 roster identity, entry preparation, participation, and device authorization.
-- Up migration only. A05 uses exam.participations as the canonical participation row.

CREATE SCHEMA IF NOT EXISTS exam;

CREATE TABLE IF NOT EXISTS exam.candidate_roster_entries
(
    exam_id text NOT NULL,
    candidate_id text NOT NULL,
    candidate_name text NOT NULL,
    class_text text NOT NULL,
    admission_no text NOT NULL,
    PRIMARY KEY (exam_id, candidate_id),
    CONSTRAINT uq_candidate_roster_exam_admission UNIQUE (exam_id, admission_no),
    CONSTRAINT ck_candidate_roster_class_digits CHECK (class_text ~ '^[0-9]+$'),
    CONSTRAINT ck_candidate_roster_admission_digits CHECK (admission_no ~ '^[0-9]+$')
);

CREATE INDEX IF NOT EXISTS ix_candidate_roster_login
    ON exam.candidate_roster_entries (candidate_name, class_text, admission_no);

-- Persisted entry projection written by the exam/version modules and read by A03.
CREATE TABLE IF NOT EXISTS exam.exam_entry_access
(
    exam_id text PRIMARY KEY,
    scheduled_start timestamptz NOT NULL,
    scheduled_end timestamptz NOT NULL,
    lifecycle text NOT NULL,
    package_ready boolean NOT NULL DEFAULT false,
    instructions_ready boolean NOT NULL DEFAULT false,
    encrypted_package_uri text NOT NULL DEFAULT '',
    package_length bigint NOT NULL DEFAULT 0,
    paper_version bigint NOT NULL DEFAULT 1,
    instructions_version bigint NOT NULL DEFAULT 1,
    unlock_reference text NULL,
    CONSTRAINT ck_exam_entry_access_schedule CHECK (scheduled_end > scheduled_start),
    CONSTRAINT ck_exam_entry_access_lifecycle
        CHECK (lifecycle IN ('scheduled', 'running', 'ended')),
    CONSTRAINT ck_exam_entry_access_package_length CHECK (package_length >= 0),
    CONSTRAINT ck_exam_entry_access_paper_version CHECK (paper_version > 0),
    CONSTRAINT ck_exam_entry_access_instructions_version CHECK (instructions_version > 0)
);

CREATE TABLE IF NOT EXISTS exam.student_sessions
(
    session_id text PRIMARY KEY,
    device_id text NOT NULL REFERENCES exam.activation_devices (device_id) ON DELETE RESTRICT,
    candidate_id text NOT NULL,
    candidate_name text NOT NULL,
    class_text text NOT NULL,
    admission_no text NOT NULL,
    exam_ids text[] NOT NULL,
    candidate_ids_by_exam jsonb NOT NULL,
    authenticated_at timestamptz NOT NULL,
    CONSTRAINT ck_student_sessions_exam_ids CHECK (cardinality(exam_ids) > 0),
    CONSTRAINT ck_student_sessions_candidate_map
        CHECK (jsonb_typeof(candidate_ids_by_exam) = 'object'),
    CONSTRAINT ck_student_sessions_class_digits CHECK (class_text ~ '^[0-9]+$'),
    CONSTRAINT ck_student_sessions_admission_digits CHECK (admission_no ~ '^[0-9]+$')
);

CREATE INDEX IF NOT EXISTS ix_student_sessions_device
    ON exam.student_sessions (device_id, authenticated_at DESC);

CREATE TABLE IF NOT EXISTS exam.entry_preparations
(
    preparation_id text PRIMARY KEY,
    session_id text NOT NULL REFERENCES exam.student_sessions (session_id) ON DELETE RESTRICT,
    exam_id text NOT NULL,
    candidate_id text NOT NULL,
    device_id text NOT NULL REFERENCES exam.activation_devices (device_id) ON DELETE RESTRICT,
    paper_version bigint NOT NULL,
    instructions_version bigint NOT NULL,
    prepared_at timestamptz NOT NULL,
    CONSTRAINT fk_entry_preparations_roster
        FOREIGN KEY (exam_id, candidate_id)
        REFERENCES exam.candidate_roster_entries (exam_id, candidate_id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_entry_preparations_paper_version CHECK (paper_version > 0),
    CONSTRAINT ck_entry_preparations_instructions_version CHECK (instructions_version > 0)
);

CREATE INDEX IF NOT EXISTS ix_entry_preparations_session_exam
    ON exam.entry_preparations (session_id, exam_id, prepared_at DESC);

CREATE TABLE IF NOT EXISTS exam.participations
(
    participation_id text PRIMARY KEY,
    exam_id text NOT NULL,
    candidate_id text NOT NULL,
    entry_version bigint NOT NULL,
    business_state text NOT NULL,
    authorized_device_id text NOT NULL
        REFERENCES exam.activation_devices (device_id) ON DELETE RESTRICT,
    authorization_version bigint NOT NULL,
    version bigint NOT NULL,
    current_paper_version bigint NOT NULL DEFAULT 1,
    current_grading_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_participations_exam_candidate UNIQUE (exam_id, candidate_id),
    CONSTRAINT fk_participations_roster
        FOREIGN KEY (exam_id, candidate_id)
        REFERENCES exam.candidate_roster_entries (exam_id, candidate_id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_participations_entry_version CHECK (entry_version > 0),
    CONSTRAINT ck_participations_authorization_version CHECK (authorization_version > 0),
    CONSTRAINT ck_participations_version CHECK (version > 0),
    CONSTRAINT ck_participations_paper_version CHECK (current_paper_version > 0),
    CONSTRAINT ck_participations_grading_version CHECK (current_grading_version > 0),
    CONSTRAINT ck_participations_business_state
        CHECK (business_state IN ('not_participating', 'waiting', 'answering', 'submitted'))
);

CREATE INDEX IF NOT EXISTS ix_participations_exam_state
    ON exam.participations (exam_id, business_state, candidate_id);

CREATE TABLE IF NOT EXISTS exam.participation_device_conflicts
(
    participation_id text PRIMARY KEY
        REFERENCES exam.participations (participation_id) ON DELETE CASCADE,
    original_device_id text NOT NULL
        REFERENCES exam.activation_devices (device_id) ON DELETE RESTRICT,
    pending_device_id text NOT NULL
        REFERENCES exam.activation_devices (device_id) ON DELETE RESTRICT,
    detected_at timestamptz NOT NULL,
    CONSTRAINT ck_participation_device_conflicts_different
        CHECK (original_device_id <> pending_device_id)
);

CREATE TABLE IF NOT EXISTS exam.device_decision_audits
(
    decision_id text PRIMARY KEY,
    participation_id text NOT NULL
        REFERENCES exam.participations (participation_id) ON DELETE RESTRICT,
    selected_device_id text NOT NULL
        REFERENCES exam.activation_devices (device_id) ON DELETE RESTRICT,
    revoked_device_id text NOT NULL
        REFERENCES exam.activation_devices (device_id) ON DELETE RESTRICT,
    authorization_version bigint NOT NULL,
    operator_id text NOT NULL,
    reason text NOT NULL,
    decided_at timestamptz NOT NULL,
    CONSTRAINT ck_device_decision_audits_authorization_version
        CHECK (authorization_version > 0),
    CONSTRAINT ck_device_decision_audits_different
        CHECK (selected_device_id <> revoked_device_id)
);

CREATE INDEX IF NOT EXISTS ix_device_decision_audits_participation
    ON exam.device_decision_audits (participation_id, decided_at);

CREATE TABLE IF NOT EXISTS exam.participation_idempotency
(
    request_id text PRIMARY KEY,
    operation text NOT NULL,
    payload_hash text NOT NULL,
    response_type text NOT NULL,
    response_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_participation_idempotency_payload_hash
        CHECK (payload_hash ~ '^[0-9A-F]{64}$')
);

COMMENT ON TABLE exam.participations IS
    'Canonical A03 participation and authorization row; A05 submission finalization locks and updates this row.';
COMMENT ON COLUMN exam.participations.current_paper_version IS
    'Current server progress/submission paper version. A03 preserves this column on authorization updates.';
COMMENT ON COLUMN exam.participations.current_grading_version IS
    'Current grading version used by downstream submission/grading flows. A03 preserves this column.';
