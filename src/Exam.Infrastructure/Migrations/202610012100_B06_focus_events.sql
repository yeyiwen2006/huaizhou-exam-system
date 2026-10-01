-- B06 focus loss events: audit trail reported by student clients.
-- Deduplicated by (participation_id, event_id); reports remain acceptable after
-- submission because the events were generated while answering.
CREATE TABLE IF NOT EXISTS exam.focus_loss_events
(
    participation_id      text NOT NULL
                          REFERENCES exam.participations(participation_id) ON DELETE RESTRICT,
    event_id              text NOT NULL,
    exam_id               text NOT NULL,
    candidate_id          text NOT NULL,
    entry_version         bigint NOT NULL CHECK (entry_version >= 1),
    device_id             text NOT NULL,
    authorization_version bigint NOT NULL CHECK (authorization_version >= 1),
    paper_version         bigint NOT NULL CHECK (paper_version >= 1),
    lost_at               timestamptz NOT NULL,
    regained_at           timestamptz NOT NULL,
    duration_ms           bigint NOT NULL CHECK (duration_ms >= 0),
    received_at           timestamptz NOT NULL,
    PRIMARY KEY (participation_id, event_id)
);

CREATE INDEX IF NOT EXISTS ix_focus_loss_events_exam
    ON exam.focus_loss_events (exam_id, candidate_id);
