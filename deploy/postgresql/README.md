# PostgreSQL 18 单节点与主备部署基线

正式业务以活动节点本机可靠落盘为最低成功条件。只部署一台或备机失联时业务继续，另一台可用后补齐 WAL 和版本化资源并取得独立持久确认。数据库使用 synchronous_commit=local、空的 synchronous_standby_names，并保持 fsync=on、full_page_writes=on。仓库只保存通用模板，不保存实际地址、口令、证书、数据库或备份。

## 配置顺序

1. 首台主机安装 PostgreSQL 18，准备服务账户、数据库、资源和确认目录及适用的归档。只部署一台时配置一个受管节点；双机时第二台使用相同 PostgreSQL 18 补丁版本和独立目录。
2. 用实际局域网地址替换 `{{...}}` 占位符。复制 `primary/postgresql.conf.fragment` 和 `pg_hba.conf.fragment` 的有效项到主库配置；建立专用 `REPLICATION LOGIN` 角色，口令放入 Windows 受保护配置或服务账户的 `pgpass.conf`，不能写进本仓库。
3. 重启主库后确认归档命令成功，在服务账户受保护环境设置 `PGSSLMODE=verify-full`、`PGSSLROOTCERT` 和 `pgpass.conf`，再在空的备用数据目录运行 `scripts/Initialize-Standby.ps1`，传入从已核实部署取得的 `ExpectedSystemIdentifier`。脚本核对主库身份、角色和 PostgreSQL 18 补丁版本，不会删除或覆盖旧主机数据；失败保留临时数据和复制槽，不自动删槽。
4. 将 `standby/postgresql.conf.fragment` 的内容并入备用库配置，确认 `standby.signal`、`primary_conninfo`、复制槽和证书/题包密钥材料齐备后启动备用服务。
5. 运行 `scripts/Test-ReplicationReadiness.ps1`，显式传入预期集群标识；连接 URI 必须使用 `sslmode=verify-full` 且不包含口令。检查角色、同一集群/数据库/补丁版本、精确流复制备用名、接收器主机/槽/时间线、采样落盘边界、归档回执和有限复制槽保留量。它是双机复制的时间点检查，不授权提升。单节点运行无需把此双机检查作为业务启动条件；管理页另外显示本机健康与实际保护状态。

## 故障和接管边界

- 心跳丢失不等于主库已经停止。提升备用库前必须从网络、存储或电源层确认原主被隔离，避免双主。
- 接管调用 PostgreSQL `pg_promote` 后必须递增 `serverEpoch`，客户端拒绝旧代次。旧主恢复时保持隔离，只能以新主为源在新的空目录重建，不能直接启动旧数据目录。
- 本机持久提交不会等待失联备机。应用对健康备机另行核对数据库重放与资源持久确认；仅本机成功时明确显示单节点状态。已有 PostgreSQL 配置应按新模板更新，应用自身连接也显式使用 local，避免旧同步参数阻断事务。
- 取消单机应急入口。当前主机自动继续服务；接管采用方案 A，确认旧主已关机或隔离全部业务网络后，在服务器管理点击“接管为主服务器”。单份期间新数据可能暂时只有一份，补齐前剩余节点再次损坏可能丢失这段数据。

## 备份与恢复

`PostgresBackupCoordinator` 调用 PostgreSQL 18 的 `pg_basebackup`，同时复制版本化加密资源和受保护配置。恢复先用 `pg_verifybackup` 校验并复制到隔离暂存目录，不会覆盖正在运行的数据目录。管理员检查清单、时间点、题包和密钥材料后，停止服务并按 PostgreSQL 官方流程切换数据目录；旧库先保留为故障原件。

应用配置通过环境或受保护配置注入，最少包括：

```text
ConnectionStrings__Exam
ExamServer__ServerId
ExamServer__NodeId
ExamServer__ServerEpoch
HighAvailability__RequiredNodeIds__0
HighAvailability__RequiredNodeIds__1   # 仅双节点部署
Database__ApplyMigrations=false
Database__MaximumPoolSize
Database__Capacity__PostgresMaxConnections
Database__Capacity__PostgresReservedConnections
Database__Capacity__MaximumServerProcesses
Database__Capacity__OtherConnections
Database__Capacity__OperationsReserveConnections
Backup__Enabled=true
Backup__PostgresBinDirectory
Backup__AllowedBackupRoot
Backup__RestoreStagingRoot
Backup__VersionedResourcesDirectory
Backup__ProtectedConfigurationDirectory
```

首次空主库在完成迁移后，可由选定首台设置 HighAvailability__BootstrapPrimary=true 建立第一代身份；已有状态不改写，已有业务数据或备库拒绝初始化。第二台从首台复制，禁止各自初始化为主库。完成后关闭此开关。迁移只在维护窗口由一个节点显式启用一次，考试期间保持 `Database__ApplyMigrations=false`。

连接池上限必须按同库全部服务进程计算，并额外保留其他工具和运维连接。生产启动会核对数据库实际连接上限与声明预算；缺少上述容量配置或预算不符会阻止启动。配置含义、算术样例、队列保护和受考务身份保护的运行快照见[容量与运行观测](../../docs/operations/capacity-observability.md)。CI 的 16 连接池和开发回退值均不代表生产建议。

## 尚需真实环境完成的验收

2026-10-18 主备审计修复、最小必要确认策略、升级注意事项和隔离验证结果见 [主备审计修复记录](../../docs/operations/ha-audit-repair-20261018.md)。

本目录能在单机上检查配置、脚本参数和数据库行为，但不能代替两台独立 Windows 主机。S20/S21 必须在实际网络上记录隔离方式、复制等待、接管耗时、客户端恢复、旧主重建和 300 会话数据一致性；没有该记录时不得把“双机接管已通过”写入发布结论。

配置生成、统一空间/复制监测、调用既有 本机管理/主备管理 管理命令、保留策略及失败处置见 [部署维护 运维入口](../../docs/operations/postgresql-maintenance.md)。归档部署需要复制完整 `scripts` 目录，包含共享 `Postgresql.Operations.psm1`；旧脚本调用增加集群标识/TLS 参数要求，不能只更新单个文件。
