# 给 A 的界面需求待办

核对日期为 2026-10-02，代码基线为已拉取的 `origin/main` 提交 `75ca7b7`。本文件收录本轮由 A 处理的学生端问题、需要 A 明确的公共接口和需求，以及由 A 跟进的服务管理事项。C 的 17 项业务工作单独放在[给 C 的待办](ui-requirements-c-20261002.md)，避免两边重复开发。

## A 直接处理的学生端问题

以下保留原任务关联供追溯，本轮按要求由 A 处理，不据此修改 GitHub 上的原模块分工。

- [ ] **1. 答题页采用最新考试结束时间。** 当前剩余时间仍由进入页面时的 `_exam.ScheduledEnd` 计算。收到时间调整后，应读取已校验的最新计划并更新显示，不能仅更新状态版本。保留当前 UTC+8 返回规范和只显示时分的格式，继续按服务器生命周期和可靠结束指令处理作答权限，不能因本地倒计时归零自行改变收卷规则。

  验证延长、提前结束、开始时间后移、乱序和重复通知、断线恢复。页面显示跟随最新计划，已开考不退回等待，已交卷不恢复作答。

  依据 [requirements.md](../../requirements.md) 第 45、174 行。定位到 [ExamInProgressPage.xaml.cs](../../src/Exam.Student/Views/ExamInProgressPage.xaml.cs) 第 608 行及 [ExamPackageManager.cs](../../src/Exam.Student/Core/ExamPackageManager.cs) 第 332 行。关联 [A06 #6](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/6)、[B06 #14](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/14)。

- [ ] **2. 等待页同步新的起止时间与倒计时。** 起止时间文字在构造页面时设置，倒计时继续使用最初的 `_exam.ScheduledStart`。将显示绑定到同一份最新有效时间状态，并保留服务端准入和开始规则。

  验证等待过程中前移、后移开始时间和修改结束时间，文字与倒计时同时更新，页面重建和重连后仍一致。若新开始时间已到，由服务器状态及解锁流程推进，不自行假定已经获准作答。

  依据 [requirements.md](../../requirements.md) 第 45、174 行。定位到 [WaitingExamPage.xaml.cs](../../src/Exam.Student/Views/WaitingExamPage.xaml.cs) 第 37、441 行。关联 [B02 #10](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/10)、[A06 #6](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/6)。

- [ ] **3. 调整首次进入考试的页面顺序与拒绝提示。** 选择页目前直接导航到等待页，随后才做准备、下载和准入检查。需求规定收齐加密包和考试说明后进入等待页并确认参试。应完成下载状态与页面切换接线，对服务器拒绝首次进入稳定显示“当前不可进入该考试！”。已经参试者的恢复和补传继续走原有恢复规则。

  验证未到进入时间、身份不在本场名册、考试已结束、已提交、资料未收齐、确认时准入失效，以及合法重连。下载或页面切换本身不能增加实际参试人数。

  依据 [requirements.md](../../requirements.md) 第 33、56 行。定位到 [ExamSelectionPage.xaml.cs](../../src/Exam.Student/Views/ExamSelectionPage.xaml.cs) 第 81～112 行，以及 [WaitingExamPage.xaml.cs](../../src/Exam.Student/Views/WaitingExamPage.xaml.cs) 第 124 行。关联 [B02 #10](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/10)、[A03 #3](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/3)。

- [ ] **4. 分清资料错误和准入错误，保留正确提示。** 等待页对准备失败统一显示“试题获取失败”，异常分支合并为试题或说明失败，刷新时又可能被通用连接提示覆盖。应分别显示“试题获取失败！”和“考试说明获取失败！”，准入拒绝使用规定文字。尚未答题时不出现“已作答内容已保存在本机”的说明。

  验证两个资源分别超时、下载中断、准入拒绝、重试成功和重复刷新。用户看到的主提示应与实际错误一致，刷新不能把资源或权限错误改成连接错误。

  依据 [requirements.md](../../requirements.md) 第 9、56 行。定位到 [WaitingExamPage.xaml.cs](../../src/Exam.Student/Views/WaitingExamPage.xaml.cs) 第 134、143、468～474 行。关联 [B02 #10](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/10)。

- [ ] **5. 登录页用考生能理解的证书错误说明。** 现有提示要求考生核对 `server.json` 和证书指纹。改为明确连接校验未通过并联系监考人员，技术细节保留在适当的诊断位置。不能为了简化提示而放松证书校验。

  验证证书不可信时仍拒绝连接，主界面不要求考生修改部署配置。

  依据 [requirements.md](../../requirements.md) 第 9 行。定位到 [LoginPage.xaml.cs](../../src/Exam.Student/Views/LoginPage.xaml.cs) 第 128、188 行。关联 [B01 #9](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/9)。

## A 先明确、C 随后接线的公共事项

这些事项来自 [#46](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/46) 的接线请求，以及 [PR #103](https://github.com/yeyiwen2006/huaizhou-exam-system/pull/103) 的草稿交接。以下基于当前主线代码核查，列出 A 需要补充的服务器端能力，C 在 A 完成对应接口后再接线 GUI 与业务 CLI。

| 事项 | 当前代码状态 | A 需要补充的内容 | C 随后负责 |
| --- | --- | --- | --- |
| C02 权威名册 | 只有单条参与者写入接口（`POST /versioning/exams/{examId}/participants`），无按考试全量查询或批量导入端点 | 补充按考试查询名册的 GET 端点、原子整批写入端点；语义：完全重复行跳过，整批失败全部回滚，前导零保留，明确并发修改规则 | 手工新增、Excel 预览与确认、按考试加载、失败保留旧状态和 CLI |
| C03 试场与设备关联 | 服务器无任何试场分组接口，框架层 `MapGroup` 和 SignalR `Groups` 均与业务分组无关 | 设计分组数据模型与迁移，补充分组增删改查、审批时分组校验、按学生端编号查设备、批量转组及操作历史端点，明确权限、版本和批量原子性 | 分组管理、审批选择、完整设备列表、筛选和转组流程 |
| C04 实时状态与通知 | SignalR Hub（`/hubs/exam-events`）和失焦汇总端点（`GET/POST /submissions/focus-events`）已存在；无聚合监控状态 REST 端点 | 补充聚合监控状态端点（设备、考生、作答进度、通信状态、指令确认与回收结果一次性查询）；明确实时事件定义、重连补取和版本处理规则。失焦汇总和收齐历史接口已有，C 直接复用 | 完整监控展示、筛选、裁决信息、回收统计、补传和实时提醒 |
| C05 原子发布与修订 | 试卷发布（`POST /versioning/exams/{examId}/paper-publications`）和评分规则发布（`POST /versioning/exams/{examId}/grading-publications`）为两个独立端点，无原子整体发布入口 | 确认是补充原子整体发布入口，还是明确由两个独立端点分别调用的失败边界与清理规则；明确与 D05 重算的衔接 | 导入编排、预览确认、发布、题目及规则修订、失败清理与结果展示 |

C01 旧评论所说的”完整考试配置尚未持久化”已经不适用于当前主线。现有 [ExamConfigurationCatalog.cs](../../src/Exam.Admin/ViewModels/ExamConfigurationCatalog.cs) 和 [ExamCatalogEndpoints.cs](../../src/Exam.Server/Api/ExamCatalogEndpoints.cs) 已有正式保存链路，剩余 UI 限制与回归见 C 文档。公共契约、权限、计分及迁移仍按 [AGENTS.md](../../AGENTS.md) 由 A 确认和审查。

## A 跟进的服务管理入口

- [ ] **跟进 #23 的 GUI 缺口，并同步交接信息。** 主窗口仍缺服务管理页面及业务服务停止时的管理入口。该工作不归 C。[F04 #23](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/23) 最新正文已在 2026-10-01 将主责由 F 改为 G，任务编号仍保留 F04；后端 F01、F02 继续提供服务管理和主备能力。

  A 负责跟进 G 的页面接入、与 C 的公共导航协调和审查，开发内容包括服务配置、启停、自检、日志、备份恢复，以及节点状态、隔离确认、接管和应急等入口。应验证业务服务停止时仍能通过管理通道登录并启动，失败不显示成功，单机保护状态持续可见。

  [docs/task-map.md](../task-map.md) 与 [AGENTS.md](../../AGENTS.md) 目前仍把 F04 写为 F 主责，和最新 Issue 不一致。需由 A 统一团队记录；本轮只记录该差异，没有擅自修改这些分工文件。依据 [requirements.md](../../requirements.md) 第 11 行及“主备服务器与接管”一节。

## A 需要统一的需求

- [ ] **确认考试卡片是否还显示“类别”。** [requirements.md](../../requirements.md) 第 116 行先规定“不另设考试类别”，随后又要求卡片显示类别。需先统一文字，再决定两个客户端的展示。这个矛盾尚未解决，现有类别显示暂不列作确定缺陷，也不让 C 猜测新的分类规则。

## 已完成和不重复安排的内容

圆钮按字形中心定位、实心蓝色与白色字母、固定显示比例、分值导航、单选与“不作答”保存后自动切题、左右键切题及交卷确认中的未答数量，已随 [PR #105](https://github.com/yeyiwen2006/huaizhou-exam-system/pull/105)、[PR #108](https://github.com/yeyiwen2006/huaizhou-exam-system/pull/108) 合并。服务器向两端返回 UTC+8 时间已随 [PR #106](https://github.com/yeyiwen2006/huaizhou-exam-system/pull/106) 合并，时间调整后页面仍使用旧计划的问题另列为本文件第 1、2 项。

最新主线 [PR #107](https://github.com/yeyiwen2006/huaizhou-exam-system/pull/107) 又加入失焦事件留存补报、考务失焦汇总、修订复核状态和修订题定位。本轮已据此修正之前的监控缺口表述，C 继续完成完整监控和实时刷新，不重复补这几项已有功能。

指定的红色退出与交卷按钮、等待页退出确认、交卷成功后三秒返回、学生等待页说明字体，以及考务收齐弹窗已有实现。后者仍需完成实时触发链路，已放入 C 文档。

## 本轮检查边界

两份文件覆盖上一轮列出的 5 项学生端问题、18 项考务端问题，并单列需求文字矛盾。考务端的 17 项归 C，服务管理由 A 跟进 G；公共接口事项是这些工作的依赖，不另算重复缺陷。

本轮实际核对 GitHub Issue 正文、相关评论、PR #103 草稿和最新主线源码，并检查 Markdown 引用、UTF-8 编码及差异。仅生成两份文档，没有修改业务代码，未重跑构建、模块测试、PostgreSQL、LaTeX 或现场测试。此前代码修改的通过记录不作为本轮待办完成的证明，也不代表 T01、T02、T03 验收完成。
