# F05 主备部署与维护入口

关联 #36。按 requirements.md 主备与真实考试可靠性条款补齐产品工具；保留 F01/F02 的身份、审计、备份/恢复及接管门禁，A 审查与合并。工具不是 T01/T02/T03 的验收结论。

## 部署配置与账户

两机使用同提交业务/管理/考务/CLI 包、同 PostgreSQL 18 补丁版本，以及相同字体和 TeX 宏包依赖。服务账户与管理员操作账户分开；二进制、配置、工具、CA 与目录祖先由部署账户保护，业务账户不得修改它们。相应服务账户可读取各自节点证书私钥、解锁材料和版本资源；管理账户可控制绑定服务并写自己的审计/暂存/备份目录。不能用复制原机 DPAPI 密文代替异机密钥准备。账户/ACL/私钥访问必须以实际运行账户完成 F01/F02 检查，工具不自动授予权限。

`New-PostgresqlDeployment.ps1` 读取公开 CA 文件、两独立 IP、端口和路径，在**全新的输出目录**生成三个可审阅配置片段；不安装 PostgreSQL、不覆盖现有配置、不创建运行库、不启动服务。拒绝路径链接/联接、非绝对路径、已有输出、模板未替换以及私钥/过期证书文件。复制槽上限参数必须依据实际考试写入量、保留时长及存储容量核定；示例 8192 MB 不是正式容量建议。

```powershell
# 以下是合成路径/IP，实际配置与证书必须放在仓库外。
./deploy/postgresql/scripts/New-PostgresqlDeployment.ps1 `
  -OutputDirectory C:/SyntheticExam/reviewed-config `
  -PrimaryAddress 192.0.2.10 -StandbyAddress 192.0.2.11 `
  -DeploymentRoot C:/SyntheticExam/deployment -ArchiveRoot C:/SyntheticExam/wal-archive `
  -CaCertificatePath C:/SyntheticExam/public-ca.cer -MaxSlotWalKeepSizeMB 8192
```

部署人员审阅片段后并入独立受保护 PostgreSQL 配置，保持 `fsync`/`full_page_writes`、TLS 和同步确认要求；业务 F02 的独立持久确认仍然生效。生成的 `pg_hba` 只允许给定备用 IP 使用专用复制账户，不含口令，不代替业务/监测账户规则。Windows SCM 绑定按照 [F02 管理契约](../f02-high-availability-management.md) 的 `BuildPostgresServiceCommand` 和 [恢复边界](../backup-and-failover-runbook.md) 的 `BuildServiceCommand` 生成值登记；不要用通用 `pg_ctl register` 命令猜测这些受控参数。业务及管理宿主分别使用现有 `ExamServer:WindowsServiceName` / `Management:ServiceName` 配置。真实 SCM 注册/权限为部署步骤，本工具不会修改已有服务。

当前 F02 受控重建服务命令强制 `archive_mode=off`；这是隔离运行约束，不能靠配置片段覆盖。新监测会明确阻止把该状态认作连续归档就绪。重建/提升后受控重新启用归档的生产接线仍须 A 与 F02 确认和实现，不能按普通 `ALTER SYSTEM` 绕过固定 SCM 绑定。#36 保持打开，本文不把这一缺口改称现场验收。

## 初始化、归档与重建

`Initialize-Standby.ps1` 只接受空或不存在的数据目录，先核对 PostgreSQL 18 工具/实际主库补丁、TLS 环境、预期集群标识、主角色与未占用槽，再执行原生基础备份。`ExpectedSystemIdentifier` 来自已核实主库/部署记录，不能从本次未知目标自动采信。复制账户须有备份/复制权限，以及查询所需控制信息的明确只读权限；权限不足即失败，不自动授权。失败后保留部分数据及所有槽，因为事前槽不存在不能证明并发初始化产生的槽属于本次操作。

WAL 归档及恢复按 PostgreSQL 文件名验证，拒绝路径穿越、NTFS 附加流和祖先联接；同名同摘要允许幂等重试，同名不同内容拒绝覆盖。写入使用同目录临时文件、持久刷新及无覆盖发布。部署完整 `scripts` 目录。归档/恢复均不可用同名文件覆盖故障原件。

旧主恢复或槽 `lost`/`unreserved` 时：保持旧主业务网络隔离，记录当前主身份/代次、已确认边界、保留故障目录及槽状态；通过服务管理页或既有 `management rebuild` 提交原请求，在新的受控暂存槽重建并由 F02 核对副本/资源/密钥及追平。不可把旧库直接启动为主，不可用删除槽、增大上限或清空答卷制造恢复成功。若初始化失败需要人工清理自己的孤立槽，先独立确认归属、没有活动消费者及所需 WAL/备份边界；本脚本不自动处理。

## 复制与空间监测

[合成配置](../../deploy/postgresql/check-deployment.example.json)包含无口令 URI、预期集群、槽预算和五类目录。部署时替换并保存仓库外；监测账户需要只读统计/控制函数权限。口令用 `pgpass.conf` 或受保护环境，URI 不携带口令；两侧使用 `verify-full`。SQL 设置连接/语句超时和只读事务，恢复调用者原环境设置。

```powershell
# 一次检查；非零退出表示不满足检查条件，不能投入正式使用。
./deploy/postgresql/scripts/Test-ExamDeployment.ps1 -ConfigurationPath C:/SyntheticExam/protected/check.json
# 持续采样，输出每次独立 JSON；Ctrl+C 停止，不会切主或启用应急。
./deploy/postgresql/scripts/Test-ExamDeployment.ps1 -ConfigurationPath C:/SyntheticExam/protected/check.json -Samples 360 -IntervalSeconds 10
```

复制检查核对精确单个备用名、实际接收器/时间线、同集群与同补丁、采样主库 flush 边界、备用落盘接收及回放、归档成功/未解决失败，以及槽有效性/有限保留量。LSN 按两个十六进制分量计算，不能用字符串排序。字段定义依据 [PostgreSQL 18 动态统计](https://www.postgresql.org/docs/18/monitoring-stats.html#PG-STAT-WAL-RECEIVER-VIEW)和 [复制槽视图](https://www.postgresql.org/docs/18/view-pg-replication-slots.html)。该检查不认证题包/密钥完整性，不证明业务最新确认链，不代替 F02 的提升授权。

空间检查只读核对 PostgreSQL 数据、WAL 归档、版本资源、历史备份、受保护配置目录，绝对字节与百分比阈值须同时满足。同卷也逐项显示用途，不把容量相加；缺目录/访问失败/联接/无法核对卷均不显示就绪，不自动创建目录或删除内容。默认阈值只是工具起始值，正式部署需按场地预算调整。GUI/CLI 原有 `checks` 仍须执行真实持久写入、版本、密钥、备份检查，空间报告不能替代这些门禁。

## 调用现有管理接口

`Invoke-ExamMaintenance.ps1` 是现有发布 `Exam.Cli.exe` 的参数化入口，只访问本机 HTTPS 管理宿主。先通过现有 `management login` 获取受保护考务会话；不传口令参数。`status`/`ha-status` 只读；`checks`/`backup`/`restore`/`rebuild` 要求原 JSON 请求文件，`operation` 只使用原 `requestId` 查询。退出码原样返回，不另做备份、提升、授权或数据库操作。

```powershell
./deploy/postgresql/scripts/Invoke-ExamMaintenance.ps1 `
  -CliPath C:/SyntheticExam/published/Exam.Cli.exe -Operation backup `
  -ServerId synthetic-logical -NodeId primary -ManagementUrl https://127.0.0.1:5010 `
  -InputPath C:/SyntheticExam/protected/original-backup-request.json
```

GUI 对应服务管理页既有备份、恢复、重建及操作查询；请求与结果契约使用 F01/F02，不另造业务状态。工具安装/片段生成是部署准备，不能在考试运行中替换配置。

## 保留与恢复

考前建立并验证完整备份，考试中连续归档，考后再归档。历史备份包含数据库、版本资源、节点证书与解锁恢复材料及清单，和同步副本分别管理。归档增长与复制槽保留同时监测；有限槽上限可能造成槽失效，须告警并按重建流程恢复，不能宣称限制槽等于备份完成。

不按文件年龄自动删除 WAL、备份或确认日志。清理计划须核对最早保留且实际验证的基础备份、所有必要 WAL/时间线、未完成恢复和最新独立确认链，并保留独立离线副本；不足则增加容量并暂停风险操作。恢复使用原请求及隔离工作副本，故障原件/原确认记录保留；原库不可读且独立确认链缺失时仍阻断业务。实际恢复后核对原回执、剩余收卷、作答与计分，不能把历史备份直接认作当前状态。

## 模块验证与限制（2026-10-06）

Windows / PowerShell 7，合成文件、报表、临时公开证书及明确模拟的原生命令；工具测试纳入现有 `tests/Tools` 自动发现。新增 F05 工具测试 11 项通过，全工具测试 36 项通过；锁定还原及全项目 Release 构建 0 警告、0 错误。现有服务管理/主备/管理 CLI 分类回归共 449 项通过（核心 297、考务 63、接口 89；排除 PostgreSQL/TeX/WPF）。覆盖归档/恢复幂等与不同内容拒绝、参数/祖先联接、配置生成不覆盖、身份/角色/补丁/时间线/LSN/槽/归档失败、实际卷空间阈值、初始化失败保留数据/槽、原 CLI 请求与退出码、依赖缺失不能显示绿色。

没有操作正式数据、运行库或 Windows 服务。尚未在真实 PostgreSQL 18 TLS 双机执行新增 SQL、Windows PowerShell 5.1 兼容、跨服务账户 ACL、SCM 登记、断电、现场恢复或容量验收；最新 CI/A 审查也需单列。整个产品发布仍依赖 T01/T02/T03。回退脚本保留数据库、WAL、资源、密钥、最高代次及确认日志，不能删除数据解除告警。

已同步 main c66074c，并在 PR126/127/129/130 本地组合候选再次检查：locked restore / Release 0 警告、0 错误；适用非 PostgreSQL/TeX/WPF 测试 2612 项全部通过，工具 36 项通过。组合检查不替代真实 PG 双机或本卡缺失的生产入口。Actions 最新已检查提交因账户付款/额度受限未启动任务，不能算 CI 通过。F02 受控 SCM 命令强制 archive_mode=off 的生产归档接线缺口仍存在，须 A 审查修复后才能关闭 #36；本 PR 未绕过其恢复隔离边界。
