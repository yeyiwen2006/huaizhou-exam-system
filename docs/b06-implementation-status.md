# B06 现场异常与在线更新 — 实施状态

状态：**已完成**（本地验证通过，待推送 PR）
基线：main @ 607882e（含 PR #66/#69 合并及 C01/D03 后续合并）
工作副本：`remote/main-20260927/`
日期：2026-09-28

## 交付概要

学生端 B06 完整实现：普通权限霸屏与快捷键拦截、失焦检测补报、设备裁决界面与
撤销禁写、新题包原子切换、修题提示与复核记录。前置 B05（本地）与 A06（服务端
PaperUpdated 事件下发）均已就绪。

## 核心交付

### 1. 普通权限霸屏（`Core/KioskGuard.cs` 新增 + `MainWindow.xaml.cs` 接线）

- `WH_KEYBOARD_LL` 低级键盘钩子在**标准用户会话内**安装（无需管理员）：拦截
  Win（LWin/RWin）、Alt+Tab、Alt+Esc、Ctrl+Esc、Alt+F4（系统键）。
- 纯判定函数 `KioskGuard.Decide(vkCode, flags, isSystemKey)` 独立可测：
  Win/Alt+Tab → SuppressAndRefocus（拦截并重新聚焦主窗口），Alt+Esc/
  Ctrl+Esc/Alt+F4(系统键) → Suppress，普通按键放行；注入事件（LLKHF_INJECTED）
  放行避免输入法被误杀。
- 静态单实例管理（`Enable(Window)`/`Disable()`/`Retarget()`），钩子委托保持
  强引用防 GC；**安装失败不阻断考试**（降级为无钩子，文档注明边界）。
- `MainWindow`：考试激活态启用钩子并 `Topmost`，导航切换时保持；关闭在激活态
  被取消并重新最大化置顶。**Ctrl+Alt+Del 由 Windows 安全注意画面处理，用户态
  无法拦截**——此边界在需求文档注明，由考务端 `FocusLostCount` 监控兜底。

### 2. 失焦检测补报（`Core/FocusTracker.cs` 新增 + 同步服务扩展）

- `FocusTracker`：失焦次数/累计时长/最近复焦时间戳，注入 `TimeProvider` 可测；
  重复失焦不重复计数，页面卸载 `Close()` 收尾。
- `ExamInProgressPage` 挂接窗口 `Deactivated`/`Activated`；复焦立即 `KickAsync`
  触发同步，状态区显示累计失焦次数与时长。
- `SyncProgressRequest` 新增可选字段 `FocusLostCount` / `LastFocusRegainedAt`
  （向后兼容，旧调用方不传即可）；**版本未变但失焦计数变化时强制补报**（新
  RequestId），与常规 60 秒进度上报共用幂等语义。

### 3. 设备裁决界面与撤销禁写

- 等待页：`EntryPreparationStatus.DeviceDecisionPending` 显示"设备冲突待裁决"，
  每 15 秒自动重试准备（`MaybeRetryArbitration`）。
- 考试页：管理器 `Revoked` 后显示撤销遮罩（禁止继续作答，提供"返回选择考试"）。
- **被撤销设备不能写有效答案**：`DeviceDecided` 撤销后 `AnswerSession` 进入
  `PermissionDenied`，所有写入被拒绝；本地已记录答案保留不删除。

### 4. 新题包原子切换（`Core/ExamPackageManager.cs` + `PaperUpdateCoordinator.cs` 新增）

`ApplyPaperUpdateAsync` 六步流程，**任一步失败旧版明文与状态原样保留**：

1. 重新 `PrepareEntryAsync` 取新版描述符（校验版本前进且不超 `NewestPaperVersion`、
   参试/设备一致）；
2. 说明版本变化时取新说明；
3. `DownloadUpdatePackageAsync`：`.part` 临时文件 + 长度校验，不影响旧版文件；
4. 重新 `ConfirmParticipationAsync` 取新 `UnlockReference`；
5. 解锁 + 解密 + 新清单解析（失败不切换，暂存明文清零）；
6. `_plaintextGate` 内代次（`ControlGeneration`）核对后一次性切换明文并落盘，
   旧明文 `ZeroMemory`。

`AnswerSession.SwitchPaperVersionAsync`：同一参试/设备/授权下仅版本提升切换，
**答案、标记、当前题全部结转**（不清空不重建），新结构须通过
`AnswerSheetContract.Validate`，拒绝回退与已固定会话。

### 5. 更新结果补报与编排（`Core/PaperUpdateCoordinator.cs` 新增）

- 收到 `PaperUpdated` 事件入队，后台循环驱动：等待页阶段轮询等重新准备推上版本；
  作答阶段执行原子切换；**成功报 `Applied` + 实际版本，失败报
  `ResourceUpdateFailed` + 原因**（对应 A 侧 `ReliableCommandService` 校验
  `ActualPaperVersion >= ResourceVersion` 的契约）。
- 失败按 30s→120s 指数退避自动重试；`Applied` 确认失败进入补发清单
  （`_unackedApplied`）直到服务器确认；终态（交卷/撤销/结束）不再应用更新。
- 全程不阻塞作答；事件经 UI 线程封送。

### 6. 修题提示与复核记录（SQLite v2）

- 协调器切换成功后 `RecordInstalledPaperUpdate`（内存态）+ `UpdateInstalled`
  事件（UI 弹确认对话框 + 横幅"试题已修订，请复核"）。
- `LocalStateStore` schema v1→v2：新增 `paper_update_review` 表；v1 库自动迁移
  （`PRAGMA user_version` + `.identity` 文件双判定，临时文件 + `File.Replace`
  原子更新身份文件），**只增表不动既有答案数据**。
- `ConfirmPaperUpdateReviewAsync`：考生点击"已复核修订试题"落库；`ON CONFLICT
  DO NOTHING` 保证**同一版本首次确认后重复确认不覆盖原确认时间**。

## 测试（+11，验收判据逐条对应）

`tests/Unit/Exam.StudentUnitTests/PaperUpdateTests.cs`（10）+
`SubmissionSyncServiceTests.cs`（1）：真实双版本合成加密包
（`UpdateFixture` + `VersionedPackageApi`，支持截断下载/拒绝解锁注入）。

| 验收判据 | 测试 |
|---|---|
| 霸屏按键判定（拦截逃逸组合、放行普通键） | KioskBlocksExitShortcutsButAllowsNormalKeys |
| 失焦计数/时长/复焦时间戳；补报换新 RequestId | FocusTrackerCountsLossesAndDurations；FocusLossIsReportedPromptlyWithNewRequestId_AndRegularSyncSkipsIt |
| **原答案标记不被清空**（版本结转） | SwitchPaperVersionCarriesAnswersMarksAndCurrentQuestion |
| 切换拒绝回退/结构不符/已固定 | SwitchPaperVersionRejectsRegressionAndInvalidStructureAndNonActiveSessions |
| **新包失败仍可恢复旧包**（下载失败重试成功 + 补报语义） | ManagerUpdateFailureKeepsOldPackageAndSucceedsAfterRetry |
| 解锁失败保留旧包 | ManagerUpdateUnlockFailureKeepsOldPackage |
| 协调器端到端（结转 + Applied 补报 + 复核记录不覆盖） | CoordinatorAppliesUpdate_PreservesAnswersAndRecordsReview |
| **被撤销设备不能写有效答案**（本地记录保留） | DeviceDecisionRevocationStopsAnsweringAndKeepsLocalRecord |
| 待裁决状态可见直到解决 | DeviceDecisionPendingIsVisibleUntilResolved |
| v1→v2 迁移保留复核记录 | LegacyV1DatabaseMigratesToV2AndKeepsReviewRecords |

## 验证结果

基于 main @ 607882e 基线（含 C01/D03）+ B06 改动全量复验：

- 全解决方案 Debug / Release 构建 **0 警告 0 错误**
- **学生端 266/266**（含 B06 新增 11 个 + D03/C01 新增互操作测试）、身份
  25/25、管理 137/137（含 C01 新增 ScheduleReply 边界测试）全绿
- Exam.UnitTests 1138/1141：3 个失败均为本机环境问题（2 个符号链接权限 +
  1 个缺 TeXLive 的 Latex 分类测试），与 B06 代码无关
- CI 条件：`restore --locked-mode` OK + Release 构建 0 警 0 错（无新增 NuGet
  包，锁文件无变化）
- 启动冒烟通过（DOTNET_ROOT=D:\dotnet，Debug 与 Release 均正常显示主窗口
  "怀籀传承在线考试系统 · 学生端"且响应）

## 实施说明与注意点

1. **契约扩展向后兼容**：`SyncProgressRequest` 新增两个可选可空字段，服务端
   旧版本不感知；服务端是否持久化失焦数据及考务端展示请 A/C 接续。
2. **复核确认暂无上报通道**：服务端 `PaperUpdateState.ReviewRequired` 已建模，
   但学生端"已复核"确认目前仅落本地（`paper_update_review` 表）。**请 A 确认
   是否需要在 `AcknowledgeCommandRequest` 或新端点上补充确认上报字段**，B 侧
   随时接续。
3. **修复了一个设计缺陷**：复核状态最初依赖 UI 事件订阅记录，无 UI 场景下丢失；
   已改为协调器直接调 `RecordInstalledPaperUpdate`（事件仅作 UI 提示），单元
   测试覆盖该路径。
4. `IStudentApi` 无新增方法（更新流程复用既有准备/确认/解锁/确认命令端点）。
5. KioskGuard 边界：Ctrl+Alt+Del 与 Win 安全画面无法在用户态拦截，属系统限制，
   由失焦补报 + 考务端监控闭环。

## 下一步

- B07（#15 学生端发布）：B01–B06 全部就绪后可开始
- 推送：需一次 GitHub 设备授权，分支建议 `codex/b06-exam-anomalies`
- 契约问题（见注意点 1/2）随 PR 一并向 A 提出
