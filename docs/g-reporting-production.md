# G 报表生产接线与审计说明

整理来源：当前主分支源码、G01/G02/G03 任务卡和本次实际运行的模块检查。脱敏声明：文中、样例、测试和截图仅使用角色代号及合成数据，非真实考生资料。

本变更将已合并的计算、工作簿和表格组件接到真实考试流程：考试结束、全部实际参试答卷已持久化、所有答卷按当前规则计分完成后，考务身份可读取同一快照的五表及 Excel。开发实现已完成，公共契约、服务器入口、导航及一致性边界交 A 审查，尚未合并或关闭任务卡。

## 范围与来源

| 任务 | 原生产缺口 | 本次实现 |
| --- | --- | --- |
| G01，#31 | 权威输入、开放条件、整体结果发布、API/CLI | PostgreSQL 快照适配、开放状态、五表整体查询、版本复核、CLI status/show |
| G02，#32 | 受控下载、真实导出命令、失败安全保存 | 同快照 xlsx API、CLI export、身份/长度/哈希核验、临时文件与最终复核 |
| G03，#22 | 考务正式导航、真实加载和导出、失效处理 | 现有考务端 G03 页、缺卷提示、五表切换、固定均分行、取消及失效清空 |

PR #65、#75、#76、#77 的成果已在当前 main。PR #87 包含 #85 的消费端形状校验和 #86 的完整文件保存，并补强算术、状态与文件容器校验；按用户要求将三个候选统一到 #87 审查，旧候选保留分支历史。任务卡在 A 审查合并后关闭。

没有数据库迁移或新 NuGet 包。`Exam.Contracts/Reporting` 为新增 schema 1；业务入口扩展现有 `/api/v1` 和 E01 CLI，不建立第二个报表程序。C/F 原页面及功能保留。报表读取 D 的当前持久化成绩和已发布题目结构，G 不重算答案判定或题目分数。

## 权威读取与一致性

`PostgresReportSource` 读取 `version_state`、`actual_roster_versions`、实际 `participations`（排除 `not_participating`）、对应正式名册字段、最终交卷回执、冻结答卷和 `grading_results`，不用预报名单、在线人数或内存进度替代。当前已发布的规则和试卷结构提供动态科目、题号、满分及选项展示映射；原始答案键和计分规则不进入公开报表。

短事务复用 `PostgresActiveNodeWriteGuard`，拒绝备库、只读数据库、未初始化生产主备状态、过期节点或代次。对当前版本行执行不改变业务字段的 WAL 更新并锁定名单版本，再以单条查询取得输入；提交成功后才返回快照。此提交遵循部署已配置的同步持久化策略，不能替代 F 的配置核验和真实双机验收。短事务不包含排名计算、工作簿生成、下载或本地文件保存。

开放状态包括 `examNotEnded`、`noParticipants`、`collecting`、`grading`、`invalidSource`、`ready`。缺卷给出缺失的实际参试者；零实际参试者不开放。全部单科最终成绩为零时仍可列个人表，共同均分样本为空、均值及班级均分排名为 null，显示“无有效样本”。

快照标识是考试、规则/试卷状态、名单版本、回执/冻结答卷/成绩输入的 SHA-256 指纹。返回一次完整 `PublishedReport`，五类投影不会分批切换。查询计算后、Excel 生成后和客户端替换文件前都重新读取并比对当前权威快照；补收、名单变更或规则重算期间旧快照拒绝返回。重启从持久化输入重新构造，不依赖未持久化的发布缓存或后台任务。

计算与消费端同时核验完整字段范围、个人/班级排名、跨表行序、共同均分样本、精确均值、科目累计、小题满分颜色。原始科目成绩、负分归零标志与小题成绩一致。仍保留 D 的完全正确判定，答案正确性与小题满分分别着色。精确值参加排序、累计和均值；显示保留两位，正负半分界远离零，负零显示 `0.00`，此既有计算策略在本次覆盖负小题均值并交 A 复核。

## 公共协议

三个 GET 入口沿用现有考务身份验证、中间件错误封套及服务身份，不接受仅有学生设备身份的调用。所有成功响应设置 `Cache-Control: no-store`。

| 路径 | 输出 |
| --- | --- |
| `/api/v1/reports/{examId}/status` | `ApiEnvelope<ReportStatus>`，计数、版本及缺卷；未开放时 SnapshotId 为 null |
| `/api/v1/reports/{examId}?snapshotId=...` | `ApiEnvelope<PublishedReport>`；snapshotId 可省略，消费端提供可防止加载时换版 |
| `/api/v1/reports/{examId}/xlsx?snapshotId=...` | 指定快照的完整 OpenXML 文件；snapshotId 必填 |

schema 1 的 `Result` 是 `ReportCalculationResult` 五表投影，以 camelCase 编码；精确分数为有限小数字符串或 `n/d`，不用浮点数传输。均分字段含 `exact`、`sampleCount`、`display`。公开 DTO 用 `JsonElement` 承载稳定 schema，不让 Contracts 反向依赖 Domain；客户端必须经过 `ReportJson.Read(PublishedReport)` 完整校验。状态须满足已计分≤已回收≤实际参试，缺卷唯一且数量等于差额；ready 须人数为正、三个计数相等、版本有效且有快照，结果人数也须相等。字段 schema 见 `samples/g-reporting/published-report.schema.json`，可加载合成样例见 `samples/g-reporting/published-report.synthetic.json`。schema 描述形状及约束说明，算术、开放条件和身份由代码校验。

未开放或输入无效返回现有 `REPORT_NOT_READY`，过期指定快照返回 `VERSION_CONFLICT`；身份失败沿用 401/403。成功文件响应含 `X-Exam-Server-Id`、`X-Exam-Node-Id`、`X-Exam-Server-Epoch`、`X-Exam-Report-Snapshot` 和 `X-Exam-Workbook-Sha256`。同代次更换节点、服务身份变化或代次倒退均拒绝。文件长度上限 128 MiB，服务器最多同时生成两个工作簿。

## 使用和恢复

启动已配置 PostgreSQL 的现有服务器及考务端。连接验证通过后打开“G03 成绩报表”，输入考试标识，刷新。未收齐显示缺卷清单，重算显示当前版本计分进度。表格下方均分固定，横向查看动态列；长列标题换行并提供悬浮文本。

CLI 沿用现有凭据环境变量，不把令牌写入命令行或文件：

```powershell
dotnet run --project src/Exam.Cli --configuration Release -- reports status synthetic-exam --server https://localhost:5001
dotnet run --project src/Exam.Cli --configuration Release -- reports show synthetic-exam --server https://localhost:5001
dotnet run --project src/Exam.Cli --configuration Release -- reports export synthetic-exam ./synthetic.xlsx --server https://localhost:5001
```

以上标识与目标为合成示例。默认仅 HTTPS；显式开发配置才允许数字回环 HTTP，不跟随重定向。CLI 返回 0 成功、2 参数错误、3 身份拒绝、4 未开放/版本拒绝、5 网络不可达、6 服务端或文件错误、130 用户取消。Ctrl+C 取消同一请求及保存流程。

考务端每秒在活动页面检查状态；考试、连接、身份、页面或版本变化清空旧表并取消旧任务，包括尚未完成的首次加载和自动加载。状态查询超过五秒即隐藏旧表，即使传输忽略取消也不回填迟到响应。轮询有一次检查周期的显示延迟，服务端每次查询/导出独立校验。手动取消后停止自动加载，须手动刷新；取消文件选择保留当前表格，保存对话框期间的上下文变化禁止开始下载。传输失败或版本失效使导出失败并隐藏旧结果。

客户端先验证文件类型、长度、身份、快照、SHA-256 及 ZIP 中唯一非空的类型声明、根关系和工作簿部件，拒绝截断容器、普通 ZIP、重复/空部件及歧义响应头。该检查不代替完整 OpenXML 语义验证。通过后写目标目录唯一临时文件，刷新到磁盘，最终核验状态后替换目标；考务端和 CLI 导出均有覆盖响应体与保存的五分钟总期限。取消、断网、损坏响应、文件占用或快照失效保留原目标并清理临时文件。已成功提交的文件不能追溯撤回；进程强制终止可能留下唯一 `.partial` 文件，重新导出不使用它，可人工清理。第三方 XLSX 生成调用不支持内部取消，边界观察取消，后台生成期间界面仍可操作。

回退为撤回本 PR 代码并重建上一版本，无迁移需回滚。原始交卷和计分存储不改变。公共契约、WAL 提交边界、轮询和下载最终复核由 A 审计，不能以本次单机测试宣称生产主备、断电、300 真实终端或 T01/T02/T03 已验收。

## 实跑验证

环境：Windows、.NET SDK 10.0.401、Release，独立回环 PostgreSQL 18.6。只使用合成数据；本机数据库、运行时、日志、输出和 TRX 不入仓库。

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release
dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release --filter "Category!=Latex"
```

已配置隔离 `EXAM_TEST_POSTGRES` 并实际执行数据库测试。锁定还原通过，Release 0 警告/0 错误。新增状态/迟到响应/取消/容器回归后，报表单元 91 项、WPF 报表 27 项通过。全量非 Latex 首轮 2134 通过、1 失败、3 跳过；失败为已有 C 组轮询测试读取瞬时提示，完整考务测试项目复测 180 项全部通过，合并各项目最近结果为 2135 通过、0 失败、3 跳过。保留首轮记录，不修改该无关测试。3 个跳过项为已有物理恢复测试，专用环境未配置；XeLaTeX 未安装，Latex 类别未执行，报表功能不调用 TeX。CI 状态以 PR 最新记录为准。

覆盖：300 合成参与者排名与 Excel 回读；错置列、矛盾均值及显示、原始成绩、颜色、精度；真实数据库结束/缺卷/补交/规则变更与重算；服务重建一致性；实际授权 API、Kestrel/CLI、工作簿回读、服务停止后文件保留；8 并发导出与交卷；下载哈希/类型/身份/长度异常、取消、文件占用；正式 WPF 页加载/失效/授权失败/恢复/切考/迟到结果，以及 100%、125%、150% 等效布局。截图 `g-reporting-images/report-page-100.png` 等由合成数据离屏渲染并人工查看，未替代真实显示器 DPI 或真实 Excel 人工验收。

CI 沿用现有 Windows 非 PostgreSQL/WPF 作业与 Linux PostgreSQL 作业。集成测试现在显式构建实际 CLI，即使只构建该测试项目也具备子进程产物。
