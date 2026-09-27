# F01 本机管理合成响应

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
