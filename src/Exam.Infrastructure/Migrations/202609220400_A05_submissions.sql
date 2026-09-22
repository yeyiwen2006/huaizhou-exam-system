CREATE SCHEMA IF NOT EXISTS exam;

-- A03 owns exam.participations. These two version columns are also declared by A03;
-- ADD IF NOT EXISTS keeps independently replayed development migrations safe.
ALTER TABLE exam.participations
    ADD COLUMN IF NOT EXISTS current_paper_version bigint NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS current_grading_version bigint NOT NULL DEFAULT 1;

CREATE TABLE IF NOT EXISTS exam.participation_progress
(
    participation_id      text PRIMARY KEY
                          REFERENCES exam.participations(participation_id) ON DELETE RESTRICT,
    exam_id               text NOT NULL,
    candidate_id          text NOT NULL,
    entry_version         bigint NOT NULL CHECK (entry_version >= 1),
    device_id             text NOT NULL,
    authorization_version bigint NOT NULL CHECK (authorization_version >= 1),
    answer_version        bigint NOT NULL CHECK (answer_version >= 0),
    paper_version         bigint NOT NULL CHECK (paper_version >= 1),
    answers               jsonb NOT NULL,
    current_question_id   text NULL,
    last_synced_at        timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS exam.progress_idempotency
(
    request_id       text PRIMARY KEY,
    exam_id          text NOT NULL,
    candidate_id     text NOT NULL,
    participation_id text NOT NULL,
    device_id        text NOT NULL,
    result_payload   jsonb NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS exam.final_submissions
(
    submission_id        text PRIMARY KEY,
    exam_id              text NOT NULL,
    candidate_id         text NOT NULL,
    participation_id     text NOT NULL UNIQUE
                         REFERENCES exam.participations(participation_id) ON DELETE RESTRICT,
    device_id            text NOT NULL,
    authorization_version bigint NOT NULL CHECK (authorization_version >= 1),
    paper_version        bigint NOT NULL CHECK (paper_version >= 1),
    frozen_answers       jsonb NOT NULL,
    received_at          timestamptz NOT NULL,
    receipt_id           text NOT NULL UNIQUE,
    grading_version      bigint NOT NULL CHECK (grading_version >= 1),
    CONSTRAINT uq_final_submissions_exam_candidate UNIQUE (exam_id, candidate_id)
);

CREATE TABLE IF NOT EXISTS exam.submission_idempotency
(
    request_id       text PRIMARY KEY,
    exam_id          text NOT NULL,
    candidate_id     text NOT NULL,
    participation_id text NOT NULL,
    device_id        text NOT NULL,
    result_payload   jsonb NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS exam.grading_outbox
(
    submission_id   text NOT NULL REFERENCES exam.final_submissions(submission_id) ON DELETE RESTRICT,
    grading_version bigint NOT NULL CHECK (grading_version >= 1),
    created_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    claimed_at      timestamptz NULL,
    completed_at    timestamptz NULL,
    attempt_count   integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    last_error      text NULL,
    PRIMARY KEY (submission_id, grading_version)
);

CREATE INDEX IF NOT EXISTS ix_grading_outbox_pending
    ON exam.grading_outbox (created_at, submission_id, grading_version)
    WHERE completed_at IS NULL;

CREATE TABLE IF NOT EXISTS exam.collection_completion_events
(
    exam_id           text NOT NULL,
    roster_version    bigint NOT NULL CHECK (roster_version >= 1),
    event_id          text NOT NULL UNIQUE,
    participant_count integer NOT NULL CHECK (participant_count > 0),
    completed_at      timestamptz NOT NULL,
    PRIMARY KEY (exam_id, roster_version)
);
