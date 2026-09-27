# B05 同步和交卷 — 实施状态

状态：**已完成**（本地验证通过，待推送 PR）
基线：main@8cee73c + A 的 B04 适配（12af637）
工作副本：`remote/main-20260927/`
日期：2026-09-27

## 交付概要

学生端 B05 完整实现：把 B04 的可靠本地状态（待发送快照、固定提交记录）接入真实服务器
同步与交卷链路。**只有取得服务器持久化回执才显示交卷成功**；所有重试沿用首次请求标识，
依赖 A05 服务端幂等重放保证同一提交不重复计次。

## 核心交付

### 1. `Core/SubmissionSyncService.cs`（新增）

常驻同步服务，一个串行回合门（SemaphoreSlim）调度两类工作，回合间可被唤醒提前执行：

- **进度上报**（作答中，每 60 秒）：当前可靠答案快照 → `PUT /submissions/progress`。
  同一 AnswerVersion 的所有重试沿用同一 RequestId（版本推进才换新 ID），
  服务器幂等重放不会产生重复进度；本地修改唤醒运行时事件即时触发。
- **固定提交上传**（交卷后，3 秒起步指数退避至 60 秒）：B04 固定记录 →
  `POST /submissions/final`，**PaperVersion/EntryVersion/AuthorizationVersion 以固定记录
  自身为准**（防止上传后版本切换造成不一致），RequestId/SubmissionId 沿用 B04 落盘值。
- **回执丢失恢复**：响应在传输中丢失时，下一轮原请求标识重发 → 服务器幂等重放返回
  原回执；`ALREADY_SUBMITTED`/`VERSION_CONFLICT` 冲突时走 `GET /submissions/receipts`
  查询恢复原结果。
- **回执校验**：SubmissionId/ParticipationId/PaperVersion 与本地固定记录一致才进入
  `Confirmed`；不一致进入 `Blocked`（服务器已持有不同提交，停止自动重试，本地记录保留
  不覆盖）。
- 状态机：`Idle → Progressing / Uploading → WaitingRetry / Confirmed / Blocked`，
  `StatusChanged` 事件封送 UI 线程。

### 2. `IStudentApi` / `HttpStudentApi`（扩展）

新增三个方法对接 A05 端点（服务端返回已确认为 `ApiEnvelope` 直包）：

- `SyncProgressAsync` → `PUT /api/v1/submissions/progress`
- `SubmitFinalAnswersAsync` → `POST /api/v1/submissions/final`
- `GetFinalSubmissionAsync` → `GET /api/v1/submissions/receipts?...`（按参试+设备查询）

### 3. `Views/ExamInProgressPage`（接线）

- 构造时创建同步服务并启动 `RunAsync` 循环；卸载时取消并释放。
- **提前交卷与 END 走同一入口**：确认对话框 → `AnswerSession.FixSubmissionAsync`（B04
  本地固定，未保存修改与固定记录同事务）→ `KickAsync` 立即上传 → 取得回执才跳转成功页；
  未取得回执时明示"已冻结、正在提交、失败自动沿用原提交重试"，不显示成功。
- 状态区新增同步状态行（`SyncStatusText`）；`Confirmed` 事件触发自动跳转（3 秒成功反馈
  只在可信回执后出现）。
- 已有固定提交时交卷按钮直接拒绝（再次进入拦截的服务端核对由 SubmittedPage 回执登记承担）。

### 4. 时钟（既有，本次确认）

60 秒校时 + 首次 45–60 秒抖动由 `AppServices.RunClockAsync`/`ServerClock` 承担
（A 已实现），页面时钟显示与剩余时间推算全部基于服务器时间，改系统时钟不影响考试状态。

## 测试（+8，验收判据逐条对应）

`tests/Unit/Exam.StudentUnitTests/SubmissionSyncServiceTests.cs`：真实 SQLite + 真实包
构建 + 可编程提交 API fake，用 `KickAsync` 单回合驱动保证确定性。

| 验收判据 | 测试 |
|---|---|
| 进度幂等上报（稳定请求标识、已同步版本跳过） | ProgressUploadUsesStableRequestIdPerVersion |
| 网络失败重试沿用同一请求标识 | ProgressRetryAfterNetworkFailure |
| 固定提交一次性上传+稳定标识+仅回执确认 | FinalSubmissionUploadsOnceWithStableRequestId |
| **丢回执恢复原结果**（响应丢失→原请求重放→原回执） | ReceiptLostInTransit_ReplayWithOriginalRequest |
| 服务器已持有提交→查询恢复回执（不产生新提交） | ServerAlreadyHoldsSubmission_QueryRecovers |
| 服务器冲突记录→阻断不覆盖，本地保留 | ConflictingServerSubmission_Blocks |
| **结束通知前修改保留**+结束后停止上报 | EndArrivesAfterLocalEdits |
| 已固定会话（含重启后）不再上报进度 | FixedSessionNeverReportsProgress |

## 验证结果

- 全解决方案 Debug 构建 **0 警告 0 错误**
- **学生端 142/142**（134 既有 + 8 新增）、身份 25/25、管理 76/76、负载 22/22 全绿
- 集成 88 失败 / 单元 1 失败均为本机缺 PostgreSQL/XeLaTeX 的既有环境问题（与同步基线
  一致，非本次代码引起）
- CI 条件：`restore --locked-mode` OK + Release 构建 0 错误（本次无新增 NuGet 包，
  锁文件无变化）
- 启动冒烟通过（DOTNET_ROOT 指向 D:\dotnet，主窗口正常显示）

## 实施说明与注意点

1. **接口变更影响**：`IStudentApi` 新增 3 方法，`Exam.StudentIdentityTests` 的
   RecordingIdentityApi 已同步补齐（NotSupportedException 占位）。
2. **服务仅消费 B04 落盘事实**：固定提交一律读 `AnswerSession.FixedSubmission`（已是
   冻结副本），`HasPendingSubmission`（固定意图未落盘）时等待 B04 重试，不代为固定。
3. **Blocked 状态是终态设计**：服务器回执与本地记录不一致（如他人代交）时停止自动
   重试，避免覆盖任何一方的原始记录，转人工核对——符合"丢回执恢复原结果、不能重复
   计次"的判据精神。
4. **进度上报的参与数据**取自 `Manager.Participation`（服务器确认的参试上下文），
   不使用本地推测值。

## 下一步

- B03 原版题目与选项（#11）：D03 契约已定稿，可按契约先行实现渲染与圆钮交互
- B06（#14）依赖本任务合并；B07（#15）依赖 B01–B06 全部
- 推送：需一次 GitHub 设备授权，分支建议 `codex/b05-sync-submission`
