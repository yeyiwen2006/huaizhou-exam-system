# 成员 C 回归、联调准备与接口需求（2026-09-23）

本记录基于 `codex/c01-exam-config` 的 PR #42（基线 `8ae7a65`）及后续分支 `codex/c04-regression-readiness`。后续分支依赖 PR #42，不能在 #42 合并前作为独立 PR 指向 `main`。本文的“接口建议”不是已存在的 API；正式路径、Contract 与错误码须由负责成员和 A 统一审定。

## 远端核对

GitHub `main` 在本次核对时为 `a714303f7215e78b0ca0aa880d9a871cb1c11807`。D01 schema 的 PR #41 已合并（实现 `3a08dbd`，合并 `fad8069`）；A05 收齐恢复接口的 PR #43 已合并（实现 `31ebdf7`，合并 `a714303`）。PR #42 仍 open，其 `build-test`、`postgres-integration` 均已通过，但 GitHub 当前判定 `mergeable=CONFLICTING`；本次不代替审查人解决冲突，也未修改其原分支。`git fetch origin --prune` 因本机到 GitHub 的连接重置而未成功，以上新主分支事实由 GitHub CLI 的远端 API 核查，**没有把未拉取的主分支直接合并进 C 后续分支**。远端可见分支及 PR 未发现 D05/E 报表实现或正式房间组、名册、在线监测、主备管理 API。

D01 `scoring-rules.schema.json` 规定常量长度上限 32、表达式长度上限 200、`single`/`multiple`/`indeterminate` 与 `oneCorrect`/`multipleCorrect` 结构；C01 编辑校验已按这些边界补强。A05 正式接口 `GET /api/v1/submissions/collection/events?examId={id}&afterRosterVersion={version}` 返回 `CollectionCompletedNotification[]`，服务端每页至多 100 条；C04 现在可手工分页查询、按事件 ID 去重，最多显示最近 500 条。该事件只证明指定名单版本的收齐，不等于成绩报告可用；客户端游标仅在本进程内保存，重启后会重新读取，不能声称持久通知验收完成。

## 合成回归与冒烟测试

C01–C04 补充空值、时间边界、计分结构与表达式、动态删除后 ID/顺序、错误码、Excel 前导零/公式/额外列/过大文件和 300 行、房间组本地草稿、四种参试业务状态、筛选与 300 行、正式 HTTP 路由及失败传播等单元测试。测试用合成数据和测试 HTTP handler；这些测试**不**证明服务器持久化、桌面操作或 300 台设备负载。房间组移动、在线/离线/恢复中及当前题号没有正式接口，未用 Fake 冒充通过。

本机无 PostgreSQL 连接配置，且未发现可用的本机 PostgreSQL/Docker。生产模式 Exam.Server 因缺数据库而拒绝启动；开发模式仅用于健康探针：`/health/live` 返回 200 `live`（PASS），`/health/ready` 返回 503 `recovering`、明确提示数据库未配置（PASS，安全阻止业务）。业务冒烟流程状态：

| 步骤 | 状态 | 原因 |
| --- | --- | --- |
| 1 管理员登录/认证 | BLOCKED | 服务未 ready，无法做真实令牌验证 |
| 2 创建合成考试 | BLOCKED | 无可写 PostgreSQL |
| 3 配置 2 科/多个题组 | BLOCKED | ViewModel 可合成测试，GUI/正式保存无法验证 |
| 4 生成说明 | BLOCKED | 自动测试覆盖生成逻辑，未做真实 GUI 操作 |
| 5 保存考试 | BLOCKED | 正式 A06 需数据库 |
| 6 导入前导零名册 | BLOCKED | 本地 Excel 自动测试通过，正式名册写入 API 尚缺 |
| 7 重复与冲突 | BLOCKED | 本地全量校验自动测试通过，正式持久结果尚缺 |
| 8 查询激活设备 | BLOCKED | 服务未 ready |
| 9 审批测试设备 | BLOCKED | 无合成激活申请及数据库 |
| 10 查询 Participation | BLOCKED | 服务未 ready |
| 11 设备裁决 | BLOCKED | 无合成参与记录及数据库 |
| 12 命令补发 | BLOCKED | 无真实持久指令事件及数据库 |

当前环境没有可操作 WPF 桌面的自动控制工具，因此没有 GUI 截图，也不把 ViewModel 测试记作 GUI 验收。

## 待实现的正式接口（交接 A/D/E）

所有写请求应走现有 `ApiEnvelope<T>`，包含 `requestId`、操作者身份及需要时的 `expectedVersion`；成功须以 PostgreSQL 持久化为准，冲突给出 `VERSION_CONFLICT`，错误携带明确 code。建议复用 `ExamId`、`RoomGroupId`、`DeviceId`、`ParticipationId`、`CandidateId`、`RosterVersion`、`ServerId`、`NodeId`、`ServerEpoch`，不要让 C 推断或自造身份。

| 任务/负责人 | 缺口与建议接口 | 至少需要的字段/语义 |
| --- | --- | --- |
| C01 / A | 考试完整元数据：建议 `GET/PUT /api/v1/exams/{examId}/metadata` | 考试名称、科目与题组的稳定 ID/顺序、题型、所属科目、允许不作答、D01 规则引用、版本；按 `expectedVersion` 原子保存。当前 A06 仅保存 schedule，C 的其他配置仍是本地草稿。 |
| C02 / A | 正式名册：建议 `GET /api/v1/exams/{examId}/roster`、`POST /api/v1/exams/{examId}/roster/import-preview`、`POST /api/v1/exams/{examId}/roster/import-commit` | 准考证号与班级用原样字符串；候选人稳定 ID、姓名、班级、准考证号、名单版本；全量行号/字段错误、重复与冲突、0 写入失败、请求幂等、按版本并发提交；给 A05 权威名单与收齐计数。 |
| C03 / A | 房间组：建议 `GET/POST /api/v1/room-groups`、`PATCH/DELETE /api/v1/room-groups/{roomGroupId}`、`POST /api/v1/room-groups/{roomGroupId}/devices:move` | 组 ID/名称/版本/设备数；批量移动输入设备 ID 列表、源/目标组及预期版本，原子返回每设备结果、未移动的原因；非空组拒删，设备的 `clientNumber`、考生身份、参试人数不变。当前 C 只有本地组草稿，A02 审批支持 `roomGroupId` 但无组持久管理。 |
| C04 / A、B | 监测聚合：建议 `GET /api/v1/exams/{examId}/monitoring?cursor={cursor}` 或快照加增量 | 每行 `participationId`、`candidateId`、业务状态、授权设备、`entryVersion`、`authorizationVersion`、`version`、独立连接状态 `online/offline/recovering`、最后心跳、当前题号/标记、交卷保存/收齐状态；明确 `offline != notParticipating`、重连后的服务器版本与分页上限。现有 A03 只有实际参试查询。 |
| C05 / D、A | D02/D03 导入任务与 D05 修订：建议 `POST/GET /api/v1/exams/{examId}/import-jobs`、`POST /api/v1/exams/{examId}/revision-drafts`、`POST /api/v1/exams/{examId}/revisions:publish` | 任务 ID、逐题解析/依赖错误（题号、字段、位置）、预览产物/哈希；草稿题目、答案、计分规则、原因、预计 `paperVersion`/`gradingVersion`；发布带 `requestId`/`expectedVersion`，明确编译失败不发布、开考后结构变动拒绝、冲突原因、逐客户端目标/安装版本和失败。A06 `GET /versioning/exams/{id}` 已可读版本，但不等于导入和 D05 重算。 |
| C06 / D、E、A | 正式报告：建议 `GET /api/v1/exams/{examId}/reports/{type}`、`GET /api/v1/exams/{examId}/reports/{type}/export` | 五类表的共同 `reportId`、`rosterVersion`、`gradingVersion`、计算时间、状态 `notGenerated/examNotEnded/collectionIncomplete/recalculating/available`；服务端精确分数字符串、满分、班级/排序、平均分行、答案及逐题得分和颜色语义；导出与 GUI 同一 `reportId`，未就绪不可导出。D05 重算与 E02/E03 正式结果尚缺。 |
| C06 / A、E | 服务管理：建议只读状态与经授权的启动/停止/恢复操作 API/CLI | 服务状态、节点身份、数据库 ready、最近错误与安全审计；不能让 GUI 直接操纵系统服务或仅凭本地按钮判定成功。 |
| C07 / A、E | 主备：建议 `GET /api/v1/ha/deployment`，及隔离检查/接管/单机应急/重建/审计的受保护管理端点 | 复用现有 `ServerDeploymentState`、`NodeState`、`IsolationEvidence`、`TakeoverRequest`、`EmergencyModeRequest`、`RebuildNodeRequest`；返回复制延迟、保护模式、备份状态/最后成功时间、隔离证据、审批者、`expectedEpoch`、操作 ID/进度及审计。失败必须拒绝接管，旧主不可写；C 不实现状态机。当前仅有 Contract/Core，无正式 Server API。 |

## C05–C07 当前交付边界

- C05：正式 A06 版本快照接入，可显示计划/题包/计分版本与逐参试者更新状态；查询失败清除旧快照。修改原因保留为本地草稿；导入/预览/发布按钮禁用并标记“接口未提供”。
- C06：五类报告选项和六种 UI 可用性状态已建模；无服务器报告时默认“接口未提供”，读取、导出禁用；绝不在 C 自行计算正式成绩。
- C07：主/备节点、复制、保护、备份和操作入口页面已布置，所有未知字段标记“接口未提供”，接管等按钮禁用；不伪造健康状态或操作结果。

## 性能与依赖安全预检查

合成 300 行名册解析/预览、300 条监测行的连续筛选通过；入库前整批校验、公式和多余列拒绝、10 MB 文件/50 MB ZIP 解压/1000 ZIP entry/10000 行上限有自动测试或代码防护。监测收齐事件一次最多补取 10 页，UI 最多保留 500 条，避免无界集合；这些是逻辑预检查，**未测量** WPF 渲染耗时、GUI 主线程卡顿、300 台设备网络请求、真实数据同步或正式负载指标。批量转组没有正式 API，未作伪性能验证。

ClosedXML 固定 `0.105.1`，直接引用仅在考务端及其单测中；锁文件记录直接和传递依赖，没有重复 Excel 库。唯一配置源为官方 NuGet `https://api.nuget.org/v3/index.json`；本机访问该源失败，NuGet 审计产生 `NU1900`，因此不能声称“无已知漏洞”。没有关闭安全审计或新增其他包。

## 人工 GUI 最终验收清单

在可用 PostgreSQL、HTTPS 证书、合成管理员凭据和合成设备/考生数据准备好后：

1. 打开考务端，检查连接失败/成功提示、错误身份和过期令牌；确认令牌不在屏幕日志或文件中。
2. 配置 2 科、不同题型和多个题组，增删/切换后核对计分说明；故意制造并发版本冲突，确认无假成功。
3. 用合成 Excel 导入 `00125`，逐行预览首/中/末行错误、重复/冲突；整批失败后刷新服务器名册，确认 0 条写入。
4. 核对真实激活审批和编号、房间组非空拒删、正式移动后编号/身份/参试人数不变。
5. 核对 `offline` 与“未参加”分离、重连/补发/裁决失败提示、A05 收齐事件与报告未就绪分离。
6. 待 D/E/A 接口交付后，再验 C05 发布与逐客户端安装、C06 五类表/精确导出、C07 隔离与接管；现在这些步骤必须显示不可用。
