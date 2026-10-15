-- A transactionally replicated revision replaces full resource-table scans on every confirmation.
-- Resource validation reads this row and the resources in the same repeatable-read snapshot.
CREATE TABLE exam.confirmation_resource_revision
(
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    revision bigint NOT NULL CHECK (revision > 0)
);
INSERT INTO exam.confirmation_resource_revision VALUES (true, 1);

CREATE FUNCTION exam.advance_confirmation_resource_revision() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'TRUNCATE' THEN
        CASE TG_TABLE_NAME
            WHEN 'versioned_resources' THEN
                IF OLD.resource_kind IS DISTINCT FROM 'paper-package'
                   AND NEW.resource_kind IS DISTINCT FROM 'paper-package' THEN RETURN NULL; END IF;
            WHEN 'version_events' THEN
                IF OLD.operation_type IS DISTINCT FROM 'paper.publish'
                   AND NEW.operation_type IS DISTINCT FROM 'paper.publish' THEN RETURN NULL; END IF;
            WHEN 'version_state' THEN
                IF (COALESCE(OLD.paper_version, 0) <= 0 AND COALESCE(NEW.paper_version, 0) <= 0)
                   OR (TG_OP = 'UPDATE' AND
                       ROW(OLD.exam_id,OLD.paper_version,OLD.snapshot->'paper')
                       IS NOT DISTINCT FROM
                       ROW(NEW.exam_id,NEW.paper_version,NEW.snapshot->'paper'))
                THEN RETURN NULL; END IF;
            WHEN 'exam_entry_access' THEN
                IF (COALESCE(OLD.package_ready, false) = false AND COALESCE(NEW.package_ready, false) = false)
                   OR (TG_OP = 'UPDATE' AND
                       ROW(OLD.exam_id,OLD.package_ready,OLD.paper_version,OLD.unlock_reference,OLD.package_length)
                       IS NOT DISTINCT FROM
                       ROW(NEW.exam_id,NEW.package_ready,NEW.paper_version,NEW.unlock_reference,NEW.package_length))
                THEN RETURN NULL; END IF;
            ELSE NULL;
        END CASE;
    END IF;
    UPDATE exam.confirmation_resource_revision SET revision=revision+1 WHERE singleton;
    IF NOT FOUND THEN RAISE EXCEPTION 'Confirmation resource revision is missing'; END IF;
    RETURN NULL;
END;
$$;

CREATE TRIGGER confirmation_resource_change AFTER INSERT OR UPDATE OR DELETE ON exam.versioned_resources
    FOR EACH ROW EXECUTE FUNCTION exam.advance_confirmation_resource_revision();
CREATE TRIGGER confirmation_material_change AFTER INSERT OR UPDATE OR DELETE ON exam.paper_materials
    FOR EACH ROW EXECUTE FUNCTION exam.advance_confirmation_resource_revision();
CREATE TRIGGER confirmation_paper_change AFTER INSERT OR UPDATE OR DELETE ON exam.version_state
    FOR EACH ROW EXECUTE FUNCTION exam.advance_confirmation_resource_revision();
CREATE TRIGGER confirmation_entry_change AFTER INSERT OR UPDATE OR DELETE ON exam.exam_entry_access
    FOR EACH ROW EXECUTE FUNCTION exam.advance_confirmation_resource_revision();
CREATE TRIGGER confirmation_publication_change AFTER INSERT OR UPDATE OR DELETE ON exam.version_events
    FOR EACH ROW EXECUTE FUNCTION exam.advance_confirmation_resource_revision();

CREATE TRIGGER confirmation_resource_truncate AFTER TRUNCATE ON exam.versioned_resources
    FOR EACH STATEMENT EXECUTE FUNCTION exam.advance_confirmation_resource_revision();
CREATE TRIGGER confirmation_material_truncate AFTER TRUNCATE ON exam.paper_materials
    FOR EACH STATEMENT EXECUTE FUNCTION exam.advance_confirmation_resource_revision();
CREATE TRIGGER confirmation_paper_truncate AFTER TRUNCATE ON exam.version_state
    FOR EACH STATEMENT EXECUTE FUNCTION exam.advance_confirmation_resource_revision();
CREATE TRIGGER confirmation_entry_truncate AFTER TRUNCATE ON exam.exam_entry_access
    FOR EACH STATEMENT EXECUTE FUNCTION exam.advance_confirmation_resource_revision();
CREATE TRIGGER confirmation_publication_truncate AFTER TRUNCATE ON exam.version_events
    FOR EACH STATEMENT EXECUTE FUNCTION exam.advance_confirmation_resource_revision();
