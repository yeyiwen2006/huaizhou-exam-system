# F01 本机管理合成响应与部署模板

这些 JSON 均人工构造，仅用于管理契约说明，不包含真实试题、账号、配置或恢复数据。

- `status-success.json`：业务停止但管理状态可读；进程状态与业务就绪分开，当前代次未核实。
- `request-conflict.json`：同 requestId 的节点、操作者或内容不同，HTTP 409。
- `restore-staged.json`：隔离暂存完成，恢复核对与业务就绪均未完成；不能作为恢复成功证据。
- `checks-blocked.json`：自检操作完成，但资源核对失败并阻断业务启动；Completed 不表示所有检查通过。
- `stop-reconciled.json`：原停机结果落盘中断后，从实际已停止状态确认原请求，未再次执行 SCM 停机。
- `restore-needs-verification.json`：原恢复结果中断且暂存证据缺失或损坏，继续等待核对，不重复恢复或开放业务。

写请求必须附 `Authorization: Bearer …`、`X-Exam-Server-Id` 与 `X-Exam-Node-Id`；后两个值是预期目标节点，不是操作者身份。启停、自检请求体为 `{ "requestId": "synthetic-stop", "serverId": "synthetic-server", "nodeId": "synthetic-node" }`。保存配置另外附 `expectedVersion` 与完整 `configuration`；备份/恢复请求体复用既有 `BackupRequest`/`RestoreRequest`。

所有输出沿用 `JsonDefaults` 的大小写和字符串枚举规则。普通状态的 `readiness=unknown` 表示未执行当前业务就绪核对；操作结果中的状态为该次操作的观察快照，不能当作当前状态。客户端须核对管理封套的 serverId/nodeId，不能将未核实代次用于业务写入或接管。

新增操作记录包含可选 `requestJson`（规范化原请求）；受保护持久记录使用它与 SHA-256 内容绑定核对配置保存结果。以上人工响应中的摘要仅为合成占位，不可直接写入本机操作目录。旧记录缺少该字段仍可读取；客户端不要用它替代 requestId、目标或预期版本校验。

备份中断后可凭已绑定清单、文件摘要与 PostgreSQL 校验恢复为 Completed；恢复中断后最多凭暂存证据恢复为 Staged，输出仍如 `restore-staged.json`。检查成功不触发新的备份、再次恢复、库切换或业务启动；证据不足时返回待核对状态。

`appsettings.management.example.json` 和 `service-configuration.example.json` 是人工构造的 **Windows 部署模板**，不是可直接投入使用的部署配置或操作记录。主、备机分别设置实际节点身份、独立服务名、端口和仓库外受控路径，逻辑服务器身份须与业务一致；示例路径没有自动创建。管理配置模板在仓库外配置为可执行文件目录的 `appsettings.json`，初始非秘密服务配置在仓库外配置到 `Management:ServiceConfigurationPath`。服务模式按可执行文件目录加载配置；不要从 system32 寻找文件。

模板刻意不包含 `ExamSecurity:OperatorToken`、`ExamSecurity:OperatorId` 或数据库连接串。按既有受保护部署配置/服务账户配置提供这些项，令牌至少 32 字符；不放命令行，不把填好的文件提交到仓库。证书指纹占位必须替换，证书/私钥必须实际部署并受保护，不能通过关闭 TLS 校验使用模板。缺少管理 TLS 证书时不会启动管理 HTTPS；缺少有效凭据时返回 401；缺失业务数据库/节点证书时允许管理认证和状态诊断，但对应操作或自检失败。

服务配置、操作目录及备份/暂存根目录分别保护。操作目录不放其他 JSON、不经过联接点，也不放在备份材料源目录内。模板的 `version=1` 仅为合成首次配置版本，不能用于覆盖现有配置；后续保存必须读取当前版本并携带 expectedVersion。`highAvailabilityEnabled=true` 不代表已完成配对或复制，`ExamServer:ServerEpoch=1` 仅为合成部署比较值，不是当前权威代次。模板不新增恢复库连接或自动切换设置，也不解决尚未完成的生产恢复闭环。

`Backup:StartupVerificationDirectory` 与 `Backup:StartupVerificationBackupId` 指定启动自检的备份及其固定标识，必须在允许备份根目录内；不能保留占位值或自动选“最新”。先通过管理备份接口创建完整备份，再由部署人员更新受保护配置中的选择并重启管理宿主（当前无动态重载）。缺少选择、摘要损坏、工具失败或标识不符均产生阻止启动的 `backup-integrity` 检查；初始化时仍可认证、查询状态和创建备份。检查不依赖业务数据库、不写恢复目标，但不证明备份新鲜度、所属服务器、WAL 连续覆盖或密钥可实际解锁，完整恢复演练仍需执行。

接口边界以 [ADR 0002](../../docs/decisions/0002-local-management-access.md) 为准；恢复切换差异见 [待确认方案](../../docs/f01-recovery-switch-proposal.md)。实际模块验证与未完成项在实现 PR 中记录。
