# GUI 与 CLI 功能覆盖

本表记录正式功能入口，不表示所有现场场景已通过。测试清单、夹具和结果仅保留在本地测试目录。实际通过范围以对应提交的分类报告为准；通用 HTTP 传输命令不代替专用业务命令。

| 编号 | 功能 | GUI 实现 | CLI 入口 | 当前行为 |
| --- | --- | --- | --- | --- |
| COV-001 | 注册 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | register | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-002 | 登录 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | login | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-003 | 退出 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | logout | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-004 | 身份查询 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | whoami | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-005 | 账号列表 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | operators list | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-006 | 批准 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | operators approve | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-007 | 停用 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | operators disable | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-008 | 启用 | src/Exam.Admin/ViewModels/OperatorAccountsViewModel.cs | operators enable | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-009 | 状态 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management status | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务 |
| COV-010 | 配置读取 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management configuration | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务 |
| COV-011 | 启动 | GUI不提供入口 | management start | 正式管理API与CLI保留启停，权限、固定目标及原请求核对继续生效 |
| COV-012 | 停止 | GUI不提供入口 | management stop | 正式管理API与CLI保留启停，权限、固定目标及原请求核对继续生效 |
| COV-013 | 状态检测 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management checks | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务 |
| COV-014 | 配置写入 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management configure | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务 |
| COV-015 | 管理日志 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management logs | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务 |
| COV-016 | 运行日志 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management runtime-logs | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务 |
| COV-017 | 备份 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management backup | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务 |
| COV-018 | 隔离恢复 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management restore | GUI与CLI已接独立管理宿主；权限、固定目标及原请求核对沿用同一服务 |
| COV-019 | 查询请求处理状态 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management operation | GUI 保留后台原请求核对，不显示请求编号输入、手工查询按钮和顶部处理状态；人工查询由管理 API 或 CLI 提供 |
| COV-020 | 同步状态 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management ha-status | GUI与CLI已接主备协调；读取实际状态并保留持久请求和安全确认 |
| COV-021 | 自动单节点持久确认 | 无需操作；管理页显示实际保护状态 | management ha-status | 备机缺席时自动本机持久保存，取消应急按钮；旧 emergency 路由拒绝新应急请求 |
| COV-022 | 接管 | src/Exam.Admin/ServiceManagement/ServiceManagementView.xaml | management takeover | 登录页可独立进入管理；醒目接管按钮要求真实隔离旧主，保持原请求，完成提升后自动启动业务 |
| COV-023 | 主节点重建 | src/Exam.Admin/ServiceManagement/ServiceManagementViewModel.cs | management rebuild / management reprotect | GUI与CLI已接主备协调；读取实际状态并保留持久请求和安全确认 |
| COV-024 | 连接与每日编号 | src/Exam.Student/Views/LoginPage.xaml | 不适用，学生端仅提供 GUI | 无需批准直接登录，同一设备当天沿用编号，次日重分 |
| COV-025 | 学生端连接状态 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | devices list / devices show | 查看学生端信息、当前考生和连接状态，无学生设备批准组件 |
| COV-026 | 设备编号查询 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | devices list / devices show / devices history | 权威设备目录、编号精确查询与转组历史已接GUI/CLI |
| COV-027 | 创建分组 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | rooms create | 试场创建已由服务器持久保存，GUI/CLI共用校验 |
| COV-028 | 分组改名 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | rooms rename | 试场改名已持久化；同名冲突与期望版本由服务器核对 |
| COV-029 | 设备转组 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | rooms move / devices history | 单个或批量转组已持久化；整批原子校验并记录历史，不改变编号 |
| COV-030 | 删除空组 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | rooms delete | 仅允许删除权威目录中的空组；版本及并发冲突拒绝 |
| COV-031 | 考试列表 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams list | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-032 | 考试详情 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams show | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-033 | 新建考试 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams create | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-034 | 修改设置 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams update | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-035 | 学生可见性 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams visibility | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-036 | 名单导入、重复及整批冲突 | src/Exam.Admin/ViewModels/RosterViewModel.cs | roster list / import / update / delete | 名册已由服务器持久化；保留前导零、逐行问题、重复跳过、整批冲突拒绝及版本检查 |
| COV-037 | 目录/ZIP 导入 | src/Exam.Admin/ViewModels/ExamImportViewModel.cs | import preview 目录或ZIP --exam 名称 --output 目录 | GUI目录/ZIP选择与CLI已接同一准备流程；预览不等于发布 |
| COV-038 | 依赖检查 | src/Exam.Admin/ViewModels/ExamImportViewModel.cs | import verify / import preview | GUI/CLI已接清单、路径和编译依赖检查；失败不发布 |
| COV-039 | 编译 | src/Exam.Admin/ViewModels/ExamImportViewModel.cs | import verify / import preview | GUI/CLI已接逐题编译和坐标提取；真实版式仍需预览确认 |
| COV-040 | 预览 | src/Exam.Admin/ViewModels/ExamImportViewModel.cs | import preview | GUI逐题预览与CLI预览目录已接线；保留同一预览供确认 |
| COV-041 | 发布 | src/Exam.Admin/ViewModels/ExamImportViewModel.cs | import publish 预览目录 --confirm | GUI/CLI确认后加密上传并原子发布，校验版本与重试身份 |
| COV-042 | 在线试题修改 | src/Exam.Admin/ViewModels/ExamImportViewModel.cs | import materials / import preview --reason / import publish | 已接取回源材料、开考后逐题修订与同版原子发布；不覆盖旧版档案 |
| COV-043 | 进度监测 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | monitor show / missing / stream | GUI与CLI读取权威快照；stream持续输出页面、进度、答案和回收变化 |
| COV-044 | 设备冲突裁决 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | decisions keep 考试名称 学生端编号 --input FILE | 按公开编号裁决；操作者来自当前登录；不确定回执重试保留原版本及设备 |
| COV-045 | 指令重发 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | commands resend / commands reupload | 重发有效开始/结束指令及请求原答卷补传；重试固定原事件，已收卷不新建补传 |
| COV-046 | 事件订阅 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | monitor events 名称 [起始名单版本] [间隔秒] [轮数] | 冲突及失败通知已接线；连续读取支持重试、去重、迟到快照拒绝与取消，不接设备专用Hub |
| COV-047 | 回收通知 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | monitor watch / follow / events | 持久收齐事件已接GUI/CLI；零参试不触发，按名单版本去重；watch/follow只处理收齐 |
| COV-048 | 当前计分规则/完整答卷范围及就绪 | src/Exam.Admin/ViewModels/GradingRevisionViewModel.cs | grading status | GUI/CLI已显示发布规则、未计分范围及结果就绪，发布成功不等于重算完成 |
| COV-049 | 规则修改与重新计分 | src/Exam.Admin/ViewModels/GradingRevisionViewModel.cs | grading revise / grading publish / grading status | 独立答案与规则预览、确认发布及后台重算已接线；不重新编译试卷 |
| COV-050 | 结果开放状态 | src/Exam.Admin/Reporting/ReportWorkspaceView.xaml | reports status | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-051 | 查询五表 | src/Exam.Admin/Reporting/ReportWorkspaceView.xaml | reports show | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-052 | Excel 导出 | src/Exam.Admin/Reporting/ReportWorkspaceView.xaml | reports export | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-053 | 连接与登录 | src/Exam.Student/Views/LoginPage.xaml | 不适用：学生端仅要求 GUI | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-054 | 考试选择与等待 | src/Exam.Student/Views/WaitingExamPage.xaml | 不适用：学生端仅要求 GUI | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-055 | 答题、时间与全屏 | src/Exam.Student/Views/ExamInProgressPage.xaml | 不适用：学生端仅要求 GUI | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-056 | 交卷与恢复 | src/Exam.Student/Views/SubmittedPage.xaml | 不适用：学生端仅要求 GUI | 入口已实现；模块验证由对应 profile 报告核对 |
| COV-057 | 修改考试时间与审计 | src/Exam.Admin/ViewModels/ExamConfigurationCatalog.cs | exams schedule 名称 --input 文件 | 按原预期版本保存；结束后延长可重开并可靠推送学生端，已固定答卷保留，历史重放不重开 |
| COV-058 | 隐藏考试及按范围取消隐藏 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams hidden / exams hide / exams unhide（self 或 all） | 个人与全体隐藏分别持久化；按认证账号读取，保留学生端显示和所有考试数据 |
