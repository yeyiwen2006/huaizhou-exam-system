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

允许页面：登录、选择考试、等待考试、正在答题、正在答题（已切出）、本地保存失败、交卷确认、回收中、交卷反馈、已退出。下载状态：未下载、下载中、已下载、更新中、已更新、失败。登录页上报不能夹带上一个考生会话；考试状态必须属于该设备的真实学生会话。退出上报清除当前考生和考试，但不撤销设备激活；旧运行连接不能在退出后重新报告在线。

学生端正式入口使用 `StudentPresenceReporter` 建立租约并串行上报。页面、登录会话、作答状态及失焦变化立即触发；下载阶段由 500 毫秒状态检查补充；不变状态每 60 秒完整补报。打开租约的回执丢失时沿用原请求号；旧进程被服务器拒绝后不再抢占新租约。状态上报回执不代表答案已保存，答案同步和最终交卷仍走各自的持久化回执。

状态判定使用服务器接收时间。明确断开立即标离线；距最后有效通信达到 135 秒标离线；业务状态保持独立。现有参试 SignalR 连接断开也更新连接状态，旧连接的断开不能使新连接离线。

## 聚合监控和未回收名单

GET `/exams/{examId}/monitoring` 返回 `MonitoringSnapshot`，可按试场和人类可识别字段筛选。一次结果中的名单、进度、最终回执、失焦汇总、有效版本、指令及设备冲突来自同一 PostgreSQL 可重复读快照，避免同一次显示混用不同查询时刻的数据。

结果包括姓名、班级、准考证号、公开学生端编号、试场、业务状态、独立连接状态、页面、当前题目标识、已选答案、最后通信、已装及目标试卷版本、下载状态、回收回执、失焦汇总、修订复核、可补发的有效开始/结束指令，以及待裁决两端的信息。私人标记不在这些类型和结果中。

`MonitoringParticipant.CurrentQuestionNo` 是新增的可空正整数，来源为同一快照中的已发布试卷结构。`CurrentQuestionId` 仍用于查询答案；界面只展示正式题号，旧服务器缺少新字段时提示题号未提供，不把内部标识当成题号。设备状态还须匹配本次参试的考生会话；最终答案优先读取持久化答卷，不会被较早的进度记录覆盖。

手动重发通过原 `/commands/{eventId}/resend` 接口。在持久确认完成后推送到原参试组，学生端仍核对考试、设备、授权与服务代次。已执行的结束指令允许对已结束但未回收的参试请求补传；保留原 END 和原答卷标识、原指令确认，不恢复作答，也不重新加入自动指令投递。已回收、撤权或已拒绝的指令不可用此方式补传。

`ActualCount`、`CollectedCount` 和 `Uncollected` 始终针对完整的当前考试实际名单；界面筛选不改变全场人数。只有已持久保存的最终回执计入已回收，学生显示“正在交卷”不算。GET `/exams/{examId}/uncollected` 可单独取得未回收列表。零实际参试不产生收齐通知；历史通知继续使用原 `/submissions/collection/events` 和名单版本去重。

本次接口提供权威快照。实时消费与学生页面上报必须接通后才能验证端到端延迟，不能因为接口存在就声称 300 人现场监控已验收。原 `/hubs/exam-events` 是设备授权通道，不可把考务账号直接接到该设备通道或取消权限检查。

## 原子整体发布

| 方法与路径 | 说明 |
|---|---|
| GET `/publications/recipients` | 取得受管节点公开证书 DER，不含私钥 |
| POST `/exams/{examId}/publications/upload` | multipart 加密学生包与 `UploadExamBundleRequest` |
| POST `/exams/{examId}/publications` | 发布已经登记的加密资源，`PublishExamBundleApiRequest`（不带源材料归档，见下） |
| GET `/exams/{examId}/paper-materials?version=N` | 按试卷版本取回源材料归档（考务会话），返回 ZIP 与归档哈希 |

上传表单有一个 `metadata` 字段和**两份密文**：`package`（加密学生包）与 `materials`（加密源材料归档，
见 [逐题导入包规范](import-package-spec.md) 第 9 节）。源材料归档里含独立答案，因此和包一样必须先加密再上传——
不上传任何明文学生 ZIP、题目源文件或答案文件，临时文件缓冲只涉及密文。`metadata.Publication` 包含清单、独立答案、期望试卷及规则版本、变更题目、原因和实际预览确认；`metadata.Envelope` 是密钥信封。操作者仍从考务登录取得。使用现有服务配置中的受管节点证书，客户端不生成、替换或上传服务器私钥。

服务器不信任调用方声称“已编译、已加密、可供所有节点解锁”。它会实际解密验证学生包、显示清单及选项定位，核对试题结构、独立答案及满分规则，并使用已配置公开证书重新包装各受管节点的数据密钥。无实际预览确认、错误版本、遗漏坐标、被篡改密文或证书不可用时均不发布。

上传资源、试卷与规则两个有效版本、原有 `paper.publish` / `grading.publish` 事件、运行投影和幂等回执在同一数据库事务提交。第二阶段失败不会留下第一阶段生效或资源半保存。沿用既有重算与报表读取链路，不能分别调用两个旧发布入口并把一半成功当作整体成功。

请求取消发生在提交前不会激活新版本；网络在提交附近断开时，客户端不能假定失败，应保留同一请求号重试或查询当前版本。重复提交不重复发布或重算。受管节点私钥、原始题目、独立答案、解锁材料不进入回执、日志或仓库样例。

`metadata.Publication.MaterialsSha256` 是**明文归档**的 SHA-256，服务器按收到的字节重算并比对；
它同时进入发布请求指纹，因此同一请求号重试但材料内容不同会被判为幂等冲突而不是被接受。
归档与版本在**同一事务**提交：不存在「版本发布了但没有归档」或反之的半套状态。

开考后修订时 `metadata.Publication.ChangedQuestionIds` 由服务器按上一已发布版本的逐题摘要核对：
漏报实际变更的题（`CHANGED_QUESTION_IDS_MISSING`）与把未变的题报成变更（`CHANGED_QUESTION_IDS_UNEXPECTED`）
都拒绝；材料没有任何内容变化时拒绝走试卷修订（`NO_CONTENT_CHANGE`，只改答案请用规则修订入口）；
上一版本没有归档时失败关闭（`MATERIALS_BASELINE_MISSING`）。归档不可读、超限或与自身清单不符报
`MATERIALS_ARCHIVE_INVALID`，格式版本不被支持报 `MATERIALS_FORMAT_UNSUPPORTED`。

取回端点只接受**考务登录会话**（不是设备凭据路由），响应带 `X-Exam-Materials-Sha256`、
`X-Exam-Materials-File-Count` 与服务器身份头，`Cache-Control: no-store`，内容按流返回。
归档含独立答案，属敏感出口：调用方应只在需要继续修题时取回，并遵守既有的材料本地保存约定。

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
monitor stream <考试名称> [间隔秒] [轮数]
monitor watch <考试名称> [起始名单版本]
monitor follow <考试名称> [起始名单版本] [间隔秒] [轮数]
monitor events <考试名称> [起始名单版本] [间隔秒] [轮数]
activation pending
activation approve <申请标识> [试场名称]
activation revoke <学生端编号> --input <JSON文件或->
decisions keep <考试名称> <学生端编号> --input <JSON文件或->
commands resend <考试名称> <学生端编号> start|end
commands reupload <考试名称> <学生端编号>
publications publish <考试名称> --input <JSON文件或->
```

`monitor stream` 默认每秒连续输出完整权威快照；`monitor events` 按指定间隔读取收齐、设备冲突及失败通知，失败包含下载/修订下载、本地保存和指令资源更新。一次读取失败返回非零，持续失败不计入成功轮数。二者拒绝迟到或版本回退的数据，并在逻辑服务器变化或服务代次回退时终止。

首次激活未分配编号，`activation approve` 使用 `activation pending` 返回的申请标识。撤销、裁决、重发与补传按公开学生端编号解析目标；裁决操作者取当前登录身份。新写命令在发送前加密保存解析后的原请求，丢回执后使用同一 `--request-id` 和原输入重试，不重新选择版本、设备或指令。完整输入、输出及跨进程边界见 [考务CLI操作说明](c04-cli-business-commands.md)。

`monitor watch` 读取一次收齐事件后退出（可给位置参数指定起始名单版本，默认 0）；`monitor follow` 持续等待并发出，
位置参数依次是起始名单版本（默认 0，用于重启后不重播历史）、间隔秒数（默认 1，允许 1..3600）与轮数（默认不限，
给了轮数则成功完成指定轮数后正常退出）。两者都把「本场考试全部答卷已成功回收」事件以**一行一个 JSON** 写到 stdout 便于脚本消费；
文本错误格式下，可读提醒实时写 stderr。指定 --error-format json 时，事件仍实时写 stdout，stderr 在失败时只写一个 JSON 错误信封；成功结束时才输出累积的可读诊断。持续失败不计入成功轮数，总超时返回 5，用户取消返回 130。事件游标在进程内保存；重启时传入上次已处理的名单版本，避免重播历史。

与考务端弹窗同一端点、同一校验口径：**每个名单版本只发出一次，零参试者不触发**，
事件标识与名单版本冲突或考试不符时整页拒绝、游标不前进。持续监控遇到整页拒绝、损坏或缺失的 JSON 结果、网络异常或 5xx 时
**提示后继续按间隔重试**（与考务端弹窗一致），鉴权或请求本身无效则立即以对应退出码失败；`monitor watch` 属一次性调用，整页拒绝按用例错误退出。

沿用 `--server` 和已有凭据环境约定，不把凭据写进示例。批量 JSON 使用契约字段；可带 `expectedVersion` 和稳定的 `requestId`。公开编号和班级、准考证号必须写成 JSON 字符串。`roster import` 校验失败返回非零退出码，即使服务器返回了有效的逐行预览结果。

### 只改答案与计分规则

开考后在**不改试卷内容**的前提下改答案或计分规则，复用既有规则发布端点
（`POST /api/v1/versioning/exams/{examId}/grading-publications`），不重新编译、不组包：
材料包里的 `manifest.json` 与完整参考答案由服务器重新校验，且必须与已发布试卷的结构一致。

先用 `import materials <考试名称> --output <新材料目录>` 取回本场工作副本，在该目录修改完整答案或计分字段。
CLI 分成不可混用的核对、确认两步：

```text
grading revise <考试名称> --package <材料包目录> --reason <修改原因> --output <新规则预览目录>
grading publish <规则预览目录> --confirm
grading status <考试名称>
```

`revise`（也可用 `preview`）只检查、保存，不发布也不调用 TeX。逐题答案和满分在仅当前账号可访问的 `preview.md` 中核对，终端不输出答案。
`publish` 固定预览中的服务器、考试、账号、规则版本、材料、修改原因及请求编号。重复执行同一目录安全重放；网络超时不能据此认定未提交，不能重新核对生成另一请求代替重试。并发版本冲突由服务器拒绝后才重新取回核对。

考务端在「只改答案与计分规则」卡片依次选择目录、填写原因、检查并核对逐题答案与满分、确认发布。修改输入会使未发送的预览失效；已尝试而回执未确认的预览保持不变。切换考试或连接不会让旧异步操作写入新考试，也不会显示成新考试的成功结果。

- **入口分别显示三种状态**：① 发布成功（服务器已切换规则版本，回执给出规则版本与试卷版本）；
  ② 重算中（还有答卷未按该版本计分，附未完成份数与阻断原因）；③ 结果就绪（全部最终答卷已按该版本计分）。
  只有「发布成功」不表示成绩已重算完。
- **没有对外的「触发重算」接口**：发布提交后由服务器内部及时安排一轮重算，后台重算循环负责补偿；
  即时调度采用 3 秒取消期限；调度失败或取消不回滚已提交的发布，不改变已确认发布回执，只记不含材料正文的日志并等下一轮循环。该行为同时覆盖规则发布、整体上传及兼容 JSON 整体发布入口。
- `GET /api/v1/exams/{examId}/grading-materials` 只向已登录考务返回当前有效规则源材料与试卷/规则版本，禁止缓存；学生设备凭据不能访问。当前事件损坏时拒绝，不返回旧答案替代。
- 取回工作副本时，会在原试卷源材料上补入当前独立修订后的完整答案与分值；历史归档不改写。随后改题只保留当前规则，服务器在同一版本事务内拒绝旧归档将规则覆盖回旧版。更改答案/计分应继续使用独立规则入口。
- 重算期间旧报表**不可查询或导出**：既有报表门要求每份成绩版本与已发布规则版本精确相等，
  否则返回 `REPORT_NOT_READY`（见成绩统计一节）。

## 已执行的检查

在隔离工作树与仅监听环回地址的临时 PostgreSQL 18 中执行：全解决方案 Release 构建 0 警告、0 错误；数据库回归 178 项通过、0 失败，5 项主备物理恢复测试因独立原生环境未配置跳过；新增整体上传、权限与考务接口组 25 项通过、0 失败、0 跳过。

这些检查覆盖名册原子性、并发版本、前导零、重试、身份更正、转组历史、空组删除、旧进程迟到状态、权限隔离、已回收口径、真实密文解密与重包装、multipart 上传、两个版本原子提交及故障回滚。没有把它们当作完整 GUI 流程、真实断电或 300 台设备的现场验收。
