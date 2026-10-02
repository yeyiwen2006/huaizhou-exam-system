# 给 C 的界面需求待办

核对日期为 2026-10-02，代码基线为已拉取的 `origin/main` 提交 `75ca7b7`。本文件把五页面需求核对中属于 C 的 17 项剩余工作，归回现有 GitHub Issue，供 C 继续开发、A 审查。

## A 接口交付更新（2026-10-02）

下述原评审要求 A 补充的服务端接口已实现，详细契约、权限、事务边界和 CLI 见 [考务管理接口](../administration-api.md)。包括分考试名册查询和原子批量导入、身份维护、试场持久化与整批转组、按公开编号查询完整设备目录、带代次的设备状态、聚合监控和未回收名单、真实加密上传及试卷/计分规则原子整体发布。新增 migration 随正常迁移流程执行，不写入示例考生或凭据。

接口已通过隔离 PostgreSQL 与 HTTP 边界回归；这不代表下面的 GUI 接线、实时消费或真实多机验收已完成。原设备 SignalR Hub 仍是设备授权通道，不可为考务端复用而放开设备鉴权。A 主备与服务管理模块仍不计入本次整改。

## 当前任务与已有实现

| 跟踪位置 | 当前状态 | 本文件对应范围 |
| --- | --- | --- |
| [C01～C04 剩余业务实现与接线 #46](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/46) | 开放，C 主责 | 考试设置、名册、激活分组、监控和人工裁决 |
| [C05 导入和考试中修改 #21](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/21) | 开放，C 主责 | 导入、预览、发布、考试中修订和重算入口 |
| [C 模块草稿 PR #103](https://github.com/yeyiwen2006/huaizhou-exam-system/pull/103) | 开放草稿，尚未合入本次基线 | 已提出目录、ZIP、单清单的只读结构预览，仍未完成编译、入库和发布 |

C01～C04 的旧卡 #17～#20 因合并跟踪而关闭，剩余工作统一记在 #46。不能把旧卡关闭理解为对应功能全部完成，也无需重新打开重复卡。

以下能力已存在，后续应复用。完整考试配置已通过正式考试 API 保存到服务器；激活审批、设备查询、换机裁决、指令重发及收齐历史补取已有实现。最新主线还加入了失焦次数与时长汇总、修订复核状态。它们仍有展示及实时接线缺口，但不应重新开发成另一套业务。

需要 A 确认的公共接口与权限列在[给 A 的待办](ui-requirements-a-20261002.md)。服务与主备管理入口按 #23 的当前 F 分工处理，不计入 C 的开发范围或本次非 F 整改。

## C01 考试设置与说明

依据 [requirements.md](../../requirements.md) 第 9、116、131 行，跟踪到 [#46](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/46)。

- [ ] **1. 开考后禁止编辑科目和题组。** 当前科目、题组表格及增删按钮仍可操作，保存时却会跳过这些结构修改；若同时改了时间，还可能提示“考试设置已保存”。应统一禁用不可变字段和命令，明确实际保存的内容。完成时需验证开考前能修改，开考后只允许需求规定的操作，禁止修改的草稿不会被悄悄忽略后显示成功。

  定位到 [MainWindow.xaml](../../src/Exam.Admin/MainWindow.xaml) 第 225、240 行，以及 [ExamConfigurationCatalog.cs](../../src/Exam.Admin/ViewModels/ExamConfigurationCatalog.cs) 第 166、179 行。

- [ ] **2. 统一考试说明预览字体。** 考务预览目前是普通文本框，未区分黑体正文与 Times New Roman 数字。应继续从结构化配置生成说明，并复用合适的显示组件。验证中文、数字、负分和规则表达式显示正常，不从预览文字反推计分配置。

  定位到 [MainWindow.xaml](../../src/Exam.Admin/MainWindow.xaml) 第 282 行。学生端现有 [InstructionTextRenderer.cs](../../src/Exam.Student/Core/InstructionTextRenderer.cs) 可作为字体处理的参考，复用共享组件时由 A 确认边界。

## C02 考生名册

依据 [requirements.md](../../requirements.md) 第 29～31、116 行，跟踪到 [#46](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/46)。A 已补充 `GET /exams/{examId}/roster`、`POST /exams/{examId}/roster/import` 及身份更正、删除接口（统一 `/api/v1` 前缀），C 应接入权威名册版本。批量写入规则：完全重复行跳过，整批写入失败全部回滚，不允许部分写入。

- [ ] **3. 名册真正保存到服务器。** 当前手工新增和 Excel 确认导入只修改内存集合，关闭考务端即丢失。接入服务器查询和写入，复用现有格式校验及预览。验证重启后仍可读取、前导零保留、重复行与冲突行按统一规则处理、整批失败没有部分写入，失败时不显示导入成功。

  定位到 [RosterViewModel.cs](../../src/Exam.Admin/ViewModels/RosterViewModel.cs) 第 13、65、125 行。

- [ ] **4. 名册随当前考试切换。** 主窗口只创建一个名册对象，打开考试时没有给它切换考试上下文。应按考试加载权威名册，并在切换考试、服务器或退出账号后清理旧预览和旧结果。验证从考试甲切到考试乙时不显示或提交甲的名单，迟到的甲考试响应不能覆盖乙。

  定位到 [MainViewModel.cs](../../src/Exam.Admin/ViewModels/MainViewModel.cs) 第 33、81 行。

## C03 激活与试场分组

依据 [requirements.md](../../requirements.md) 第 17、23、25 行，跟踪到 [#46](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/46)。A 已补齐试场分组存储、迁移、版本化增删改、整批设备转组、操作历史和公开编号设备查询；C 按 [接口约定](../administration-api.md) 接线。

- [ ] **5. 批准激活时能选择真实试场。** 当前输入被禁用，批准请求固定传空分组，设备只能先归入未分组。接入服务器维护的分组选择，并在批准时保存关联。未指定时明确显示“未分组”，失效分组不能提交。保留现有幂等批准和重复回执保护。

  定位到 [MainWindow.xaml](../../src/Exam.Admin/MainWindow.xaml) 第 505 行，以及 [ActivationRoomViewModel.cs](../../src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs) 第 227～241 行。

- [ ] **6. 分组管理持久化并补齐转组。** 现有增删改只维护本机集合，缺少单个与批量转组、分组筛选、操作历史和服务器对非空组删除的校验。应统一由服务器判断当前关联，不能仅检查本页已查到的设备。验证刷新或重启后保留结果，批量失败符合已确认的原子性规则，转组不改变编号、考生、参试记录和成绩。

  定位到 [ActivationRoomViewModel.cs](../../src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs) 第 187、197 行，以及 [MainWindow.xaml](../../src/Exam.Admin/MainWindow.xaml) 第 541 行。

- [ ] **7. 按学生端编号查询设备。** 当前查询框和接口调用要求内部设备标识。正常查询应接受需求中的学生端编号，并由服务器解析到设备；内部标识仅用于实现和必要排查。验证完整编号、未找到和权限不足三类结果。

  定位到 [ActivationRoomViewModel.cs](../../src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs) 第 256～265 行。

- [ ] **8. 加载完整设备列表并展示真实状态。** 当前集合主要积累本次查询和批准的设备，当前考生、连接状态、最后通信时间使用固定占位值。需加载已激活设备，补齐编号、试场、激活状态、连接状态、姓名、班级、准考证号、所在考试和最后通信时间，明确未登录及未分组。验证退出、换人登录、转组和断连后显示正确。

  定位到 [ActivationRoomViewModel.cs](../../src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs) 第 269～273 行，以及 [MainWindow.xaml](../../src/Exam.Admin/MainWindow.xaml) 第 526 行。

## C04 监控、裁决与答卷回收

依据 [requirements.md](../../requirements.md) 第 35、178～184 行，跟踪到 [#46](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/46)。失焦汇总与设备 SignalR 通道已存在。A 已新增 `GET /exams/{examId}/monitoring`、`/uncollected` 和设备状态上报，快照同事务读取名册、设备、答案、失焦、回执、冲突和有效指令。考务实时消费必须保持独立的考务权限，不可直接接入设备专用 Hub。

- [ ] **9. 补齐监控字段。** 当前表格仍以内部参试、考生和设备标识为主，缺少姓名、班级、准考证号、学生端编号、试场、当前页面、题号及已选答案、实际试卷版本、最后通信和指令确认等信息。最新主线已有失焦汇总和修订复核列，应保留并接入统一刷新。验证字段来自真实业务状态，版本更新失败可辨认，私人标记不会显示到考务端。

  定位到 [MainWindow.xaml](../../src/Exam.Admin/MainWindow.xaml) 第 364～367 行，以及 [MonitoringViewModel.cs](../../src/Exam.Admin/ViewModels/MonitoringViewModel.cs) 第 306～365 行。

- [ ] **10. 分开显示业务状态与连接状态。** 当前页面没有完整展示未登录、在线、离线、恢复中等状态。补齐未参加、等待、答题、已提交的业务状态，以及独立的连接状态和交卷处理中提示。验证明确断开立即标异常，135 秒无有效通信按规则标离线，离线时保留原业务状态，未持久保存不显示已回收。

  定位到 [MainWindow.xaml](../../src/Exam.Admin/MainWindow.xaml) 第 364 行，以及 [MonitoringViewModel.cs](../../src/Exam.Admin/ViewModels/MonitoringViewModel.cs) 中 `MonitoringRow` 和 `ToChinese`。

- [ ] **11. 按现场使用的信息筛选。** 当前过滤只匹配几个内部标识。补齐试场、学生端编号、姓名、班级、准考证号筛选，并与当前考试范围一致。验证组合筛选、清除条件和切换考试后结果正确。

  定位到 [MonitoringViewModel.cs](../../src/Exam.Admin/ViewModels/MonitoringViewModel.cs) 第 534 行。

- [ ] **12. 接通实时刷新和收齐通知。** 参试列表主要靠手动刷新，收齐历史按 20 秒补取且依赖监控页活动状态，不能满足正常联网不超过 2 秒的监测要求。保留历史补取用于恢复，接通实时状态和通知。模块验证覆盖事件到页面更新、跨页、断线重连、重复事件和名单版本变化；现场延迟与多机验证仍由 T 阶段执行。

  定位到 [MonitoringViewModel.cs](../../src/Exam.Admin/ViewModels/MonitoringViewModel.cs) 第 85、306 行。收齐弹窗已经存在于 [MainWindow.xaml.cs](../../src/Exam.Admin/MainWindow.xaml.cs) 第 59 行，需完善触发链路。

- [ ] **13. 补齐回收人数、未回收名单和补传入口。** 当前只有实际参试总数及已提交过滤，缺少成功回收人数、专门的未回收视图和请求补传入口。按服务器持久化结果统计，并与有效开始、结束指令重发衔接。验证未确认交卷不计入回收、补传后计数更新、零参试不弹收齐提示，同一名单版本不重复提示。

  定位到 [MonitoringViewModel.cs](../../src/Exam.Admin/ViewModels/MonitoringViewModel.cs) 第 361～365、534 行，以及 [MainWindow.xaml](../../src/Exam.Admin/MainWindow.xaml) 第 343～453 行。

- [ ] **14. 裁决前展示两台设备的对照信息。** 当前主要依赖填写目标设备标识，缺少原端和新端编号、试场、授权、连接、最后同步时间与答题进度的对照。应接入冲突提醒和可核对的设备选择，再调用现有裁决接口。验证裁决前原端授权不被抢占，裁决后两端状态一致，重复和过期裁决被正确处理。

  定位到 [MainWindow.xaml](../../src/Exam.Admin/MainWindow.xaml) 第 399～420 行，以及 [MonitoringViewModel.cs](../../src/Exam.Admin/ViewModels/MonitoringViewModel.cs) 中裁决命令。

## C05 导入、发布和考试中修改

依据 [requirements.md](../../requirements.md) 的”考试设置与计分””逐题导入、原版显示与加密””考试中修改时间与试题”，跟踪到 [#21](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/21)。A 已新增 `/exams/{examId}/publications/upload` 与 `/publications` 原子整体发布入口，真实校验加密学生包后，在同一事务保存资源、两个版本、事件和运行投影；第二阶段失败全部回滚。新 GUI 与 CLI 应使用整体入口，不能把两个旧端点的分别成功当作整体成功。

- [ ] **15. 完成正式导入和发布流程。** 主线缺少目录或 ZIP 导入、清单生成编辑、依赖检查、逐题编译、版式与选项绑定预览、确认发布的完整 GUI。PR #103 的只读预览可复用，但不能算完整导入。消费 D01～D03 已有模块，在 A 确认的原子发布接口上接 GUI、API、CLI。验证合成材料成功发布、缺项和编译失败、取消及重试、权限与版本冲突；失败不留下半份可用试卷，学生包不含答案和解析。

  入口定位到 [MainWindow.xaml](../../src/Exam.Admin/MainWindow.xaml) 的页面集合和 [ManifestPreflightViewModel.cs](../../src/Exam.Admin/ViewModels/ManifestPreflightViewModel.cs)。草稿范围以 [PR #103](https://github.com/yeyiwen2006/huaizhou-exam-system/pull/103) 为准。

- [ ] **16. 接通题目、答案规则修订及重算。** 当前缺少题干、图片、已有选项内容修订，以及参考答案、计分规则修订和重算的正式 GUI。已有时间保存能力应复用。通过 A06 版本发布和 D05 重算接线，记录操作者、原因、版本并显示处理结果。验证考试中不改题目结构和稳定标识，已有选择保留，失败保持可恢复旧版，重算期间结果不可混用，后续补收使用最新有效规则。

  需求和责任见 [#21](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/21)。公共发布边界见[给 A 的待办](ui-requirements-a-20261002.md)中的 C05 接口事项。

## C 各页面共同处理

- [ ] **17. 日常界面隐藏内部标识和开发说明。** 科目、题组、参试记录、设备、事件和版本等内部值仍直接出现在常规表格和输入框，重发指令还需要手填事件标识。改用考试名、科目和题组名称、学生端编号、考生信息及可理解的操作选择；必要诊断信息单独按中文说明展示。收齐弹窗也应显示考试名称。不得把只读清单预检或模块接入状态作为面向考务的最终产品页面。

  依据 [requirements.md](../../requirements.md) 第 9 行。定位到 [MainWindow.xaml](../../src/Exam.Admin/MainWindow.xaml) 第 227、243、364、384、399、423、480、518、528、562 行，以及 [MainWindow.xaml.cs](../../src/Exam.Admin/MainWindow.xaml.cs) 第 63 行。涉及公共导航或共享组件时先与 A 确认。

## 提交与验证

每项在原 Issue 中记录实现、依赖和实际检查，C01～C04 使用 #46，C05 使用 #21。按影响范围执行 GUI、接口、真实数据库或文件、并发和恢复回归，PR 交 A 审查。完成本卡所需接线与模块检查后再按[开发卡关闭规则](../development-closure-policy.md)判断关闭；不把未完成的软件功能留给 T01 或 T02。

原评审仅做源码与需求核对；本次更新明确记录 A 接口已交付和已执行的接口测试。各项 GUI 待办仍以实际实现与回归结果为准，接口通过不能替代界面验收。
