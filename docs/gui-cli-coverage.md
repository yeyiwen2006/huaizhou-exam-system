# GUI/CLI 入口与模块覆盖

来源：`requirements.md`、当前源码及 `e01-coverage.json`。2026-10-06 按最新主分支与 #131 修正核对。仅使用角色代号和合成测试依据，不含真实身份或凭据。

表中“已接线”说明产品入口存在，不等于现场验收通过。运行结果、失败修正和未执行范围见 [#131 审查记录](reviews/pr131-review-20261006.md)；F05 归档接线见 [#130 审查记录](reviews/pr130-review-20261006.md)。通用 `api` 命令不单独证明业务闭环；学生端按需求只提供 GUI。

| 场景 | 业务动作 | GUI | CLI | 状态与缺口归属 |
| --- | --- | --- | --- | --- |
| E01-C01 | 注册 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | register | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C02 | 登录 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | login | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C03 | 退出 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | logout | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C04 | 身份查询 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | whoami | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C05 | 账号列表 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | operators list | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C06 | 批准 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | operators approve | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C07 | 停用 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | operators disable | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C08 | 启用 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | operators enable | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C09 | 状态 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management status | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务；F |
| E01-C10 | 配置读取 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management configuration | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务；F |
| E01-C11 | 启动 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management start | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务；F |
| E01-C12 | 停止 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management stop | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务；F |
| E01-C13 | 自检 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management checks | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务；F |
| E01-C14 | 配置写入 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management configure | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务；F |
| E01-C15 | 管理日志 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management logs | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务；F |
| E01-C16 | 运行日志 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management runtime-logs | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务；F |
| E01-C17 | 备份 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management backup | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务；F |
| E01-C18 | 隔离恢复 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management restore | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务；F |
| E01-C19 | 操作进度 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management operation | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务；F |
| E01-C20 | 同步状态 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management ha-status | GUI与CLI已接主备协调；读取实际状态并保留持久请求和安全确认；F |
| E01-C21 | 应急确认 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management emergency | GUI与CLI已接主备协调；读取实际状态并保留持久请求和安全确认；F |
| E01-C22 | 接管 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management takeover | GUI与CLI已接主备协调；读取实际状态并保留持久请求和安全确认；F |
| E01-C23 | 主节点重建 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management rebuild / management reprotect | GUI与CLI已接主备协调；读取实际状态并保留持久请求和安全确认；F |
| E01-C24 | 激活批准 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | activation pending / activation approve 申请标识 [试场名称] | 首次审批按待审批申请标识执行，批准后才分配编号；真实CLI/API/数据库接线已补齐；C |
| E01-C25 | 激活撤销 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | activation revoke 学生端编号 --input FILE | 按公开编号撤销；原请求重试保持同一设备，服务器重新鉴权；C |
| E01-C26 | 设备编号查询 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | devices list / devices show / devices history | 权威设备目录、编号精确查询与转组历史已接GUI/CLI；C |
| E01-C27 | 创建分组 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | rooms create | 试场创建已由服务器持久保存，GUI/CLI共用校验；C |
| E01-C28 | 分组改名 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | rooms rename | 试场改名已持久化；同名冲突与期望版本由服务器核对；C |
| E01-C29 | 设备转组 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | rooms move / devices history | 单个或批量转组已持久化；整批原子校验并记录历史，不改变编号；C |
| E01-C30 | 删除空组 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | rooms delete | 仅允许删除权威目录中的空组；版本及并发冲突拒绝；C |
| E01-C31 | 考试列表 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams list | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C32 | 考试详情 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams show | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C33 | 新建考试 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams create | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C34 | 修改设置 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams update | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C35 | 学生可见性 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams visibility | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C36 | 名单导入、重复及整批冲突 | src/Exam.Admin/ViewModels/RosterViewModel.cs | roster list / import / update / delete | 名册已由服务器持久化；保留前导零、逐行问题、重复跳过、整批冲突拒绝及版本检查；C |
| E01-C37 | 目录/ZIP 导入 | src/Exam.Admin/ViewModels/ExamImportViewModel.cs | import preview 目录或ZIP --exam 名称 --output 目录 | GUI目录/ZIP选择与CLI已接同一准备流程；预览不等于发布；D |
| E01-C38 | 依赖检查 | src/Exam.Admin/ViewModels/ExamImportViewModel.cs | import verify / import preview | GUI/CLI已接清单、路径和编译依赖检查；失败不发布；D |
| E01-C39 | 编译 | src/Exam.Admin/ViewModels/ExamImportViewModel.cs | import verify / import preview | GUI/CLI已接逐题编译和坐标提取；真实版式仍需预览确认；D |
| E01-C40 | 预览 | src/Exam.Admin/ViewModels/ExamImportViewModel.cs | import preview | GUI逐题预览与CLI预览目录已接线；保留同一预览供确认；D |
| E01-C41 | 发布 | src/Exam.Admin/ViewModels/ExamImportViewModel.cs | import publish 预览目录 --confirm | GUI/CLI确认后加密上传并原子发布，校验版本与重试身份；D |
| E01-C42 | 在线试题修改 | src/Exam.Admin/ViewModels/ExamImportViewModel.cs | import materials / import preview --reason / import publish | 已接取回源材料、开考后逐题修订与同版原子发布；不覆盖旧版档案；D |
| E01-C43 | 进度监测 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | monitor show / missing / stream | GUI与CLI读取权威快照；stream持续输出页面、进度、答案和回收变化；C |
| E01-C44 | 设备冲突裁决 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | decisions keep 考试名称 学生端编号 --input FILE | 按公开编号裁决；操作者来自当前登录；不确定回执重试保留原版本及设备；C |
| E01-C45 | 指令重发 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | commands resend / commands reupload | 重发有效开始/结束指令及请求原答卷补传；重试固定原事件，已收卷不新建补传；C |
| E01-C46 | 事件订阅 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | monitor events 名称 [起始名单版本] [间隔秒] [轮数] | 冲突及失败通知已接线；连续读取支持重试、去重、迟到快照拒绝与取消，不接设备专用Hub；C |
| E01-C47 | 回收通知 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | monitor watch / follow / events | 持久收齐事件已接GUI/CLI；零参试不触发，按名单版本去重；watch/follow只处理收齐；C |
| E01-C48 | 当前计分规则/完整答卷范围及就绪 | src/Exam.Admin/ViewModels/GradingRevisionViewModel.cs | grading status | GUI/CLI已显示发布规则、未计分范围及结果就绪，发布成功不等于重算完成；D |
| E01-C49 | 规则修改与重新计分 | src/Exam.Admin/ViewModels/GradingRevisionViewModel.cs | grading revise / grading publish / grading status | 独立答案与规则预览、确认发布及后台重算已接线；不重新编译试卷；D |
| E01-C50 | 结果开放状态 | src/Exam.Admin/Reporting/ReportWorkspaceView.xaml | reports status | 入口已实现；模块验证由对应 profile 报告核对；G |
| E01-C51 | 查询五表 | src/Exam.Admin/Reporting/ReportWorkspaceView.xaml | reports show | 入口已实现；模块验证由对应 profile 报告核对；G |
| E01-C52 | Excel 导出 | src/Exam.Admin/Reporting/ReportWorkspaceView.xaml | reports export | 入口已实现；模块验证由对应 profile 报告核对；G |
| E01-C53 | 激活与登录 | src/Exam.Student/Views/ActivationPage.xaml | 不适用：学生端仅要求 GUI | 入口已实现；模块验证由对应 profile 报告核对；B |
| E01-C54 | 考试选择与等待 | src/Exam.Student/Views/WaitingExamPage.xaml | 不适用：学生端仅要求 GUI | 入口已实现；模块验证由对应 profile 报告核对；B |
| E01-C55 | 答题、时间与全屏 | src/Exam.Student/Views/ExamInProgressPage.xaml | 不适用：学生端仅要求 GUI | 入口已实现；模块验证由对应 profile 报告核对；B |
| E01-C56 | 交卷与恢复 | src/Exam.Student/Views/SubmittedPage.xaml | 不适用：学生端仅要求 GUI | 入口已实现；模块验证由对应 profile 报告核对；B |

## 监测与重试边界

`monitor show/missing` 查询一次；`monitor stream 名称 [间隔秒] [轮数]` 默认每秒输出完整权威快照，持续到取消。`monitor events` 不给间隔时只读一次，给间隔后持续发出收齐、冲突和失败通知。`monitor watch/follow` 仍只处理持久收齐事件，不能用 C47 的完成代替 C46。

首次激活在批准前没有学生端编号：先用 `activation pending` 取得申请标识，再批准；撤销、裁决、重发和补传按公开编号选择。新业务写命令把原请求加密保存在当前 Windows 账号的 CLI 工作目录；回执不确定时用相同 `--request-id` 和原输入重试，不重新解析成最新设备、版本或事件。服务器每次仍校验当前登录与业务权限。

冲突/失败按快照判定，不能补出从未观察到的短暂异常；下载失败、修订下载失败、本地答案保存失败及指令资源更新失败均有提醒。失败不会伪装成正常空结果；持续读取只把成功轮次计入结束条件。迟到或版本回退的快照整页拒绝；不同逻辑服务器或回退的服务代次终止当前操作。跨进程收齐去重需调用方保存已处理名单版本，冲突/失败去重在进程内维护。

## 证据范围

机器表逐行保留需求、共享入口、成功与失败测试文件、类别、证据引用和现场边界。测试文件存在不表示其本轮执行通过；以对应审查记录、日志和 TRX 为准。名册、试场、完整试卷流程、服务管理和计分修订的既有实现不再列为“服务器持久化未实现”或“专用命令未接线”。

当前 GitHub Actions 的计费限制与本地测试结果分别记录，不能把未启动的 CI 写成通过。真实双机、断电、全部设备/DPI、现场时延、完整300会话和最终发布验收仍由 T01/T02/T03 执行。
