-- D04 计分结果持久化。
-- 一份答卷一行：换规则版本重算时更新该行，因此成绩不可能被重复累计。
-- 提交方为 A05 的计分 outbox（exam.grading_outbox）；消费与写入由 D04 的后台计分协调器完成。
-- 前置版本：A05 的 exam.final_submissions 与 exam.grading_outbox 已存在。
-- 恢复方案：本迁移只新增表与索引，回退时删除本表即可，不影响既有答卷、回执与事件数据。

CREATE TABLE IF NOT EXISTS exam.grading_results
(
    submission_id   text PRIMARY KEY
                    REFERENCES exam.final_submissions(submission_id) ON DELETE RESTRICT,
    exam_id         text NOT NULL,
    candidate_id    text NOT NULL,
    grading_version bigint NOT NULL CHECK (grading_version >= 1),
    question_scores jsonb NOT NULL,
    subject_scores  jsonb NOT NULL,
    total_score     text NOT NULL,
    graded_at       timestamptz NOT NULL
);

-- 报表按（考试，已发布的计分版本）整体读取；同一次发布期间该组合只对应一个版本。
CREATE INDEX IF NOT EXISTS ix_grading_results_exam_version
    ON exam.grading_results (exam_id, grading_version);
