# A 组实施状态

> 2026-09-24任务归属更新：旧A07/A08、B08、C07分别接续为F01/F02、F03、F04，原E07的产品工具为F05；整体测试及修复现归T01/T02，基础设施归E01。当前职责与前置以[任务映射](task-map.md)、[测试安排](testing-plan.md)及对应Issue为准。下文保留原日期的实现和实测记录，旧编号、审查方式和后续安排只作历史追溯，不代表本次重跑或新轮次通过。

复核日期：2026-09-23。代码依据为本轮容量/A05 变更集与已合并 D01 的最新 `main`；Release 构建、普通测试、真实 PostgreSQL 集成及本机 300 会话基线均在本轮重新执行。本文区分服务端代码完成、单机容量基线和仍需双机/现场执行的验收，不用本机结果代替正式环境结论。

| 任务与 Issue | 模块状态 | 结论 |
| --- | --- | --- |
| [A01 #1](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/1) | 已完成，关闭 | 架构、公共契约、样例、协作与自动检查入口已交付，不因未来所有模块尚未完成而继续挂起 |
| [A02 #2](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/2) | 已完成，保持关闭 | 激活编号、正式接口、PostgreSQL 并发与恢复测试已完成 |
| [A03 #3](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/3) | 已完成，保持关闭 | 参试与授权事务已完成，B01/B02 真实接入及截止后恢复修正已合并 |
| [A04 #4](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/4) | 已完成，关闭 | 持久指令、真实推送补发、受授权解锁及 B02 消费已接通 |
| [A05 #5](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/5) | 已完成，关闭 | 权威实际名单、自动持久检查、可恢复收齐事件及考务补取 API 已交付；现场通知时限仍由 E04/E05 验收 |
| [A06 #6](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/6) | 已完成，保持关闭 | 时间与资源版本发布、并发检查、通知原子性及终态保护已完成 |
| [A07 #7](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/7) | 部分完成，继续打开 | 缺独立 Windows 管理宿主、完整启动自检与完整恢复运行验证 |
| [A08 #8](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/8) | 部分完成，继续打开 | 接管协调器尚未接入持久化和管理 API/CLI，尚未完成真实双机闭环 |

关闭 A01/A04 不表示全部成员已完成本机联调，也不宣布 M0–M5 整体验收通过。B/C/D 的功能和 E 的集成、性能及场地演练继续在原任务中跟踪，不重复作为已完成 A 模块的未完成项。

## 逐项复核及具体待办

### A01 架构、契约与开发组织

**已完成，关闭。** 共同仓库、三端和 CLI 解决方案、项目依赖方向、SDK/依赖锁定、响应与错误契约、事件及版本字段、合成样例、迁移编号、任务卡、PR 模板、审查安排和联调批次均已建立。B01/B02 已使用公共契约完成实际接入，主分支完整构建和测试通过。

证据：`Huaizhou.ExamSystem.sln`、`global.json`、`Directory.Build.props`、`src/Exam.Contracts`、`samples/contracts`、`samples/synthetic/minimal`、`.github/workflows/build.yml`、`docs/architecture.md`、`docs/database-migrations.md`、`docs/integration-sequence.md`、`tests/Unit/Exam.UnitTests/Common/ContractSerializationTests.cs`。

本卡没有尚未交付的基础架构待办。持续协调和未来新增契约属于后续开发管理；完整 GUI/CLI、D01 样包深化及 E04/E05/E07 验收仍按各自任务执行，不将“所有后续模块完成”追加为 A01 的关闭条件。

### A02 激活与编号

**已完成，保持关闭。** PostgreSQL 事务持久化、每日序号锁、凭据保护、幂等审批、撤销及保号再激活、API/CLI 已交付。已有实库 300 并发、跨日、999 上限和服务重建恢复记录，B01 学生端已接入。

证据：`tests/Integration/Exam.IntegrationTests/Activation/PostgresActivationPersistenceTests.cs`、`src/Exam.Infrastructure/Postgres/Activation/PostgresActivationPersistence.cs`。C03 管理页面和 E05 现场负载复核由原任务跟踪，不重复挂在 A02。

### A03 参试与设备授权

**已完成，保持关闭。** 名册核对、准备与确认分离、等待退出、唯一参试及设备裁决、授权版本检查均已接入 PostgreSQL。PR #37 移除公开 finalize 旁路；PR #38 已完成真实客户端接入，修正实际参试者截止后恢复、确认重放读取当前状态及已结束不返回解锁引用。

证据：`src/Exam.Application/Participation/ParticipationService.cs`、`tests/Integration/Exam.IntegrationTests/Participation/PostgresParticipationPersistenceTests.cs`、`tests/Unit/Exam.UnitTests/Participation/ParticipationServiceTests.cs`。C02 名册维护和 B05 完整交卷客户端仍独立跟踪；不再写“B02 接入中”。

### A04 时间和可靠指令

**已完成，关闭。** 服务端校时、PostgreSQL 持久事件、独占投递租约、退避重发、人工补发、幂等确认、重连补查和到期开始/结束已交付。PR #38 已接通真实 WSS、统一题包解锁及客户端终态处理；解锁事务拒绝未开考、旧授权、错版本、错代次和超过截止时间的请求。结束事件确认与最终答卷及收齐记录分别保存，不能把 ACK 当成交卷成功。

证据：`tests/Unit/Exam.UnitTests/Commands/ReliableCommandServiceTests.cs`、`tests/Integration/Exam.IntegrationTests/Commands/PostgresCommandEventStoreTests.cs`、`tests/Integration/Exam.IntegrationTests/Resources/PostgresPackageUnlockServiceTests.cs`、`tests/Unit/Exam.StudentUnitTests/SignalRExamCommandFeedTests.cs`。记录包括丢通知补发、同事件重放、持久租约恢复、授权撤销、截止竞态，以及慢 START 不阻挡 END/撤销的真实 Hub 回归。

本卡没有已识别的未实现指令基础服务。D03 正式组包发布、B05 自动交卷、B06 资源更新应用由对应任务负责；300 会话、丢包及服务重启后的时限实测由 E04/E05 跟踪，未宣称这些整体场景已通过。A05 自动收齐链路已在本轮补齐；300 会话与双机/现场时限仍由 E04/E05 跟踪。

### A05 进度与最终交卷

**已完成，关闭。** 进度版本保护、最终答卷/永久回执/参试终态/幂等结果/计分 outbox 同事务提交，以及权威实际名单驱动的自动收齐和可恢复通知出口均已接通 PostgreSQL。兼容的 `/api/v1/submissions/collection/evaluate` 不再信任调用方提供的名单或版本；生产判断只读取数据库中的实际参试事实。

实际名单变化与最终答卷事务会在同一数据库事务中更新名单版本或写入 `collection_pending_checks`。`CollectionCompletionWorker` 仅在活动节点运行，按有界批次恢复并处理未决检查；每场检查在同一 SQL 快照中读取当前权威名单版本、实际参试者及最终答卷，零人不产生收齐事件。完成事实与可重放通知保存在 `collection_completion_events`，`(exam_id, roster_version)` 保证同一名单版本最多一个业务事件；并发名单变化会重新留下待检查记录，不因 worker 清理而丢失唤醒。

考务端可使用现有管理员认证调用 `GET /api/v1/submissions/collection/events?examId=...&afterRosterVersion=...` 按版本补取。返回前会写 `delivery_fence_at` 并提交，形成当前数据库持久策略下的 WAL 提交屏障；服务端不以“已发送”为由删除事件，消费端可保存游标并按事件标识去重。具体恢复语义见 `docs/a05-collection-recovery.md`。

回归证据包括伪造精简名单、最后两份并发提交、等待退出、零人、新参试者形成新名单版本、outbox/通知事务失败回滚、worker 重启恢复、并发队列清理不丢唤醒，以及真实 HTTP 管理员认证、游标和提交屏障。本轮本机 300 会话基线还独立核对了 300 个实际参试者、300 份最终答卷、300 个唯一回执、300 个计分任务、1 条权威收齐事件、0 条待检查，答案结构全部一致。

A05 的服务端关闭不表示 C04/C06 弹窗展示、双机同步持久化或现场 2 秒通知时限已经通过。`collectionPersistedAfterLastReceiptTimestampMs` 只比较数据库处理时间戳，不是提交完成或考务 GUI 展示时间；这些继续由 E04/E05/E07 在目标双机、独立负载机和现场网络中验收。

### A06 时间与资源版本发布

**已完成，保持关闭。** PostgreSQL 事件流和投影、请求幂等、版本冲突、题包发布与通知同事务、旧结束任务失效、资源防回退及已交卷/已结束不可重开已有实现和实库测试。

证据：`tests/Unit/Exam.UnitTests/Versioning/ExamVersioningServiceTests.cs`、`tests/Integration/Exam.IntegrationTests/Versioning/PostgresExamVersioningServiceTests.cs`、`tests/Integration/Exam.IntegrationTests/Resources/PostgresVersionedResourceStoreTests.cs`。B02 已补版本化公开说明服务；C01 结构化说明生成、D03/D05 资源生产与 B06 更新应用仍按其任务跟踪。

### A07 服务管理与备份

**部分完成，继续打开。** 原生基础备份、验证工具调用、密文资源和受保护配置同包、路径限制、隔离暂存恢复、备份/恢复请求跨进程幂等及运行中 API/通用 CLI 已实现。已有一次真实基础备份工具验证，但不等于恢复后完整业务已运行通过。

当前代码位置：`src/Exam.Application/ServiceManagement/ServiceManagementService.cs` 声明 `IManagedBusinessHost`、`IStartupReadinessProbe`；生产代码尚无完整 Windows 宿主适配。`src/Exam.Server/Program.cs` 的备份/恢复端点依赖业务服务在线；`PostgresBackupCoordinator.RestoreAsync` 目前恢复到隔离暂存目录，不负责完整启动恢复后的服务。

- [ ] **独立服务管理入口。** 实现真实 Windows 进程/服务启停适配及业务进程之外的管理宿主，让业务服务已停止或启动失败时仍能查询、配置和启动。GUI 与 CLI 调用同一管理模块，不能依赖向已停止的业务 HTTP 服务发请求。
- [ ] **持久配置与完整启动自检。** 接通配置保存和启动检查，覆盖数据库角色/代次/迁移、证书与私钥可用性、资源可读性、目录权限和磁盘、未完成事件/定时恢复；关键项失败保持业务关闭，管理入口仍可用，输出具体失败原因。现有 readiness 仅覆盖部分数据库与节点检查。
- [ ] **完成恢复运行闭环。** 在隔离 PostgreSQL 实例加载恢复材料并启动业务，验证编号接续、原回执、最终答卷、题包、事件与定时任务一致，且可继续收卷。不能只用“文件已复制到暂存目录”认定恢复完成，也不能覆盖现用数据库进行演练。
- [ ] **补部署恢复证据。** 与 E06/E07 验证基础备份加连续 WAL 归档的实际恢复，核对受保护配置和节点私钥在新服务账户/主机上的可用性。当前尚无长期归档与全新 Windows 主机完整恢复记录，记录环境、故障点及结果后才能勾选。

**关闭条件：** 服务停止后管理入口仍可启动真实业务，关键自检生效，完成隔离恢复后继续收卷的验证。C06 界面布局/操作体验归 C，E 的现场执行归 E；A 的宿主和恢复能力不能因已有备份工具而提前关闭。

### A08 主备服务与接管

**部分完成，继续打开。** 已有 PostgreSQL 主备状态存储、活动节点门禁、健康探测、提升/空目录重建适配、同步复制及 WAL 脚本。题包采用共享 AES-GCM 格式及分节点证书封装，已做本机双节点证书和数据库资源往返测试；这不是两台 Windows 主机切换验收。

当前代码位置：`src/Exam.Application/HighAvailability/FailoverCoordinator.cs` 的请求结果、节点、审计和操作状态仍由进程内集合维护；`PostgresFailoverStateStore` 已存在，但未与协调器完整接通。`PostgresRuntimeRegistration` 尚未注册完整接管协调/平台调用链，服务端和 CLI 尚无完整接管管理入口。

- [ ] **接通持久接管流程。** 将接管请求、步骤、隔离证明、操作者、服务代次及审计接入 PostgreSQL，特别处理“数据库已提升但应用状态尚未更新”时崩溃。重启重放原 requestId 能恢复确定结果，不能重复提升、漏记代次或开放旧主写入。
- [ ] **交付管理 API/CLI。** 提供节点状态/配对检查、隔离确认、接管、单机应急、旧主重建及恢复同步模式的正式管理入口，复用同一持久状态机并接通真实平台适配。备端管理入口不能依赖旧主在线；非法版本、身份或隔离条件明确拒绝。C07 接页面，不另写一套接管规则。
- [ ] **建立真实双机配置并验证密钥。** 准备两台独立 Windows 主机、地址、服务账户和证书，在实际同步复制下验证正常成功确认受备库持久应用约束，备机故障不会静默降级；新主用自己的私钥解开同一已发布题包。不能用同机临时证书测试代替。
- [ ] **完成最小故障闭环。** A 与 E07 执行旧主隔离、备库提升、代次更新、事件/定时恢复、继续收卷、旧主拒绝写入、从新主重建并恢复同步；对每个中断点记录恢复结果。接管 60 秒、客户端恢复 10 秒及 300 会话跨机指标分别与 B08/E05/E07 联测，尚未执行的保留未通过状态。

**关闭条件：** 持久管理流程及正式入口实际可用，并有最小真实双机接管/恢复证据。目前不只是缺第二台主机，前两项仍是明确的软件实现缺口。

## 本轮核对方法

本轮修改了 A05 自动收齐、生产容量控制与观测、负载工具及回归测试，并使用全新隔离数据库执行本机 300 会话基线。Release 构建为 0 警告、0 错误；合并最新 D01 后，非 PostgreSQL 分类 264/264、真实 PostgreSQL 分类 47/47，合计 311 项自动测试 0 失败、0 跳过；负载工具另完成 300 会话本机基线与只读数据库审计。以下历史基础版本数据保留为原始记录，不当作最新计数。

## A 组基础版本验证

- .NET Release 全解决方案构建：0 警告、0 错误。
- 普通单元测试：82/82。
- 非 PostgreSQL 集成测试：7/7，包含正式 API 映射和实时连接授权元组校验。
- PostgreSQL 18.6 真实数据库集成：22/22，覆盖 A02 至 A06、事务内设备/资源复核、确认参试前的准备记录下载授权、目标化指令租约、自动开始结束、加密题包数据库往返、双节点证书解密、主备状态恢复以及备份/恢复请求跨进程幂等恢复。
- 生产模式进程实际连接随机隔离数据库：权威主备状态未初始化时 `/health/ready` 返回 503；初始化后返回当前 serverId/nodeId/serverEpoch。通用 CLI 获取时间并创建版本考试成功，伪造与令牌绑定身份不一致的操作者字段返回 403。
- PostgreSQL 18.6 `pg_basebackup` 和 `pg_verifybackup` 对本机集群完成一次真实基础备份及校验；临时验证目录已清理。
- PostgreSQL 部署脚本全部通过 PowerShell 语法解析；WAL 归档重复调用、恢复及 SHA-256 内容往返在本机通过，临时目录已清理。

以上是单机实现与测试证据，不等于正式 Windows 发布包、现场网络、300 台设备或双机故障演练通过。当前也不能把“主备接管管理面”“跨机题包解锁”“完整现场自检”和“服务停止时仍可操作的 Windows 管理宿主”写成完成。

任何未列为完成的集成或现场场景都不得在报告中写成通过。生产启动不会退回内存存储；测试内存适配器不能用于正式考试。

## B01/B02 接入后的修订

本轮增加自助取消激活、真实考试概览、版本化公开说明及授权解锁接口，移除绕过正式交卷事务的公开 finalize 路由；修正已有实际参试者在首次截止后恢复的规则。确认请求重放返回当前状态及 Lifecycle，结束后不返回可继续作答的解锁信息。

A01、A02、A03、A04、A06 已按各自模块交付范围关闭任务卡。A05 服务端自动收齐链路已在本轮完成；A07、A08 的具体实现缺口、待验证项及关闭条件见上文。A05 的现场展示与时限不作为服务端代码缺口回退，继续由对应 E/C 任务验收。B01、B02 已合并，不能沿用“B02 接入中”或“只有内存参考实现”的描述。
