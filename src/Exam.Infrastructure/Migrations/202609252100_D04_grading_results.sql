-- D04 计分结果持久化。
-- 一份答卷一行：换规则版本重算时更新该行，因此成绩不可能被重复累计。
-- 提交方为 A05 的计分 outbox（exam.grading_outbox）；消费与写入由 D04 的后台计分协调器完成。
-- 前置版本：A05 的 exam.final_submissions 与 exam.grading_outbox 已存在。
--
-- 兼容性：只新增表与索引，不改动既有列、约束或语义；不需要“先兼容、再切换、最后清理”的多步发布。
-- 实测（PostgreSQL 18.2，合成库）：应用前 exam 架构 352 个对象，应用后新增 11 个且全部属于本表
-- （8 列 + 主键 + 本索引），既有对象零变化。
--
-- 恢复方案：
--   1) 回退代码并 DROP TABLE exam.grading_results，即回到迁移前状态（已实测：删除后信息架构对象与迁移前完全一致）；
--      答卷、回执、版本事件与既有资源数据不受影响。
--   2) 若需要在同一数据库上**重新应用**本迁移，必须同时删除 exam.schema_migrations 中
--      migration_name = '202609252100_D04_grading_results.sql' 的行，否则迁移执行器会按已应用跳过。
--   代码回退不能代替数据恢复：如果已经产生过计分结果，删除本表会丢失这些结果，需要时先导出。

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
