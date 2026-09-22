# 数据库迁移约定

PostgreSQL 迁移在 `src/Exam.Infrastructure/Migrations` 中按提交顺序保存，文件名采用 `YYYYMMDDHHmm_Axx_short_description.sql`。同一分钟发生冲突时由 A 重新分配时间段；已经进入共享分支或 PR 的迁移不得改名或修改原内容，只能新增后续迁移。

每项迁移同时提供：

- `up` 变更及明确的前置版本；
- 与当前发布版本兼容的恢复方案；
- 唯一约束、索引及事务边界说明；
- 使用合成数据执行的升级、重启和恢复记录。

删除列、改变字段语义、重写历史数据或影响旧程序读取的操作必须拆成“先兼容、再切换、最后清理”的多个发布步骤。代码回退不能代替数据恢复。A 确认兼容与恢复方案后方可合并。

当前迁移按顺序覆盖：

- `202609220100_A02_activation.sql`：设备、激活请求、每日序号和幂等响应。
- `202609220200_A03_participation.sql`：名册、会话、准备、唯一参试事实表、设备冲突和裁决审计。
- `202609220300_A04_reliable_commands.sql`：可靠指令、投递状态和确认请求。
- `202609220400_A05_submissions.sql`：进度、最终答卷、永久回执、计分 outbox 和回收完成事件。
- `202609220501_A06_versioning.sql`：版本事件流、当前投影和版本写请求幂等响应。
- `202609220600_A07_resources_and_service_state.sql`：版本化加密资源、主备状态/审计、备份记录和恢复记录。
- `202609220700_B01_student_exam_metadata.sql`：学生可见的考试名称、类别及科目元数据。
- `202609220800_B02_student_instructions.sql`：公开考试说明历史版本及元数据更新幂等记录。

迁移作为程序集嵌入资源，由 `PostgresMigrationRunner` 在 PostgreSQL advisory lock 下逐项执行，并在 `exam.schema_migrations` 记录名称。生产默认不自动迁移；维护窗口显式设置 `Database__ApplyMigrations=true`，完成后恢复为 `false`。B01/B02 合并前已在 PostgreSQL 18.6 随机隔离数据库中应用全部迁移并通过 36 项实库测试；这是已有验证记录，本轮进度更新未重新运行迁移。环境和边界见 [B01/B02 复核记录](b01-b02-review-verification.md)。
