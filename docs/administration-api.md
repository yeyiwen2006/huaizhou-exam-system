# 考务管理接口：名册、试场、设备、监控与整体发布

本约定对应 `docs/reviews/ui-requirements-c-20261002.md` 请求 A 提供的接口。所有路径以下列前缀开头：`/api/v1`。数据类型的唯一代码定义在 `Exam.Contracts/Administration`；此文不另建一套字段。

## 身份、版本和重试

考务接口要求已批准账号的 Bearer 会话，普通已批准考务账号可以操作；操作者由服务器从会话取得，不能通过 JSON 冒充。设备状态上报使用已有设备编号标识与受保护凭据请求头，不能用考务会话代替，也不能因此读取考务名册、已选答案或发布公钥。公开的学生端编号只用于界面选择，内部设备标识仍用于服务器关联。

响应沿用 `ApiEnvelope<T>`：逻辑服务器、节点、服务代次、关联标识、结果及错误。查询界面必须确认响应属于当前服务器和考试，切换后丢弃迟到响应。

每个修改请求都要保存并复用 `RequestId`，直到取得明确结果。同一请求、同一内容重试返回原回执；复用请求号而改变内容或操作者会冲突。`ExpectedVersion` 是操作人员实际读取的版本，不得为了让写入成功而自动替换成最新版本。版本冲突后刷新、重新确认再产生新的请求。

## 考生名册

| 方法与路径 | 请求 | 结果 |
|---|---|---|
| GET `/exams/{examId}/roster` | 无 | `CandidateRosterSnapshot` |
| POST `/exams/{examId}/roster/import` | `ImportCandidateRosterRequest` | `RosterImportResult` |
| PUT `/exams/{examId}/roster/{candidateId}` | `UpdateCandidateRosterRequest` | `CandidateRosterSnapshot` |
| DELETE `/exams/{examId}/roster/{candidateId}` | JSON `DeleteCandidateRosterRequest` | `CandidateRosterSnapshot` |

名册按考试隔离。手工录入与 Excel 确认导入均调用同一个批量入口，手工录入是一条记录的批次。每批最多 10000 行。班级和准考证号按数字文本验证、保存，前导零不丢失。完全重复记录跳过；同一准考证号的姓名或班级冲突、任一格式错误时，`Applied=false`、`Added=0`，整批不写入。HTTP 成功并不等于导入成功，界面必须检查 `Applied` 和逐行 `Issues`。

修改、删除要带名册版本。已有准备入场或实际参试记录的身份不能删除、更换，避免破坏答卷与历史关联。尚未使用的身份更正会生成新的内部身份标识，使旧登录会话不能沿用原身份进入考试。名册维护本身不增加实际参试人数。

## 试场和设备

| 方法与路径 | 说明 |
|---|---|
| GET/POST `/rooms` | 查询分组；按 `CreateRoomGroupRequest` 新建 |
| PUT/DELETE `/rooms/{groupId}` | 带版本改名；仅允许删除服务器确认没有设备的分组 |
| POST `/rooms/move-devices` | `MoveDevicesRequest`，按公开学生端编号和各设备版本整批转组 |
| GET `/devices` | `DeviceDirectoryPage`，支持 `roomGroupId`、`query`、`status`、`offset`、`limit` |
| GET `/devices/{clientNumber}/room-history` | 最近 2000 条转组历史，包含前后试场、操作者、原因和时间 |

试场属于当前逻辑服务器，不依附某一场考试。分组名做 Unicode 兼容规范化和不区分大小写的重复检查。未分组是空关联，不是需要额外创建的实体分组。

设备查询不传 `roomGroupId` 表示全部，传空字符串表示未分组。`query` 匹配公开编号、当前姓名、班级或准考证号。分页默认 500，最大 2000，使用返回的 `NextOffset` 继续。完整公开编号查询后须精确匹配，不能把部分匹配的第一台设备当作目标。

一次转组允许 1 至 2000 台不同设备。任一设备不存在、版本变化或目标分组失效，整批不转组；成功后同时保存转组历史。转组不改变设备身份、凭据、公开编号、参试人数或成绩。旧激活数据已有的分组标识通过迁移保留，不会静默变为未分组。

激活审批继续使用原 `/activation/approve`。`RoomGroupId` 可为空；非空时验证真实分组并防止并发删除。

## 设备状态上报

| 方法与路径 | 请求 | 结果 |
|---|---|---|
| POST `/student/presence/open` | `OpenDevicePresenceRequest` | `DevicePresenceLease` |
| POST `/student/presence` | `UpdateDevicePresenceRequest` | `DevicePresenceReceipt` |

每次进程建立新的运行连接，服务器递增 `Generation`，使旧进程的迟到状态失效。运行内的 `Sequence` 严格递增；同序号同内容可重试，但不会重新延长最后通信时间；同序号不同内容拒绝。

允许页面：登录、选择考试、等待考试、正在答题、交卷确认、回收中、交卷反馈、已退出。下载状态：未下载、下载中、已下载、更新中、已更新、失败。登录页上报不能夹带上一个考生会话；考试状态必须属于该设备的真实学生会话。退出上报清除当前考生和考试，但不撤销设备激活；旧运行连接不能在退出后重新报告在线。

状态判定使用服务器接收时间。明确断开立即标离线；距最后有效通信达到 135 秒标离线；业务状态保持独立。现有参试 SignalR 连接断开也更新连接状态，旧连接的断开不能使新连接离线。

## 聚合监控和未回收名单

GET `/exams/{examId}/monitoring` 返回 `MonitoringSnapshot`，可按试场和人类可识别字段筛选。一次结果中的名单、进度、最终回执、失焦汇总、有效版本、指令及设备冲突来自同一 PostgreSQL 可重复读快照，避免同一次显示混用不同查询时刻的数据。

结果包括姓名、班级、准考证号、公开学生端编号、试场、业务状态、独立连接状态、页面、当前题目标识、已选答案、最后通信、已装及目标试卷版本、下载状态、回收回执、失焦汇总、修订复核、可补发的有效开始/结束指令，以及待裁决两端的信息。私人标记不在这些类型和结果中。

`ActualCount`、`CollectedCount` 和 `Uncollected` 始终针对完整的当前考试实际名单；界面筛选不改变全场人数。只有已持久保存的最终回执计入已回收，学生显示“正在交卷”不算。GET `/exams/{examId}/uncollected` 可单独取得未回收列表。零实际参试不产生收齐通知；历史通知继续使用原 `/submissions/collection/events` 和名单版本去重。

本次接口提供权威快照。实时消费与学生页面上报必须接通后才能验证端到端延迟，不能因为接口存在就声称 300 人现场监控已验收。原 `/hubs/exam-events` 是设备授权通道，不可把考务账号直接接到该设备通道或取消权限检查。

## 原子整体发布

| 方法与路径 | 说明 |
|---|---|
| GET `/publications/recipients` | 取得受管节点公开证书 DER，不含私钥 |
| POST `/exams/{examId}/publications/upload` | multipart 加密学生包与 `UploadExamBundleRequest` |
| POST `/exams/{examId}/publications` | 发布已经登记的加密资源，`PublishExamBundleApiRequest` |

上传表单恰有一个 `metadata` 字段和一个名为 `package` 的加密文件。不上传未加密学生 ZIP；临时文件缓冲只涉及密文。`metadata.Publication` 包含清单、独立答案、期望试卷及规则版本、变更题目、原因和实际预览确认；`metadata.Envelope` 是密钥信封。操作者仍从考务登录取得。使用现有服务配置中的受管节点证书，客户端不生成、替换或上传服务器私钥。

服务器不信任调用方声称“已编译、已加密、可供所有节点解锁”。它会实际解密验证学生包、显示清单及选项定位，核对试题结构、独立答案及满分规则，并使用已配置公开证书重新包装各受管节点的数据密钥。无实际预览确认、错误版本、遗漏坐标、被篡改密文或证书不可用时均不发布。

上传资源、试卷与规则两个有效版本、原有 `paper.publish` / `grading.publish` 事件、运行投影和幂等回执在同一数据库事务提交。第二阶段失败不会留下第一阶段生效或资源半保存。沿用既有重算与报表读取链路，不能分别调用两个旧发布入口并把一半成功当作整体成功。

请求取消发生在提交前不会激活新版本；网络在提交附近断开时，客户端不能假定失败，应保留同一请求号重试或查询当前版本。重复提交不重复发布或重算。受管节点私钥、原始题目、独立答案、解锁材料不进入回执、日志或仓库样例。

## CLI

现有 CLI 新增以下业务入口，考试和试场按名称选择，不要求操作者手工查内部 ID：

```text
roster list <考试名称>
roster import <考试名称> --input <JSON文件或->
roster update <考试名称> <准考证号> --input <JSON文件或->
roster delete <考试名称> <准考证号>
rooms list
rooms create <试场名称>
rooms rename <旧名称> <新名称>
rooms delete <试场名称>
rooms move <试场名称或未分组> --input <JSON文件或->
devices list [试场名称或未分组]
devices show <学生端编号>
devices history <学生端编号>
monitor show <考试名称>
monitor missing <考试名称>
publications publish <考试名称> --input <JSON文件或->
```

沿用 `--server` 和已有凭据环境约定，不把凭据写进示例。批量 JSON 使用契约字段；可带 `expectedVersion` 和稳定的 `requestId`。公开编号和班级、准考证号必须写成 JSON 字符串。`roster import` 校验失败返回非零退出码，即使服务器返回了有效的逐行预览结果。

## 已执行的检查

在隔离工作树与仅监听环回地址的临时 PostgreSQL 18 中执行：全解决方案 Release 构建 0 警告、0 错误；数据库回归 178 项通过、0 失败，5 项主备物理恢复测试因独立原生环境未配置跳过；新增整体上传、权限与考务接口组 25 项通过、0 失败、0 跳过。

这些检查覆盖名册原子性、并发版本、前导零、重试、身份更正、转组历史、空组删除、旧进程迟到状态、权限隔离、已回收口径、真实密文解密与重包装、multipart 上传、两个版本原子提交及故障回滚。没有把它们当作完整 GUI 流程、真实断电或 300 台设备的现场验收。
