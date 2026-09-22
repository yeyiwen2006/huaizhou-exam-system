-- Public, configured metadata for the student exam selector. No question content.
CREATE TABLE IF NOT EXISTS exam.student_exam_metadata
(
    exam_id text PRIMARY KEY REFERENCES exam.exam_entry_access (exam_id) ON DELETE RESTRICT,
    display_name text NOT NULL CHECK (length(btrim(display_name)) > 0),
    category_name text NULL,
    subjects jsonb NOT NULL CHECK (jsonb_typeof(subjects) = 'array'),
    version bigint NOT NULL CHECK (version > 0),
    updated_at timestamptz NOT NULL,
    updated_by text NOT NULL
);
