-- C05 源材料归档：每个已发布试卷版本保存一份原始材料（清单、题目 .tex、共享样式、图片、独立答案）。
-- 用途：后续修题从这里取回材料；服务器据此核对「实际变更题号 vs 声明变更题号」。
--
-- 只新增表，不修改既有数据。写入与试卷/规则版本发布在**同一事务**里，因此不会出现
-- 「版本发布了但没有归档」或「有归档但版本没发布」的半套状态；主键保证同一版本不会产生
-- 第二份内容不同的归档（重试由发布请求幂等表在写入之前拦下）。
-- 按决定不做自动删除：每个已发布版本的源材料都保留。
-- 数据在 Postgres，随流复制到备机，因此接管后仍可按版本取回。

CREATE TABLE IF NOT EXISTS exam.paper_materials (
  exam_id text NOT NULL,
  paper_version bigint NOT NULL CHECK (paper_version > 0),
  archive bytea NOT NULL,
  archive_sha256 text NOT NULL,
  package_content_sha256 text NOT NULL,
  format_version int NOT NULL,
  file_count int NOT NULL,
  archive_bytes int NOT NULL,
  question_digests jsonb NOT NULL,
  created_at timestamptz NOT NULL,
  created_by text NOT NULL,
  PRIMARY KEY (exam_id, paper_version));
