# F01 本机管理合成响应与部署模板

这些 JSON 均人工构造，仅用于管理契约说明，不包含真实试题、账号、配置或恢复数据。

- `status-success.json`：业务停止但管理状态可读；进程状态与业务就绪分开，当前代次未核实。
- `request-conflict.json`：同 requestId 的节点、操作者或内容不同，HTTP 409。
- `restore-staged.json`：隔离暂存完成，恢复核对与业务就绪均未完成；不能作为恢复成功证据。
- `restore-recovery-verified.json`：恢复目标及领域检查通过，业务尚未开放，仍为 Staged；CLI 不报告操作完成。
- `restore-activated.json`：部署引用切换后业务 HTTPS 就绪通过，三个恢复结果均为 true，才报告 Completed。
- `restore-rolled-back.json`：启动失败后确认业务停止且无新增业务状态，回退原连接引用；保持 Staged，不自动启动原库。
- `checks-blocked.json`：自检操作完成，但资源核对失败并阻断业务启动；Completed 不表示所有检查通过。
- `stop-reconciled.json`：原停机结果落盘中断后，从实际已停止状态确认原请求，未再次执行 SCM 停机。
- `restore-needs-verification.json`：原恢复结果中断且暂存证据缺失或损坏，继续等待核对，不重复恢复或开放业务。

写请求必须附 `Authorization: Bearer …`、`X-Exam-Server-Id` 与 `X-Exam-Node-Id`；后两个值是预期目标节点，不是操作者身份。启停、自检请求体为 `{ "requestId": "synthetic-stop", "serverId": "synthetic-server", "nodeId": "synthetic-node" }`。保存配置另外附 `expectedVersion` 与完整 `configuration`；备份/恢复请求体复用既有 `BackupRequest`/`RestoreRequest`。

所有输出沿用 `JsonDefaults` 的大小写和字符串枚举规则。普通状态的 `readiness=unknown` 表示未执行当前业务就绪核对；操作结果中的状态为该次操作的观察快照，不能当作当前状态。客户端须核对管理封套的 serverId/nodeId，不能将未核实代次用于业务写入或接管。

操作记录包含可选 `requestJson`（规范化原请求）、`recoveryWorkingDirectory`（已绑定的非秘密恢复目标路径）及 `recoveryActivation`（选择待执行、已选择、业务启动中、业务就绪、回退待执行、已回退）。旧记录缺省仍可读取；目标在副本准备前持久保存，配置变化不能把原恢复重定向到另一工作槽。每项启用副作用前写入 NeedsVerification 意图，暂存后的故障保留阶段及目标并继续阻止另一个请求启动。人工响应中的摘要只是合成占位，不能直接写入操作目录；客户端仍须校验 requestId、目标及预期版本。

备份中断后可凭已绑定清单、文件摘要与 PostgreSQL 校验恢复为 Completed；恢复中断后凭暂存证据恢复为 Staged，输出如 `restore-staged.json`。配置工作槽后，同一恢复 POST 可显式续行准备工作副本及核对恢复数据库。未启用部署选择时核对通过仍保持 Staged 和 businessReady=false。启用后的中断查询只观察原请求的部署选择、绑定工作副本、实际目标、领域检查和 HTTPS 就绪；不复制、切换或重启。不能核实时保留 NeedsVerification。

`appsettings.management.example.json` 和 `service-configuration.example.json` 是人工构造的 **Windows 部署模板**，不是可直接投入使用的部署配置或操作记录。主、备机分别设置实际节点身份、独立服务名、端口和仓库外受控路径，逻辑服务器身份须与业务一致；示例路径没有自动创建。管理配置模板在仓库外配置为可执行文件目录的 `appsettings.json`，初始非秘密服务配置在仓库外配置到 `Management:ServiceConfigurationPath`。服务模式按可执行文件目录加载配置；不要从 system32 寻找文件。

模板刻意不包含 `ExamSecurity:OperatorToken`、`ExamSecurity:OperatorId` 或数据库连接串。按既有受保护部署配置/服务账户配置提供这些项，令牌至少 32 字符；不放命令行，不把填好的文件提交到仓库。证书指纹占位必须替换，证书/私钥必须实际部署并受保护，不能通过关闭 TLS 校验使用模板。缺少管理 TLS 证书时不会启动管理 HTTPS；缺少有效凭据时返回 401；缺失业务数据库/节点证书时允许管理认证和状态诊断，但对应操作或自检失败。

服务配置、操作目录及备份/暂存根目录分别保护。操作目录不放其他 JSON、不经过联接点，也不放在备份材料源目录内。模板的 `version=1` 仅为合成首次配置版本，不能用于覆盖现有配置；后续保存必须读取当前版本并携带 expectedVersion。`highAvailabilityEnabled=true` 不代表已完成配对或复制，`ExamServer:ServerEpoch=1` 仅为合成部署比较值，不是当前权威代次。模板不默认启用连接切换。

`Backup:StartupVerificationDirectory` 与 `Backup:StartupVerificationBackupId` 指定启动自检的备份及其固定标识，必须在允许备份根目录内；不能保留占位值或自动选“最新”。先通过管理备份接口创建完整备份，再由部署人员更新受保护配置中的选择并重启管理宿主（当前无动态重载）。缺少选择、摘要损坏、工具失败或标识不符均产生阻止启动的 `backup-integrity` 检查；初始化时仍可认证、查询状态和创建备份。检查不依赖业务数据库、不写恢复目标，但不证明备份新鲜度、所属服务器、WAL 连续覆盖或密钥可实际解锁，完整恢复演练仍需执行。

`Recovery:WorkingRoot` 须为预建受保护目录，固定工作槽为 `slot/database`，不得与备份、暂存或原材料目录交叠。缺失原材料源目录不阻断从完整备份暂存，但新建备份仍要求原材料完整。残缺槽保留并拒绝覆盖；完整槽绑定原 requestId，不用于另一项恢复。不配置工作根时仍只返回隔离暂存。

`ConnectionStrings:ExamRecovery` 由独立受保护配置提供，不出现在模板或请求中。要求回环地址、与本机原库不同的端口及相同数据库名；SQL 核对实际端口和数据目录必须对应工作槽，拒绝备库角色，再运行该目标的迁移、节点/代次及领域恢复检查。目标未启动、配置不符或检查失败时不报告恢复核对通过。当前宿主不会启动 PostgreSQL 或导入密钥；部署人员须先以独立受限实例启动已经绑定的工作副本，核对其配置仅访问隔离目录并使用独立回环端口，部署匹配的证书和解锁材料。不能启动暂存原件或手工修改操作记录解除保护。

可选 `Recovery:SelectionPath` 启用恢复后的业务连接切换，须为仓库外、预建受保护目录中的绝对文件路径。管理和业务两套受保护配置必须设置同一路径、Exam/ExamRecovery 连接及服务器/节点/预期代次，并使用包含本接线的业务版本；文件缺失表示原连接，损坏、身份不符或已选恢复连接缺失时拒绝回退。选择文件只保存原请求身份、工作槽及连接引用，不保存秘密；不要放在操作 JSON 目录或备份材料源中。业务运行时不要手工改选库文件或自动启动原库。

启用前业务须停止，自检及恢复目标检查通过，并只读比对原库与恢复库的全部 exam 业务表、列定义和序列（排除备份/恢复操作审计表）。必须已存在匹配节点和代次的 EmergencySingleNode 权威状态；本操作不启用应急模式、不提升节点、不改代次。原库不可读、状态不同或仍需重建双机保护时保持阻断，不把旧备份当作最新状态；离线原库的完整性证明及 F02 主备重新接线仍不在本路径内。

选择写入后才启动固定业务服务，业务入口继续保留原活动节点门禁，并核对实际恢复数据目录后运行领域恢复检查。只有 HTTPS 就绪通过才返回恢复完成。失败或取消先停止业务，再重新核实原库与恢复库状态；一致才回退连接引用，原库不自动启动。已有新写入、原库不可读、停止或回退无法确认时保留选中的恢复库与 NeedsVerification，禁止覆盖较新答卷。同一已完成 requestId 只返回原结果。

接口边界以 [ADR 0002](../../docs/decisions/0002-local-management-access.md) 为准，恢复原则见已有 [恢复运行说明](../../docs/backup-and-failover-runbook.md)。实际模块验证与未完成项在实现 PR 中记录。
