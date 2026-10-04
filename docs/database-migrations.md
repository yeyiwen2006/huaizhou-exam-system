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

- `202610031000_C05_paper_materials.sql`：源材料归档（按考试与试卷版本保存归档字节、哈希与逐题摘要）。
  只新增表，不改既有数据；主键 `(exam_id, paper_version)` 保证同一版本不会产生第二份不同内容的归档。
  写入发生在发布事务内，与资源、试卷/规则版本、事件和投影一起提交或回滚；按决定不做自动删除。
  兼容性：该表为空时既有行为不变；本功能上线前发布的考试没有归档行，恢复核对按「尚未归档」豁免
  （见下），因此升级不需要回填，也不会因旧数据拦住业务写入。
  恢复：数据在 PostgreSQL 中，随流复制到备机，接管后 `GET /exams/{examId}/paper-materials?version=N` 仍可取回；
  核对应由恢复就绪检查的 `paper-materials` 项覆盖。

迁移作为程序集嵌入资源，由 `PostgresMigrationRunner` 在 PostgreSQL advisory lock 下逐项执行，并在 `exam.schema_migrations` 记录名称。生产默认不自动迁移；维护窗口显式设置 `Database__ApplyMigrations=true`，完成后恢复为 `false`。B01/B02 合并前已在 PostgreSQL 18.6 随机隔离数据库中应用全部迁移并通过 36 项实库测试；这是已有验证记录，本轮进度更新未重新运行迁移。环境和边界见 [B01/B02 复核记录](b01-b02-review-verification.md)。

## 集成测试的数据库连接容量

2026-09-22，B04 合并后主分支的 PostgreSQL 检查在 300 个并发激活审批时失败。数据库日志出现 `too many clients already`，随后客户端获取连接超时；Windows 构建和其余 35 项 PostgreSQL 测试通过。记录见 [失败运行](https://github.com/yeyiwen2006/huaizhou-exam-system/actions/runs/35710590447)。

测试夹具现在将单个共享连接池限制为最多 16 条连接，超过容量的请求由连接池排队。300 个并发申请及 300 个并发审批的原测试保持不变，也未增加连接超时、语句超时或数据库连接上限。这样可为独立的健康检查和运维连接留出余量。驱动默认连接池上限为 100，不能直接把它视为整台数据库可全部提供给单个池的容量，参见 [Npgsql 连接池参数](https://www.npgsql.org/doc/connection-string-parameters.html#pooling) 和 [PostgreSQL 连接容量](https://www.postgresql.org/docs/18/runtime-config-connection.html#RUNTIME-CONFIG-CONNECTION-SETTINGS)。

`PostgresConnectionPoolTests` 使用真实数据库填满这 16 个池内连接，并验证额外运维连接仍能建立、池内第 17 个请求等待、取消等待不泄漏连接、释放连接后后续请求恢复。它用于防止测试夹具意外退回默认连接配置，不代替 300 台终端的现场性能验收。

该限制仅用于随机隔离数据库的集成测试，不修改生产连接配置、数据库架构或业务事务。生产部署仍须结合数据库总连接容量、其他进程和运维余量明确配置各进程的连接池大小，不能把本次 CI 通过作为生产容量验收。
