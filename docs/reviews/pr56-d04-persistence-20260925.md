# PR #56：D04 持久化、任务租约与协调器审查

审查日期：2026-09-25。需求基线为仓库 `requirements.md`；同时核对 A05 收卷/回执、A06 版本事件与 D04 精确计分接口。审查包含作者新增协调器提交 `016f2a3`，不是仅检查最初的持久化提交。`AGENTS.md` 已加入用户指定的审查、测试、接口检查和合并要求原文。

## 发现与修复

| 问题 | 修复与验证 |
| --- | --- |
| 旧消费者可用低版本覆盖新成绩；同版本不同结果也能覆盖 | 内存与 PostgreSQL 均拒绝版本倒退、归属改变和同版本内容冲突；相同内容重试保持首次时间。数据库插入同时校验 A05 最终答卷归属。 |
| 内存适配器持有调用方可变集合 | 成绩与冻结答案使用现有契约的不可变快照，外部修改集合不影响已保存值。 |
| 租约过期并重领后，旧消费者还能结项或释放新租约 | 完成/失败接口改为接收 `PendingGradingTask`；`AttemptCount` 为领取代次，更新时比较代次并要求任务仍在领取且未完成。已完成任务不能被迟到失败污染。 |
| 不同消费者的租期参数可能提前缩短已有租约 | 新增独立迁移 `202609252200_A01_grading_leases.sql`，保存本次领取的绝对到期时间。旧记录缺失到期时间时保留兼容回收路径。 |
| 持续失败的队首任务可能饿死后续答卷 | 未完成任务按尝试次数、创建顺序领取；仍使用 `FOR UPDATE SKIP LOCKED`。失败保留记录，不删除任务。 |
| 两次查询会把旧规则内容标成新版本；跳过无源数据的新事件也会产生错配 | 同一个 SQL 快照读取 A06 版本和对应最新发布事件，校验考试、预期版本和完整源数据；当前事件损坏时明确失败，不回退使用旧内容。 |
| 新协调器没有隔离读取、保存及结项异常 | 每条任务隔离异常；先保存结果再比较领取代次结项；结项失败不能计入成功；失败状态写入失败时仍保留租约恢复。取消继续向上传播。 |
| 协调器可能完成错误答卷或未来版本任务；异常消息可能携带答案/连接信息 | 校验任务与答卷标识、考试和目标版本下界；只记录稳定脱敏业务信息，不输出依赖异常或原始计分输入。 |
| 原 Windows CI 的配置快照并发测试真实失败 | 在既有 5 秒总等待预算内重试完整原子替换，保留取消、原文件保护和临时文件清理；不降低断言、不原地改写配置。 |
| 原 PR 有 xUnit 集合断言警告 | 使用 `Assert.Single`，Release 构建零警告、零错误。 |

## 接口与交付边界

`A05SubmissionAndOutbox_A06Publication_D04LatestExactResult_PreserveFrozenAnswer` 在真实 PostgreSQL 上串联公共适配器：A06 发布规则、A05 保存唯一答卷及回执/outbox、D04 读取最新规则并精确重算。验证旧版本任务可得到新版本结果、小题负分/科目归零、旧写入被拒绝、重试不重复累计，且原答案与原回执不变。

`IGradingTaskSource.CompleteAsync/FailAsync` 的签名和返回值已同步到内存、PostgreSQL、协调器及测试替身。不能再按两个裸标识直接结项。

任务的 `grading_version` 是入队目标下界，不等于实际产出的规则版本。实际版本以 `exam.grading_results.grading_version` 为准。D05 的范围判定必须从全部 `final_submissions` 左联结结果表，不得把已完成的旧 outbox 版本推断为成绩版本。

此 PR 提供持久化和有界协调器，不声称已完成生产后台注册、主备写门接入、规则发布后的持久化重算调度、GUI/CLI、报表查看/导出版本门或整体验收。生产集成必须把这些边界接好；不能仅凭批次成功计数或某次范围快照放行报表。没有执行真实 TeX 编译、300 客户端压力或主备断电演练；本次未改相关实现。

## 本地验证

环境为 Windows、.NET SDK 10.0.401、独立的本机回环 PostgreSQL 18 测试实例。所有数据库用例使用夹具生成的随机合成数据库并清理；未连接现有业务数据库。未更改超时上限或跳过失败用例。

首次加入的三条回归在原代码上均失败：版本倒退、归属改变、集合别名。修复后转为通过。

| 检查 | 结果 |
| --- | --- |
| `dotnet restore Huaizhou.ExamSystem.sln --locked-mode` | 通过，依赖锁文件未变 |
| Release 全解决方案构建 | 0 警告、0 错误 |
| 非 PostgreSQL / 非 Latex / 非 WPF 全量回归 | 1221 通过、0 失败、0 跳过；分项目为 942 / 24 / 24 / 134 / 75 / 22 |
| 隔离测试主机的 WPF 分组 | 2 通过、0 失败、0 跳过 |
| PostgreSQL 全量集成回归 | 66 通过、0 失败、0 跳过 |

命令与 CI 保持相同筛选：

```powershell
dotnet build Huaizhou.ExamSystem.sln --configuration Release --no-restore
dotnet test Huaizhou.ExamSystem.sln --configuration Release --no-build -m:1 --filter "Category!=Postgres&Category!=Latex&Category!=Wpf" --logger "trx;LogFileName=pr56-final.trx" --blame-hang-timeout 90s --blame-hang-dump-type none
dotnet test Huaizhou.ExamSystem.sln --configuration Release --no-build -m:1 --filter "Category=Wpf" --logger "trx;LogFileName=pr56-final-wpf.trx" --blame-hang-timeout 90s --blame-hang-dump-type none
# EXAM_TEST_POSTGRES 由环境提供，仅指向独立合成测试实例。
dotnet test tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --configuration Release --no-build --filter "Category=Postgres" --logger "trx;LogFileName=pr56-final-postgres.trx"
```

测试证据保留在本地忽略目录 `artifacts/pr-review-20260925/` 和各测试项目的 `TestResults/`，不把运行日志或数据库提交到仓库。

## 迁移和恢复

保留已共享的 D04 迁移不改写，后续租约变化使用新增迁移。部署前先停止旧消费者，完成迁移后启动统一版本的消费者；不要混跑不支持领取代次/到期时间的旧代码。

**迁移记账名称不带 `.sql`。** `PostgresMigrationRunner.GetMigrationName` 会去掉扩展名。原 D04 SQL 注释中带 `.sql` 的示例不能直接用来删除记账行；实际名称是 `202609252100_D04_grading_results`，新增租约迁移名称是 `202609252200_A01_grading_leases`。

`MigrationRestartAndReapplicationPreserveFrozenAnswerAndReceipt` 仅在随机测试库演练：迁移重复启动不重复应用；删除派生成绩表并删除正确记账行后可以重建；最终答卷、原回执、outbox 保留。生产优先保留新增空列/索引做代码回退，不应为回退删除成绩。确需恢复派生成绩时，先备份并暂停写入，恢复结构后按当前已发布版本重新调度缺失成绩；已完成的旧任务可能需要显式修复重排，不能只调用 A05 的重复提交去重入口。该恢复过程不得删除或改写原始答卷和回执。

补充稳定性验证：原 Windows CI 失败的配置并发快照用例以独立测试主机连续重复 25 次，25/25 通过。实际测试数据库版本为 PostgreSQL 18.6。
