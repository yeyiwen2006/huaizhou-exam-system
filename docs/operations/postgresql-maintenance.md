# PostgreSQL 主备部署与维护


## 部署配置与账户

两机使用同提交业务/管理/考务/CLI 包、同 PostgreSQL 18 补丁版本，以及相同字体和 TeX 宏包依赖。服务账户与管理员操作账户分开；二进制、配置、工具、CA 与目录祖先由部署账户保护，业务账户不得修改它们。相应服务账户可读取各自节点证书私钥、解锁材料和版本资源；管理账户可控制绑定服务并写自己的审计/暂存/备份目录。不能用复制原机 DPAPI 密文代替异机密钥准备。账户/ACL/私钥访问必须以实际运行账户完成服务管理和主备检查，工具不自动授予权限。

`New-PostgresqlDeployment.ps1` 读取公开 CA 文件、两独立 IP、端口、备用节点应用名、复制槽名和路径，在**全新的输出目录**生成三个可审阅配置片段及规范化的公开 PEM CA；不安装 PostgreSQL、不覆盖现有配置、不创建运行库、不启动服务。拒绝路径链接/联接、非绝对路径、已有输出、模板未替换以及私钥/过期证书文件。复制槽上限参数必须依据实际考试写入量、保留时长及存储容量核定；示例 8192 MB 不是正式容量建议。

```powershell
# 以下是合成路径/IP，实际配置与证书必须放在仓库外。
./deploy/postgresql/scripts/New-PostgresqlDeployment.ps1 `
  -OutputDirectory C:/SyntheticExam/reviewed-config `
  -PrimaryAddress 192.0.2.10 -StandbyAddress 192.0.2.11 `
  -DeploymentRoot C:/SyntheticExam/deployment -ArchiveRoot C:/SyntheticExam/wal-archive `
  -CaCertificatePath C:/SyntheticExam/public-ca.cer -MaxSlotWalKeepSizeMB 8192
```

部署人员审阅片段后并入独立受保护 PostgreSQL 配置，保持 `fsync`/`full_page_writes`、TLS 和同步确认要求；业务主备管理的独立持久确认仍然生效。生成的 `pg_hba` 只允许给定备用 IP 使用专用复制账户，不含口令，不代替业务/监测账户规则。Windows SCM 绑定按照 [主备管理契约](../high-availability-management.md) 的 `BuildPostgresServiceCommand` 和 [恢复边界](../backup-and-failover-runbook.md) 的 `BuildServiceCommand` 生成值登记；不要用通用 `pg_ctl register` 命令猜测这些受控参数。业务及管理宿主分别使用现有 `ExamServer:WindowsServiceName` / `Management:ServiceName` 配置。真实 SCM 注册/权限为部署步骤，本工具不会修改已有服务。

正式主备服务通过 `BuildPostgresServiceCommand` 固定 `archive_mode=on`、空 `archive_library`、受控 `archive_command`、`fsync=on` 和 `full_page_writes=on`。归档命令仅调用预部署工具，不从备份继承；工具和归档目录均须位于数据、资源、重建目录之外。管理配置须给出 `HighAvailability:WalArchiveDirectory` 与 `HighAvailability:WalArchiveScriptsDirectory`。缺目录、缺工具、链接或命令分隔符会阻止生成服务命令，不能退回关闭归档。服务管理的主备检查会返回 `wal-archiving`：主库须有成功回执且没有较新的未解决失败；备库核对固定配置，提升后同样要求归档回执。

旧部署须在维护窗口按新生成的受控命令更新预登记的 SCM 绑定，再重启并完成检查；代码不会擅自修改当前 Windows 服务。`ControlledRecoveryDatabase` 的临时隔离恢复库仍保持 `archive_mode=off`、固定回环端口及禁用继承恢复钩子，不能将该命令用于正式主备服务。正式服务也继续禁用未审查的恢复、清理及预加载钩子。

Windows PostgreSQL 配置片段按本机原生代码页生成，并在创建输出目录前拒绝不能无损编码的路径；公开 CA 保持 PEM。跨不同代码页主机应分别生成并检查本机配置，不能盲拷文本。备用应用名须与主备管理配置的节点标识一致；初始化、生成片段及状态检测使用同一 `StandbyApplicationName`/`ReplicationSlot`，`pg_basebackup -R` 的自动配置也会保存应用名。

管理配置的 `HighAvailability:WalArchiveTimeoutSeconds` 控制生成服务命令中的归档切换间隔，单位为秒，必须大于零，未配置时保持原来的 60 秒。例如设置为 `1800` 可用于接受较长归档等待时间的环境；按环境变量注入时使用 `HighAvailability__WalArchiveTimeoutSeconds`。同步复制仍实时传输 WAL，不会等到该间隔结束。提前切换的归档段仍占完整段大小，缩短间隔会增加归档空间占用；延长间隔则会延后未写满段的归档。应按恢复需求选择，并让 PostgreSQL 配置片段与生成的服务参数保持一致。更新现有服务绑定须安排维护窗口，发布代码本身不会改变正在运行的服务。此参数不提供归档自动清理，保留规则仍见下文。

Windows 归档脚本由 PostgreSQL 服务账户执行。部署时须在该账户下确认脚本执行策略及源、目标目录的读写权限；管理员终端运行成功不能代替服务账户验证。若策略阻止脚本，按部署策略完成脚本签名或服务账户配置，再确认归档成功；归档命令不通过 `ExecutionPolicy Bypass` 绕过策略。

## 初始化、归档与重建

`Initialize-Standby.ps1` 只接受空或不存在的数据目录，先核对 PostgreSQL 18 工具/实际主库补丁、TLS 环境、预期集群标识、主角色与未占用槽，并拒绝外部表空间，再以 `--no-clean` 执行原生基础备份。`ExpectedSystemIdentifier` 来自已核实主库/部署记录，不能从本次未知目标自动采信。复制账户须有备份/复制权限，以及查询所需控制信息的明确只读权限；权限不足即失败，不自动授权。失败后保留部分数据及所有槽，因为事前槽不存在不能证明并发初始化产生的槽属于本次操作。

WAL 归档及恢复按 PostgreSQL 文件名验证，拒绝路径穿越、NTFS 附加流和祖先联接；同名同摘要允许幂等重试，同名不同内容拒绝覆盖。写入使用同目录临时文件、持久刷新及无覆盖发布。部署完整 `scripts` 目录。归档/恢复均不可用同名文件覆盖故障原件。

管理服务执行数据库备份和备机重建时，会把连接配置中的 `SSL Mode=VerifyCA`、`SSL Mode=VerifyFull` 分别转换为 PostgreSQL 原生工具接受的 `verify-ca`、`verify-full`，并将 `Root Certificate` 路径传给 `PGSSLROOTCERT`。CA 文件须部署到执行备份或重建的主机，并允许实际运行账户读取；路径和连接凭据保存在仓库外的受保护配置中。

旧主恢复或槽 `lost`/`unreserved` 时：保持旧主业务网络隔离，记录当前主身份/代次、已确认边界、保留故障目录及槽状态；通过服务管理页或既有 `management rebuild` 提交原请求，在新的受控暂存槽重建并由主备管理核对副本/资源/密钥及追平。不可把旧库直接启动为主，不可用删除槽、增大上限或清空答卷制造恢复成功。若初始化失败需要人工清理自己的孤立槽，先独立确认归属、没有活动消费者及所需 WAL/备份边界；本脚本不自动处理。

## 复制与空间监测

[合成配置](../../deploy/postgresql/check-deployment.example.json)包含无口令 URI、预期集群、槽预算和五类目录。部署时替换并保存仓库外；监测账户需要只读统计/控制函数权限。口令用 `pgpass.conf` 或受保护环境，URI 不携带口令；两侧使用 `verify-full`。SQL 设置连接/语句超时和只读事务，恢复调用者原环境设置。

```powershell
# 一次检查；非零退出表示不满足检查条件，不能投入正式使用。
./deploy/postgresql/scripts/Test-ExamDeployment.ps1 -ConfigurationPath C:/SyntheticExam/protected/check.json
# 持续采样，输出每次独立 JSON；Ctrl+C 停止，不会切主或启用应急。
./deploy/postgresql/scripts/Test-ExamDeployment.ps1 -ConfigurationPath C:/SyntheticExam/protected/check.json -Samples 360 -IntervalSeconds 10
```

复制检查核对精确单个备用名、实际接收器/时间线、同集群与同补丁、采样主库 flush 边界、备用落盘接收及回放、归档成功/未解决失败，以及槽有效性/有限保留量。LSN 按两个十六进制分量计算，不能用字符串排序。字段定义依据 [PostgreSQL 18 动态统计](https://www.postgresql.org/docs/18/monitoring-stats.html#PG-STAT-WAL-RECEIVER-VIEW)和 [复制槽视图](https://www.postgresql.org/docs/18/view-pg-replication-slots.html)。该检查不认证题包/密钥完整性，不证明业务最新确认链，不代替主备管理的提升授权。

空间检查只读核对 PostgreSQL 数据、WAL 归档、版本资源、历史备份、受保护配置目录，绝对字节与百分比阈值须同时满足。同卷也逐项显示用途，不把容量相加；缺目录/访问失败/联接/无法核对卷均不显示就绪，不自动创建目录或删除内容。默认阈值只是工具起始值，正式部署需按场地预算调整。GUI/CLI 原有 `checks` 仍须执行真实持久写入、版本、密钥、备份检查，空间报告不能替代这些门禁。

## 调用现有管理接口

`Invoke-ExamMaintenance.ps1` 是现有发布 `Exam.Cli.exe` 的参数化入口，只访问本机 HTTPS 管理宿主。先通过现有 `management login` 获取受保护考务会话；不传口令参数。`status`/`ha-status` 只读；`checks`/`backup`/`restore`/`rebuild` 要求原 JSON 请求文件，`operation` 只使用原 `requestId` 查询。退出码原样返回，不另做备份、提升、授权或数据库操作。

```powershell
./deploy/postgresql/scripts/Invoke-ExamMaintenance.ps1 `
  -CliPath C:/SyntheticExam/published/Exam.Cli.exe -Operation backup `
  -ServerId synthetic-logical -NodeId primary -ManagementUrl https://127.0.0.1:5010 `
  -InputPath C:/SyntheticExam/protected/original-backup-request.json
```

GUI 对应服务管理页既有备份、恢复、重建及操作查询；请求与结果契约沿用服务管理和主备管理接口，不另造业务状态。工具安装/片段生成是部署准备，不能在考试运行中替换配置。

## 保留与恢复

考前建立并验证完整备份，考试中连续归档，考后再归档。历史备份包含数据库、版本资源、节点证书与解锁恢复材料及清单，和同步副本分别管理。归档增长与复制槽保留同时监测；有限槽上限可能造成槽失效，须告警并按重建流程恢复，不能宣称限制槽等于备份完成。

不按文件年龄自动删除 WAL、备份或确认日志。清理计划须核对最早保留且实际验证的基础备份、所有必要 WAL/时间线、未完成恢复和最新独立确认链，并保留独立离线副本；不足则增加容量并暂停风险操作。恢复使用原请求及隔离工作副本，故障原件/原确认记录保留；原库不可读且独立确认链缺失时仍阻断业务。实际恢复后核对原回执、剩余收卷、作答与计分，不能把历史备份直接认作当前状态。

## 投入使用前的验证

使用实际运行账户核对跨账户 ACL、Windows 服务绑定、证书私钥读取和主备网络，再按[验收标准](../acceptance-criteria.md)验证恢复与完整考试流程。隔离回环测试不能代替独立双机、断电恢复、实际系统 DPI 和 300 人整场考试验证。

回退时保留数据库、WAL、版本资源、密钥、最高代次及确认链。不能通过关闭告警、删除故障原件或清空目录制造恢复成功。
