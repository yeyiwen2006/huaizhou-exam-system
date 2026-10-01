-- 考务端考试目录：考试名称（即考试类别）唯一，完整考试设置与学生端显示开关。
-- 只新增表，不修改既有数据；没有目录行的旧考试保持原有显示行为。
CREATE TABLE IF NOT EXISTS exam.exam_catalog
(
    exam_id text PRIMARY KEY,
    exam_name text NOT NULL CHECK (length(btrim(exam_name)) BETWEEN 1 AND 128),
    name_key text NOT NULL,
    settings jsonb NOT NULL CHECK (jsonb_typeof(settings) = 'object'),
    visible_to_students boolean NOT NULL DEFAULT false,
    version bigint NOT NULL CHECK (version > 0),
    created_at timestamptz NOT NULL,
    created_by text NOT NULL,
    updated_at timestamptz NOT NULL,
    updated_by text NOT NULL,
    CONSTRAINT uq_exam_catalog_name_key UNIQUE (name_key)
);

CREATE TABLE IF NOT EXISTS exam.exam_catalog_idempotency
(
    request_id text PRIMARY KEY,
    payload_hash text NOT NULL,
    response_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
