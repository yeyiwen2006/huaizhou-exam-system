# 成员 C 下午联调准备记录（2026-09-23）

基线为 GitHub `main` 提交 `59eb1093ff77cc396922155a5a9cc30a33bc2282`，PR #42 已合并。PR #42 的审查提交 `9a6013d` 已选择性吸收后续分支 `f22d74f` 中的 C01–C04 防护及测试，并新增真实 WPF 启动/绑定验证；本次只移植尚未进入主线的 C05–C07 页面、状态和测试，保留主线更完整的 C01–C04 实现。

本机到 `github.com` 的 Git 拉取连续失败；本次使用 GitHub CLI 下载**指定 SHA 的只读源码归档**进行构建、测试与差异核对。归档无 Git 历史，不能把它误称为已同步的本地 `main`。正式提交应以 GitHub 当前 `main` 的真实父提交和树创建；若该提交已变化，须重新核对并复测。

## 已接入的正式能力与边界

- C05：`GET /api/v1/versioning/exams/{examId}` 读取 `ExamVersionSnapshot`，展示 `ScheduleVersion`、`PaperVersion`、`GradingVersion`、是否有 `PaperPublicationInfo`，及每名 `ParticipantVersionStatus` 的目标版本、已安装版本、下载/复核状态与错误。考试 ID 或连接变化后清除旧快照；迟到响应不能重新填入旧考试。发布成功不等于所有学生端已更新。
- C06：五类报告入口与未生成、考试未结束、答卷未收齐、重算中、可用、接口未提供等独立状态模型已准备。当前没有正式报告查询/导出 API，默认显示“接口未提供”，读取和导出按钮禁用；未在 C 端计算任何正式成绩。
- C07：主/备节点、复制、保护模式、备份及隔离/接管/应急/重建入口已准备。当前仅有 `Exam.Contracts.HighAvailability` 和核心服务，考务端无正式管理 HTTP API；字段显示“接口未提供”，写操作禁用，不在 GUI 实现接管状态机。
- C01–C04：延续 PR #42 审查版。A05 收齐事件可手动分页查询，但事件只对应名单版本，不能代替当前回收进度或报告可用性。

## 最新远端核对

在本次核对时，`main` 为 `59eb1093`；PR #41（D01）、#43（A05 容量与收齐恢复）、#42（C01–C04）均已合并。可见 PR 中未出现 D05、E02/E03 或 A08 管理 API 的新合并。现有 A06 可读考试版本及发布题包/规则，但无完整导入任务、草稿编译预览和 D05 重算闭环。

主线存在 `PUT /api/v1/student/exams/{examId}/metadata`，可持久化公开名称、类别、科目名称和说明文本，要求 `ExpectedVersion`；但没有考务端读取当前公开元数据版本的正式 GET，也不存储科目/题组 ID、题型、题量、计分规则。C01 当前只安全保存计划；不能在未经过 D04 规则语义校验时，把本地说明冒充已验证的正式计分说明。

## 待 A/B/D/E 交付的精确接口

下列路径是**建议契约**，不是已存在 API。所有写请求需要 `requestId`、经认证的操作者、必要的 `expectedVersion`；返回 `ApiEnvelope<T>`、稳定 `serverId/nodeId/serverEpoch` 与明确错误码。持久化成功之后才能返回成功。

| 任务 / 负责人 | 缺口 | C 需要的字段与失败语义 |
| --- | --- | --- |
| C01 / A、D | 考务 GET 公开元数据及完整考试配置读写，例如 `GET /api/v1/exams/{examId}/configuration`、`PUT /api/v1/exams/{examId}/configuration` | `examId`、`name`、`categoryId/name`、时间、稳定 `subjectId/groupId`、顺序、题型、题量、满分、D01 规则、`allowAbstain`、配置版本；D04 语义/满分校验结果。读取当前版本后按预期版本写，冲突不覆盖。现有公开 metadata PUT 仅为部分能力。 |
| C02 / A | 权威名册读写，例如 `GET /api/v1/exams/{examId}/roster`、预检与原子确认入口 | `candidateId`、姓名、班级、准考证号**文本**、`rosterVersion`；逐行字段/行号错误、完全重复数、冲突数、新增数。任一错误 0 写入；重复 `requestId` 返回原结果，报名不增加实际参试人数。 |
| C03 / A | `GET/POST /api/v1/room-groups`、`PATCH/DELETE /api/v1/room-groups/{id}`、设备单/批量转组与按客户端编号查询 | 稳定 `roomGroupId`、名称、版本、权威设备数；设备 `deviceId/clientNumber`、原/新组及版本、操作者和历史。非空组拒删；转组不改编号、考生或参试人数。现有 A02 仅能在审批时指定组 ID。 |
| C04 / A、B | 考务监测聚合快照/增量与可靠连接状态 | `participationId/candidateId/deviceId/clientNumber/roomGroupId`、姓名班级准考证号、四种业务状态、独立 `online/offline/recovering`、最后通信、当前题号及已保存答题版本、冲突双方、指令确认、失焦、未回收/未更新名单、快照版本/游标；`offline != notParticipating`。A03 当前仅有实际参与列表。 |
| C05 / D、A | 逐题导入任务、依赖检查、预览、修订草稿、编译发布、D05 重算接口 | 任务 ID/阶段/进度、每题文件位置与错误、预览产物、变更原因、`expectedVersion`、`paperVersion/gradingVersion/scheduleVersion`、发布回执与逐学生端安装状态。编译失败不发布，开考后结构增删拒绝，冲突与可重试失败分开。 |
| C06 / D、E、A | `GET /api/v1/exams/{examId}/reports/{type}`、同报告版本的正式导出 | 五类表 `reportId`、名单/计分版本、生成时间、可用性（未生成/未结束/未收齐/重算中/可用）、精确分数字符串、排名、平均分行、答案与得分颜色语义；导出与 GUI 使用相同 `reportId`，未就绪不可导出。 |
| C06 / A、E | 服务停止后仍可操作的本机管理服务与共用 CLI | 服务配置、启动/停止、自检、备份/恢复的状态、操作 ID、进度、失败原因和审计；不能让 GUI 绕过本机管理服务直接判定成功。 |
| C07 / A、E | HA 状态与受保护操作入口 | 复用 `ServerDeploymentState/NodeState/IsolationEvidence/TakeoverRequest/EmergencyModeRequest/RebuildNodeRequest`；还需备份可用性、隔离检查结果、`expectedEpoch`、操作 ID/进度与审计。原主未隔离或检查失败必须拒绝接管，备用考务端不能依赖旧主在线。 |

## 冒烟与性能边界

本机未配置 `EXAM_TEST_POSTGRES` 或服务器数据库连接，未发现本机 PostgreSQL 服务及 `psql`/`docker`。因此管理员认证、创建/保存合成考试、正式名册导入、激活审批、实际参与查询、裁决和补发等业务端到端步骤均 **BLOCKED**；ViewModel/HTTP handler 测试不能替代服务器持久化或真实 GUI 操作。主线 PR #42 的审查记录曾在独立临时 PostgreSQL 实例执行 387 项测试，但**本次归档构建未重跑该实例**，不沿用其结果冒充本次实测。

PR #42 已有 300 行合成名册和 300 条监测筛选测试；本次新增页面未发起自动网络轮询，未扩大无界事件集合。此为逻辑预检查，不是 WPF 渲染耗时、300 台终端或 300 会话正式负载验收。

本次依赖按锁文件还原；NuGet 官方漏洞数据源不可达导致 `NU1900`，安全审计**未完成**，未关闭审计，也没有增加 NuGet 包。

## 人工验收

待合成 PostgreSQL、HTTPS 凭据、设备及可用的 D/E/A 接口齐备后，需实际操作考务窗口验证 C01–C04 保存/导入/审批/监测流程，C05 发布与逐客户端安装分离，C06 五类表与同版本导出，C07 隔离阻止与接管审计；并分别记录真实 GUI、数据库及负载结果。
