-- Membership and pending checks are committed with the originating business transaction.
CREATE TABLE exam.actual_roster_versions
(
    exam_id text PRIMARY KEY,
    roster_version bigint NOT NULL CHECK (roster_version > 0)
);

CREATE TABLE exam.collection_pending_checks
(
    exam_id text PRIMARY KEY,
    requested_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

ALTER TABLE exam.collection_completion_events
    ADD COLUMN delivery_fence_at timestamptz NULL,
    ADD COLUMN is_authoritative boolean NOT NULL DEFAULT false;

CREATE FUNCTION exam.queue_collection_membership_change() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    old_actual boolean := false;
    new_actual boolean := false;
    target_exam text;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        old_actual := OLD.business_state <> 'not_participating';
    END IF;
    IF TG_OP <> 'DELETE' THEN
        new_actual := NEW.business_state <> 'not_participating';
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD.exam_id <> NEW.exam_id OR OLD.participation_id <> NEW.participation_id) THEN
        RAISE EXCEPTION 'Participation identity is immutable';
    END IF;
    IF old_actual = new_actual THEN
        RETURN NULL;
    END IF;
    target_exam := CASE WHEN TG_OP = 'DELETE' THEN OLD.exam_id ELSE NEW.exam_id END;
    INSERT INTO exam.actual_roster_versions (exam_id, roster_version)
        VALUES (target_exam, 1)
        ON CONFLICT (exam_id) DO UPDATE
        SET roster_version = exam.actual_roster_versions.roster_version + 1;
    INSERT INTO exam.collection_pending_checks (exam_id) VALUES (target_exam)
        ON CONFLICT (exam_id) DO UPDATE SET requested_at = clock_timestamp();
    RETURN NULL;
END;
$$;

CREATE TRIGGER collection_membership_changed
    AFTER INSERT OR UPDATE OR DELETE ON exam.participations
    FOR EACH ROW EXECUTE FUNCTION exam.queue_collection_membership_change();

CREATE FUNCTION exam.queue_collection_submission() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO exam.collection_pending_checks (exam_id) VALUES (NEW.exam_id)
        ON CONFLICT (exam_id) DO UPDATE SET requested_at = clock_timestamp();
    RETURN NULL;
END;
$$;

CREATE TRIGGER collection_submission_saved
    AFTER INSERT ON exam.final_submissions
    FOR EACH ROW EXECUTE FUNCTION exam.queue_collection_submission();

-- Existing manual events may have arbitrary versions. Start above their high-water mark;
-- do not delete history or mistake an older, caller-supplied event for a current completion.
INSERT INTO exam.actual_roster_versions (exam_id, roster_version)
    SELECT ids.exam_id, COALESCE(MAX(e.roster_version), 0) + 1
      FROM (SELECT exam_id FROM exam.participations
            UNION SELECT exam_id FROM exam.collection_completion_events) ids
      LEFT JOIN exam.collection_completion_events e ON e.exam_id = ids.exam_id
     GROUP BY ids.exam_id;
INSERT INTO exam.collection_pending_checks (exam_id)
    SELECT exam_id FROM exam.actual_roster_versions;

COMMENT ON TABLE exam.collection_completion_events IS
    'Durable operator notification outbox. Replay per exam ordered by roster_version; consumers deduplicate by event_id.';
