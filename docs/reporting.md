# 成绩报表与导出

考试结束、全部实际参试答卷已持久保存、所有答卷按当前规则计分完成后，考务人员可读取同一快照的五类成绩表并导出 Excel。统计只读取权威答卷和成绩，不修改原始答案。

## 权威读取与一致性

`PostgresReportSource` 读取 `version_state`、`actual_roster_versions`、实际 `participations`（排除 `not_participating`）、对应正式名册字段、最终交卷回执、冻结答卷和 `grading_results`，不用预报名单、在线人数或内存进度替代。当前已发布的规则和试卷结构提供动态科目、题号、满分及选项展示映射；原始答案键和计分规则不进入公开报表。

短事务复用 `PostgresActiveNodeWriteGuard`，拒绝备库、只读数据库、未初始化生产主备状态、过期节点或代次。对当前版本行执行不改变业务字段的 WAL 更新并锁定名单版本，再以单条查询取得输入；提交成功后才返回快照。此提交遵循部署已配置的同步持久化策略，不能替代 配置核验和真实双机验收。短事务不包含排名计算、工作簿生成、下载或本地文件保存。

开放状态包括 `examNotEnded`、`noParticipants`、`collecting`、`grading`、`invalidSource`、`ready`。缺卷给出缺失的实际参试者；零实际参试者不开放。单科及该科小题均分只排除该科最终成绩为零者；总均分排除任一科最终成绩为零者。班级内分别筛选。某项样本为空时该项均值及班级排名为 null，显示“无有效样本”；个人表仍保留零分考生。总分样本为空时，单科均分仍可能存在。

为兼容已有封套，`CommonSampleCount`、`ClassAverageRow.SampleCount` 保留名称，仅表示总分共同样本人数；各单科及小题均值使用各自 `ReportMean.SampleCount`。消费端按科目独立重算样本人数和精确均值，不能假定所有表人数相同。

快照标识是考试、规则/试卷状态、名单版本、回执/冻结答卷/成绩输入的 SHA-256 指纹。返回一次完整 `PublishedReport`，五类投影不会分批切换。查询计算后、Excel 生成后和客户端替换文件前都重新读取并比对当前权威快照。JSON 与 XLSX 入口还在 持久确认完成后、开始发送响应前再次复核；不能把确认网络等待之前的旧检查结果当作发送许可。下载身份、快照及 SHA-256 响应头仅在最终复核通过后设置，失败响应不残留成功文件的校验头；补收、名单变更或规则重算期间旧快照拒绝返回。重启从持久化输入重新构造，不依赖未持久化的发布缓存或后台任务。

计算与消费端同时核验完整字段范围、个人/班级排名、跨表行序、单科样本与总分共同样本、精确均值、科目累计、小题满分颜色。原始科目成绩、负分归零标志与小题成绩一致。仍保留完全正确判定，答案正确性与小题满分分别着色。精确值参加排序、累计和均值；显示保留两位，正负半分界远离零，负零显示 `0.00`，此策略同样适用于负小题均值。

## 公共协议

三个 GET 入口沿用现有考务身份验证、中间件错误封套及服务身份，不接受仅有学生设备身份的调用。所有成功响应设置 `Cache-Control: no-store`。

| 路径 | 输出 |
| --- | --- |
| `/api/v1/reports/{examId}/status` | `ApiEnvelope<ReportStatus>`，计数、版本及缺卷；未开放时 SnapshotId 为 null |
| `/api/v1/reports/{examId}?snapshotId=...` | `ApiEnvelope<PublishedReport>`；snapshotId 可省略，消费端提供可防止加载时换版 |
| `/api/v1/reports/{examId}/xlsx?snapshotId=...` | 指定快照的完整 OpenXML 文件；snapshotId 必填 |

schema 1 的 `Result` 是 `ReportCalculationResult` 五表投影，以 camelCase 编码；精确分数为有限小数字符串或 `n/d`，不用浮点数传输。均分字段含 `exact`、`sampleCount`、`display`。公开 DTO 用 `JsonElement` 承载稳定 schema，不让 Contracts 反向依赖 Domain；客户端必须经过 `ReportJson.Read(PublishedReport)` 完整校验。状态须满足已计分≤已回收≤实际参试，缺卷唯一且数量等于差额；ready 须人数为正、三个计数相等、版本有效且有快照，结果人数也须相等。字段 schema 见 `samples/reporting/published-report.schema.json`，可加载合成样例见 `samples/reporting/published-report.synthetic.json`。schema 描述形状及约束说明，算术、开放条件和身份由代码校验。

未开放或输入无效返回现有 `REPORT_NOT_READY`，过期指定快照返回 `VERSION_CONFLICT`；身份失败沿用 401/403。成功文件响应含 `X-Exam-Server-Id`、`X-Exam-Node-Id`、`X-Exam-Server-Epoch`、`X-Exam-Report-Snapshot` 和 `X-Exam-Workbook-Sha256`。同代次更换节点、服务身份变化或代次倒退均拒绝。文件长度上限 128 MiB，服务器最多同时生成两个工作簿。

## 使用和恢复

启动已配置 PostgreSQL 的现有服务器及考务端。连接验证通过后选择考试后打开“成绩报表”并刷新。未收齐显示缺卷清单，重算显示当前版本计分进度。表格下方均分固定，横向查看动态列；长列标题换行并提供悬浮文本。

CLI 沿用现有凭据环境变量，不把令牌写入命令行或文件：

```powershell
dotnet run --project src/Exam.Cli --configuration Release -- reports status synthetic-exam --server https://localhost:5001
dotnet run --project src/Exam.Cli --configuration Release -- reports show synthetic-exam --server https://localhost:5001
dotnet run --project src/Exam.Cli --configuration Release -- reports export synthetic-exam ./synthetic.xlsx --server https://localhost:5001
```

以上标识与目标为合成示例。默认仅 HTTPS；显式本机隔离配置才允许数字回环 HTTP，不跟随重定向。CLI 返回 0 成功、2 参数错误、3 身份拒绝、4 未开放/版本拒绝、5 网络不可达、6 服务端或文件错误、130 用户取消。Ctrl+C 取消同一请求及保存流程。

考务端每秒在活动页面检查状态；考试、连接、身份、页面或版本变化清空旧表并取消旧任务，包括尚未完成的首次加载和自动加载。状态查询超过五秒即隐藏旧表，即使传输忽略取消也不回填迟到响应。轮询有一次检查周期的显示延迟，服务端每次查询/导出独立校验。手动取消后停止自动加载，须手动刷新；取消文件选择保留当前表格，保存对话框期间的上下文变化禁止开始下载。传输失败或版本失效使导出失败并隐藏旧结果。

客户端先验证文件类型、长度、身份、快照、SHA-256 及 ZIP 中唯一非空的类型声明、根关系和工作簿部件，拒绝截断容器、普通 ZIP、重复/空部件及歧义响应头。该检查不代替完整 OpenXML 语义验证。通过后写目标目录唯一临时文件，刷新到磁盘，最终核验状态后替换目标；考务端和 CLI 导出均有覆盖响应体与保存的五分钟总期限。取消、断网、损坏响应、文件占用或快照失效保留原目标并清理临时文件。已成功提交的文件不能追溯撤回；进程强制终止可能留下唯一 `.partial` 文件，重新导出不使用它，可人工清理。第三方 XLSX 生成调用不支持内部取消，边界观察取消，后台生成期间界面仍可操作。

## 并发保存与恢复

共享 `ReportFileCommit` 现在以进程内固定单通道协调下载保存器和独立写入器的最终复核及替换。下载及临时文件持久写入保持并发；等待期间可取消，轮到提交时才执行当前状态复核，因此排队期间失效的快照不能覆盖较新的文件。保留 Windows 短暂占用的有限重试，延迟期间释放通道，每次重新进入后再复核；不采用删除目标再复制的回退。复核失败、取消或替换异常均释放通道并清理自身临时文件。该协调不跨进程，也不替代服务端逐次授权、主备持久确认或快照校验。
