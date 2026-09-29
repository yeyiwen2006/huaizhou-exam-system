# F02 主备同步与故障接管：实施边界

日期：2026-09-29。任务：[F02 #8](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/8)，主责 F，代码审查与合并由 A 负责。依据：[需求](../requirements.md)、[执行指南](../agent-implementation-guide.md)、[接管运行边界](backup-and-failover-runbook.md)及 [ADR 0002](decisions/0002-local-management-access.md)。本文件使用合成节点名，不含现场数据。

## 当前基线与缺口

`origin/main@8c6e63c` 已有 `FailoverCoordinator`、`PostgresFailoverStateStore`、`PostgresFailoverPlatform`、`ActiveNodeGuard` 和 PostgreSQL 同步复制部署模板。协调器只在内存中保存请求去重与审计；存储器不保存 `requestId` 或操作阶段；平台提升数据库后，协调器才改变内存代次。若进程在这两步之间退出，重试不能辨认已提升的库，可能重复提升或漏记代次。当前也没有管理 API/CLI 调用该协调器，没有跨机资源与解锁材料核对后的开放写入闭环，不能称为 F02 已完成。

`ActiveNodeGuard` 只核对本机 PostgreSQL 的 `server_state`。旧主与新主分区后，旧主本机仍可能保有旧状态；必须先以主机关机或业务网络隔离切断旧主写入。代次不是隔离措施，心跳丢失不能触发自动提升。

当前节点探针与 `PostgresFailoverPlatform` 都要求备用库的 WAL 接收器在最近 10 秒内持续收到原主消息。若先按要求关闭原主并在 10 秒后发起接管，原代码会拒绝提升，即使备用库已经重放所有已接收 WAL。要区分“正常双机时复制链路健康”与“原主已隔离后的本地重放完成”，并保留人工隔离证明；现有 `NodeState.ReplayLagBytes` 不能单独表达两种条件。此处涉及协调器和共享状态语义，需随 A 确认后的核心改动一起处理，不能通过简单放宽提升条件绕过隔离核对。

F01 独立管理宿主目前在未合并分支，F02 从最新主线独立开发，不把 F01 未合并代码作为 PR 基线。F02 管理入口接线须在 F01 合入或显式协调后完成。

## 待 A 确认的关键变更

1. 在受 A 负责的 `Exam.Application/HighAvailability`、`Exam.Contracts` 和 `Exam.Server` 中接入持久接管状态机、管理请求与业务门禁；复用 F01 的独立管理鉴权和操作记录，不让备机入口依赖旧主数据库。
2. 接管前复用 F01 独立管理宿主在备机本地受保护目录的操作记录，绑定 `requestId`、请求内容、已认证操作者、目标节点与阶段；此记录不依赖可能不可用的旧主数据库或只读备库。数据库仍是主备权威状态。若现有 `server_state`/`failover_audit` 无法满足提升后的跨进程幂等与审计，再提出最小兼容迁移，由 A 确认具体表结构、唯一约束和恢复方案；不能预先假定迁移是唯一实现。
3. 提升前：在本机持久受理意图，确认原主隔离、备用库角色及重放位置、证书、加密题包、解锁材料、服务身份和版本。无法确认任一项就停止，不返回成功。
4. 提升后中断恢复：重新连接目标库核对 `pg_is_in_recovery()`、目标节点和本机已记录意图；若已提升，不再次调用 `pg_promote`。在新主库单个事务内递增代次、更新活动节点及必要的权威操作记录，之后恢复定时事件并通过启动检查，最后才开放业务写入。无法确定提升结果时保持待核对，不猜测成功。
5. 单机应急只能由已认证考务人员明确启用并持久审计，持续显示风险；恢复双机确认前不能标示同步保护。旧主回归保持隔离，只能从新主建立新的备用副本，不能直接启动旧数据目录。

迁移须按[数据库迁移约定](database-migrations.md)提交升级、兼容与恢复说明。F01 #7 的 ADR 0002 只批准 F01 的管理接线，明确不包含新增数据库迁移；本轮尚未找到 A 对上述 F02 范围的确认，不在确认前改动这些目录或迁移。

## 模块验证与接线

在合成环境验证相同 `requestId` 重试、同标识不同内容、受理前后/提升前后/事务提交前后的崩溃恢复、并发接管、不可确认隔离、复制落后、重放暂停、资源或证书缺失、原主回归、应急退出和备库重建。PostgreSQL 实库测试使用 `Category=Postgres`；真实双机隔离、60 秒接管和客户端 10 秒恢复留给 T02 现场记录，不以同机模拟代替。

接线核对业务写入、交卷回执、可靠指令和资源解锁在同步复制确认前的返回语义；核对 F01 独立管理宿主与 F03 学生端身份/代次识别接口。开发交付前实跑锁定还原、Release 构建及 Release 测试，并在 PR 中分别记录适用环境和未执行项。

## 本轮独立安全修正与验证记录

本轮只在基础设施层增加两处拒绝条件：备库暂停 WAL 重放时，健康探针不再报告零延迟且提升前检查拒绝；暂存重建在创建目录前拒绝未知、身份不符的源/目标节点及源、目标相同的请求。没有修改受 A 负责的目录、公共契约或迁移，也没有接通接管业务流程。

macOS / .NET 10.0.401：`TMPDIR=/private/tmp/ dotnet test tests/Unit/Exam.UnitTests/Exam.UnitTests.csproj --no-restore --configuration Release --filter FullyQualifiedName~StagedPostgresNodeRebuilderTests` 通过 4/4；随后 `TMPDIR=/private/tmp/ dotnet test tests/Unit/Exam.UnitTests/Exam.UnitTests.csproj --no-build --configuration Release --filter 'Category!=Postgres&Category!=Latex'` 通过 1159/1159。默认 macOS 临时目录经 `/var` 符号链接；不设置 `TMPDIR` 时，同一单元项目有 78 项路径保护相关失败，不能记录为通过。

仓库规定的解决方案三项命令已尝试：`dotnet restore Huaizhou.ExamSystem.sln --locked-mode` 因 Windows 目标在 macOS 上返回 `NETSDK1100`；随后 `dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release` 返回 `NETSDK1100` 和未还原项目的 `NETSDK1004`；`dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release` 报告缺少 XeLaTeX 及默认临时目录符号链接引发的测试失败，未取得全通过。以上不代表 Windows、PostgreSQL 或双机接管验证通过。暂停重放检查的实库行为亦未在本机验证，须在 `Category=Postgres` 环境补测。
