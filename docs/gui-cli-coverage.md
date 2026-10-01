# GUI/CLI 入口与模块覆盖

来源：`requirements.md`、当前源码及 `e01-coverage.json`。脱敏声明：仅角色代号；没有真实身份或凭据。

状态区分入口实现、模块证据和整体未执行。自动证据以最新 run-report 为准，不从静态表推断测试通过。通用 api 命令只表示传输能力。学生端不生成专用 CLI 任务。

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
| E01-C09 | 状态 | 未接线/需消费方核对 | management status | 入口已实现；模块验证由对应 profile 报告核对；F |
| E01-C10 | 配置读取 | 未接线/需消费方核对 | management configuration | 入口已实现；模块验证由对应 profile 报告核对；F |
| E01-C11 | 启动 | 未接线/需消费方核对 | management start | 入口已实现；模块验证由对应 profile 报告核对；F |
| E01-C12 | 停止 | 未接线/需消费方核对 | management stop | 入口已实现；模块验证由对应 profile 报告核对；F |
| E01-C13 | 自检 | 未接线/需消费方核对 | management checks | 入口已实现；模块验证由对应 profile 报告核对；F |
| E01-C14 | 配置写入 | 未接线/需消费方核对 | management configure | 入口已实现；模块验证由对应 profile 报告核对；F |
| E01-C15 | 管理日志 | 未接线/需消费方核对 | management logs | 入口已实现；模块验证由对应 profile 报告核对；F |
| E01-C16 | 运行日志 | 未接线/需消费方核对 | management runtime-logs | 入口已实现；模块验证由对应 profile 报告核对；F |
| E01-C17 | 备份 | 未接线/需消费方核对 | management backup | 入口已实现；模块验证由对应 profile 报告核对；F |
| E01-C18 | 隔离恢复 | 未接线/需消费方核对 | management restore | 入口已实现；模块验证由对应 profile 报告核对；F |
| E01-C19 | 操作进度 | 未接线/需消费方核对 | management operation | 入口已实现；模块验证由对应 profile 报告核对；F |
| E01-C20 | 同步状态 | 未接线/需消费方核对 | 专用命令未接线 | 底层与恢复模块存在；专用 GUI/CLI 需 F/C 核对；F |
| E01-C21 | 应急确认 | 未接线/需消费方核对 | 专用命令未接线 | 底层与恢复模块存在；专用 GUI/CLI 需 F/C 核对；F |
| E01-C22 | 接管 | 未接线/需消费方核对 | 专用命令未接线 | 底层与恢复模块存在；专用 GUI/CLI 需 F/C 核对；F |
| E01-C23 | 主节点重建 | 未接线/需消费方核对 | 专用命令未接线 | 底层与恢复模块存在；专用 GUI/CLI 需 F/C 核对；F |
| E01-C24 | 激活批准 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | 专用命令未接线 | 入口已实现；模块验证由对应 profile 报告核对；C |
| E01-C25 | 激活撤销 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | 专用命令未接线 | 入口已实现；模块验证由对应 profile 报告核对；C |
| E01-C26 | 设备编号查询 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | 专用命令未接线 | 入口已实现；模块验证由对应 profile 报告核对；C |
| E01-C27 | 创建分组 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | 专用命令未接线 | GUI 本机状态入口；服务器持久化与专用 CLI 未实现；C |
| E01-C28 | 分组改名 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | 专用命令未接线 | GUI 本机状态入口；服务器持久化与专用 CLI 未实现；C |
| E01-C29 | 设备转组 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | 专用命令未接线 | GUI 本机状态入口；服务器持久化与专用 CLI 未实现；C |
| E01-C30 | 删除空组 | src/Exam.Admin/ViewModels/ActivationRoomViewModel.cs | 专用命令未接线 | GUI 本机状态入口；服务器持久化与专用 CLI 未实现；C |
| E01-C31 | 考试列表 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams list | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C32 | 考试详情 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams show | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C33 | 新建考试 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams create | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C34 | 修改设置 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams update | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C35 | 学生可见性 | src/Exam.Admin/ViewModels/ExamHomeViewModel.cs | exams visibility | 入口已实现；模块验证由对应 profile 报告核对；A |
| E01-C36 | 名单导入、重复及整批冲突 | src/Exam.Admin/ViewModels/RosterViewModel.cs | 专用命令未接线 | GUI 预览/本机暂存已实现；服务持久化与专用 CLI 未实现；C |
| E01-C37 | 目录/ZIP 导入 | src/Exam.Admin/ViewModels/ManifestPreflightViewModel.cs | import verify DIRECTORY (local) | 本地目录核对 CLI 已接线；完整 GUI、ZIP 导入及服务持久化待消费方接线；D |
| E01-C38 | 依赖检查 | src/Exam.Admin/ViewModels/ManifestPreflightViewModel.cs | import verify DIRECTORY (local) | 本地目录核对 CLI 已接线；完整 GUI、ZIP 导入及服务持久化待消费方接线；D |
| E01-C39 | 编译 | 未接线/需消费方核对 | import verify DIRECTORY (local) | 本地目录核对 CLI 已接线；完整 GUI、ZIP 导入及服务持久化待消费方接线；D |
| E01-C40 | 预览 | 未接线/需消费方核对 | 专用命令未接线 | 底层模块检查存在；专用 CLI 与完整 GUI 工作流未全部接线；D |
| E01-C41 | 发布 | 未接线/需消费方核对 | 专用命令未接线 | 底层模块检查存在；专用 CLI 与完整 GUI 工作流未全部接线；D |
| E01-C42 | 在线试题修改 | 未接线/需消费方核对 | 专用命令未接线 | 底层模块检查存在；专用 CLI 与完整 GUI 工作流未全部接线；D |
| E01-C43 | 进度监测 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | 专用命令未接线 | 入口已实现；模块验证由对应 profile 报告核对；C |
| E01-C44 | 设备冲突裁决 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | 专用命令未接线 | 入口已实现；模块验证由对应 profile 报告核对；C |
| E01-C45 | 指令重发 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | 专用命令未接线 | 入口已实现；模块验证由对应 profile 报告核对；C |
| E01-C46 | 事件订阅 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | 专用命令未接线 | 入口已实现；模块验证由对应 profile 报告核对；C |
| E01-C47 | 回收通知 | src/Exam.Admin/ViewModels/MonitoringViewModel.cs | 专用命令未接线 | 入口已实现；模块验证由对应 profile 报告核对；C |
| E01-C48 | 当前计分规则/完整答卷范围及就绪 | 未接线/需消费方核对 | grading status | 入口已实现；模块验证由对应 profile 报告核对；D |
| E01-C49 | 规则修改与重新计分 | 未接线/需消费方核对 | 专用命令未接线 | 规则/重算模块存在；专用业务 CLI 与 GUI 需 D/C 接线；D |
| E01-C50 | 结果开放状态 | src/Exam.Admin/Reporting/ReportWorkspaceView.xaml | reports status | 入口已实现；模块验证由对应 profile 报告核对；G |
| E01-C51 | 查询五表 | src/Exam.Admin/Reporting/ReportWorkspaceView.xaml | reports show | 入口已实现；模块验证由对应 profile 报告核对；G |
| E01-C52 | Excel 导出 | src/Exam.Admin/Reporting/ReportWorkspaceView.xaml | reports export | 入口已实现；模块验证由对应 profile 报告核对；G |
| E01-C53 | 激活与登录 | src/Exam.Student/Views/ActivationPage.xaml | 不适用：学生端仅要求 GUI | 入口已实现；模块验证由对应 profile 报告核对；B |
| E01-C54 | 考试选择与等待 | src/Exam.Student/Views/WaitingExamPage.xaml | 不适用：学生端仅要求 GUI | 入口已实现；模块验证由对应 profile 报告核对；B |
| E01-C55 | 答题、时间与全屏 | src/Exam.Student/Views/ExamInProgressPage.xaml | 不适用：学生端仅要求 GUI | 入口已实现；模块验证由对应 profile 报告核对；B |
| E01-C56 | 交卷与恢复 | src/Exam.Student/Views/SubmittedPage.xaml | 不适用：学生端仅要求 GUI | 入口已实现；模块验证由对应 profile 报告核对；B |

机器表逐行保留需求章节、共享入口、成功与失败检查依据、类别、自动证据、人工检查及缺口说明。表中的测试目录表示该模块检查集合，不证明某个未接线产品入口已实跑。

本卡未修改产品 GUI；普通和 WPF 检查仍为必需。C 的分组、名册持久化和专用 CLI，D/C 的完整试卷工作流，F/C 的运维 GUI 及部分专用命令由对应卡继续；不把这些消费方缺口藏在 E01 通过标记里。
