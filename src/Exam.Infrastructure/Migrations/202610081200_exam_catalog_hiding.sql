-- Empty operator_key denotes the shared scope; nonempty keys are authenticated account keys.
-- Independent from student visibility; existing exams stay on the ordinary admin list by default.
CREATE TABLE exam.exam_catalog_hiding
(
    exam_id text NOT NULL REFERENCES exam.exam_entry_access (exam_id) ON DELETE RESTRICT,
    operator_key text NOT NULL,
    hidden boolean NOT NULL,
    updated_at timestamptz NOT NULL,
    updated_by text NOT NULL,
    PRIMARY KEY (exam_id, operator_key)
);
