# PostgreSQL 18 双机部署基线

本目录归F02/F05产品主备与部署运维实现，E01据此搭建隔离测试设施；两台独立Windows主机的整项目验收在T02由全员执行。开发期间持续进行配置、脚本与数据库模块测试。配置目标是：主库只有在备用库已经重放事务后才向业务返回提交成功；备用库失联时写入会等待或失败，不会静默退回“仅主机落盘”。仓库只保存模板和只读检查脚本，不保存地址、口令、证书、数据库目录或备份。

## 配置顺序

1. 两台主机安装完全相同的 PostgreSQL 18 补丁版本，并创建独立的 Windows 服务账户、数据库数据盘、WAL 归档盘和防火墙规则。
2. 用实际局域网地址替换 `{{...}}` 占位符。复制 `primary/postgresql.conf.fragment` 和 `pg_hba.conf.fragment` 的有效项到主库配置；建立专用 `REPLICATION LOGIN` 角色，口令放入 Windows 受保护配置或服务账户的 `pgpass.conf`，不能写进本仓库。
3. 重启主库后确认归档命令成功，再在空的备用数据目录运行 `scripts/Initialize-Standby.ps1`。脚本只允许空目录，不会删除或覆盖旧主机数据。
4. 将 `standby/postgresql.conf.fragment` 的内容并入备用库配置，确认 `standby.signal`、`primary_conninfo`、复制槽和证书/题包密钥材料齐备后启动备用服务。
5. 运行 `scripts/Test-ReplicationReadiness.ps1`。只有主库显示 `synchronous_commit=remote_apply`、同步备用名正确、专用物理复制槽仍可用且超过现场指定的 `-MinimumSafeWalBytes` WAL 安全余量，备用库处于 recovery 并已重放收到的 WAL，才能进入双机保护模式。将脚本输出保存到仓库外的受保护运维记录；非零退出码阻止标记为双机保护。

## WAL 容量与持续监测

主库模板必须把 `{{MAX_SLOT_WAL_KEEP_SIZE}}` 替换为正数容量，例如 `8GB`；不能保留默认的无限制值 `-1`。按考试期间实测 WAL 速率、允许的备机中断时长、`wal_keep_size`、基础备份窗口及数据盘可用空间确定具体上限和 `-MinimumSafeWalBytes` 阈值，并经 A 审查后记录在受保护的现场配置中。该上限只约束复制槽在检查点时保留 WAL 的量，不限制尚未成功归档的 WAL；归档目录及备份目录需要单独的容量告警和受控清理策略，禁止自动删掉仍需恢复的文件。

每次启动及运行期间定期检查 `Test-ReplicationReadiness.ps1`、`Test-StorageCapacity.ps1` 的 JSON 结果、主库 `pg_replication_slots.wal_status/safe_wal_size`、`pg_stat_archiver`、`pg_wal`/归档盘/备份盘可用空间，并记录时间、主备身份和实际代次。`wal_status=unreserved/lost`、`safe_wal_size` 耗尽、归档持续失败或空间不足时持续告警，阻止宣称双机保护；查明缺失的 WAL 后通过受控基础备份重建备用库，不直接复用失效的复制槽。不要仅根据一次检查结果自动提升备机或删除 WAL。PostgreSQL 18 的 [复制槽状态](https://www.postgresql.org/docs/18/view-pg-replication-slots.html) 与 [容量设置](https://www.postgresql.org/docs/18/runtime-config-replication.html)说明了上限及失效状态。

`Test-StorageCapacity.ps1` 只检查已存在的本机数据、归档、备份目录所在卷的可用字节数，`MinimumFreeBytes` 按现场写入速率和恢复窗口确定，任一目录缺失、路径经过重解析点、卷不可读或空间不足均返回非零。它不会自动清理文件，不代替归档成功检查。两项脚本均为只读检查；macOS 无 PowerShell 与 PostgreSQL 18 运行环境，本轮只完成脚本和静态检查。Windows 模块验证需在隔离主备上分别覆盖健康、复制槽丢失或 `unreserved`、`max_slot_wal_keep_size=-1`、重放暂停及备用断连，核对 JSON 的 `ready/errors` 和非零退出码。WAL 归档与恢复脚本需在 Windows PowerShell 5.1 使用合成 WAL 文件实测首次归档、重复同内容、同名异内容拒绝、缺失文件、非法文件名和相对归档根拒绝；归档根、源文件和目标文件均使用隔离测试目录，不接触正式数据库。Windows 还需用临时测试目录和高于现有可用容量的阈值验证空间不足路径。生产监测调度、持续告警与自动化重建仍是 F05 后续接线工作。

## 故障和接管边界

- 心跳丢失不等于主库已经停止。提升备用库前必须从网络、存储或电源层确认原主被隔离，避免双主。
- 接管调用 PostgreSQL `pg_promote` 后必须递增 `serverEpoch`，客户端拒绝旧代次。旧主恢复时保持隔离，只能以新主为源在新的空目录重建，不能直接启动旧数据目录。
- `synchronous_commit=remote_apply` 保证的是被主库确认成功的 PostgreSQL 事务已经在同步备用库重放；文件系统中的题包、节点证书和受保护配置必须通过F01备份以及跨节点加密信封同时准备。
- 单机应急需要明确的人工操作、持续告警和审计。它会放弃“双机已确认数据损失为零”的承诺，不能作为正常配置。

## 备份与恢复

`PostgresBackupCoordinator` 调用 PostgreSQL 18 的 `pg_basebackup`，同时复制版本化加密资源和受保护配置。恢复先用 `pg_verifybackup` 校验并复制到隔离暂存目录，不会覆盖正在运行的数据目录。管理员检查清单、时间点、题包和密钥材料后，停止服务并按 PostgreSQL 官方流程切换数据目录；旧库先保留为故障原件。

应用配置通过环境或受保护配置注入，最少包括：

```text
ConnectionStrings__Exam
ExamServer__ServerId
ExamServer__NodeId
ExamServer__ServerEpoch
ExamSecurity__OperatorToken
ExamSecurity__OperatorId
HighAvailability__RequiredNodeIds__0
HighAvailability__RequiredNodeIds__1
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

迁移只在维护窗口由一个节点显式启用一次，考试期间保持 `Database__ApplyMigrations=false`。

连接池上限必须按同库全部服务进程计算，并额外保留其他工具和运维连接。生产启动会核对数据库实际连接上限与声明预算；缺少上述容量配置或预算不符会阻止启动。配置含义、算术样例、队列保护和受考务身份保护的运行快照见[容量与运行观测](../../docs/operations/capacity-observability.md)。CI 的 16 连接池和开发回退值均不代表生产建议。

## 尚需真实环境完成的验收

本目录能在单机上检查配置、脚本参数和数据库行为，但不能代替两台独立 Windows 主机。S20/S21 必须在实际网络上记录隔离方式、复制等待、接管耗时、客户端恢复、旧主重建和 300 会话数据一致性；没有该记录时不得把“双机接管已通过”写入发布结论。
