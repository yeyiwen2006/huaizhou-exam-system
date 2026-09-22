# B04 实施状态（本地保存与恢复）

本文只记录仓库当前可验证的实现，不替代任务卡和验收报告。上游依赖状态见 `docs/b01-implementation-status.md` 与 `docs/b02-implementation-status.md`。

## 当前已完成

| 项 | 说明 |
| --- | --- |
| 本地事务存储 | `SqliteLocalStateStore`：Microsoft.Data.Sqlite **10.0.12**（首个学生端外部包依赖，版本已锁定）；WAL 日志 + `synchronous=FULL`；全部磁盘操作在独立工作线程串行执行（`BlockingCollection` + 专用线程，`Pooling=false` 保证 Dispose 后释放文件句柄） |
| 隔离与键 | `LocalStateKey(ServerKey, ExamId, CandidateId)`：按服务器（激活地址 host:port，主备同键）、考试和考生三级隔离 |
| 版本化写入 | `AnswerSession`：每次答案操作 `answer_version` 单调 +1，与全部答案、待发送快照**同一事务**落盘；无本地记录时先写初始状态（版本 1） |
| 私人标记 | 独立 `marks` 表整体替换落盘；仅本机可见，**不进入**待发送快照（不上报）；重启保留、切换账号再切回不串号 |
| 待发送快照 | `pending_snapshot` 与答案同事务写入，载荷与 A05 `SyncProgressRequest` 同构（去掉 RequestId），B05 同步直接取用 |
| 固定提交 | `fixed_submission`：首次写入后不可变更；重复固定（同 ID 重试或换 ID 重放）一律保留第一次的记录与答案 |
| 恢复与权限核对 | `AnswerSession.StartAsync` 恢复最后成功保存状态；`ParticipationId`/`EntryVersion`/`AuthorizationVersion` 任一不符 → 拒绝恢复并锁定作答（防旧授权/串号写进度）；试卷版本变更不阻止恢复（B06 在线更新沿用稳定题号） |
| 写失败门闸 | 任何写入失败 → 会话进入 `WriteBlocked`，后续答案/标记操作直接拒绝且不再触达磁盘，内存回滚到落盘前状态；页面经 `WriteBlocked` 事件明示"本地保存失败，已阻止新增修改" |
| B02 恢复记录 | `preparation` 表：等待页下载完成/确认参试后保存阶段状态；再次进入时给出恢复提示（完整等待态续接待 A04 指令通道，由 B05/B08 接入） |
| 页面接线 | 考试进行页：私人标记经会话持久化（含恢复回显）、当前题落盘、提前交卷先本地固定提交（写失败阻止交卷并明示）；等待页：确认参试传入授权上下文（`ParticipationAuthorization`） |
| 单元测试 | `Exam.StudentUnitTests` 新增 16 个用例（真实 SQLite 7 + FakeStore 会话 9） |

## 验收判据对应（执行指南 B04）

| 判据 | 现状 |
| --- | --- |
| 崩溃恢复最后成功保存状态 | ✅ `CrashRecovery_RestoresLastSavedState`（会话级，含写失败后恢复）/ `SqliteLocalStateStoreTests.CrashRecovery_RestoresLastSavedState`（进程级：关闭连接重开） |
| 写入失败明确阻止新增修改 | ✅ `WriteFailure_BlocksFurtherModifications`：失败后后续操作直接拒绝且不再触达存储（写入计数不变），内存不含未落盘修改 |
| 私人标记保留、不串号 | ✅ `Marks_IsolatedPerCandidate_AndSurviveRestart`：同考试两考生标记互不可见、各自重启保留；`Isolation_ByServerExamAndCandidate`：服务器/考试/考生三维度隔离 |
| 已固定提交不生成新答案 | ✅ `FixedSubmission_RejectsNewAnswers_ReplayKeepsOriginal`：固定后答案修改被拒、重复固定沿用原提交、崩溃重启仍为固定态；`TryFixSubmission_KeepsFirstSubmission`（存储层幂等） |

全量测试：74（单元）+ 2（集成）+ 32（学生端）= 108 通过；CI 条件（`--locked-mode` restore + Release 构建）本地验证通过；启动冒烟（设 `DOTNET_ROOT` 后 apphost 真实启动）：主窗口正常、`%LOCALAPPDATA%\HuaizhouExam\Student\state.db` + WAL/SHM 文件生成。

## 随 B04 修复的 B01 遗留缺陷

| 缺陷 | 影响 | 修复 |
| --- | --- | --- |
| `App` 静态字段初始化顺序：`Services` 声明先于 `AppDataDirectory`，AppServices 构造时读到 null | 自 B01 起 `DeviceStore` 目录为 null 的潜伏缺陷 | `App.xaml.cs` 调整声明顺序并注释约束 |
| `app.manifest` DPI 声明用了 SMI/2005 命名空间下不存在的 `<dpi>` 元素 | 激活上下文失败（SideBySide 错误） | 改为 SMI/2016 的 `<dpiAwareness>PerMonitorV2</dpiAwareness>` |
| 本机 .NET 10 SDK 仅装在 `D:\dotnet`（用户目录），apphost 按默认路径找不到运行时 | 学生端 exe 双击/直启即报"必须安装 .NET"；此前 B01/B02 冒烟判定（`-not $p.HasExited`）为假阳性 | 非代码缺陷：本机验证需设 `DOTNET_ROOT=D:\dotnet`；发布包（B07）为自包含部署，不受影响 |

## 尚未完成 / 需外部条件

| 项 | 阻塞方 |
| --- | --- |
| 快照上报、回执与重试（消费 `LoadPendingSnapshotAsync`） | B05 × A05 |
| 等待页完整等待态续接（重新订阅指令、恢复确认状态） | A04 指令通道（B05/B08 接入） |
| 凭据与解锁恢复信息的 DPAPI 保护存储 | B07（`DeviceStore` TODO） |
| `AnswerValue` 契约的 record 相等性对 `IReadOnlyList` 字段为引用比较（测试已用逐字段断言规避） | 建议契约侧补值相等（A 确认） |
| 本地存储密钥/整体加密（当前 SQLite 明库存答案与标记；执行指南仅要求凭据与解锁信息 DPAPI） | A 确认是否需要 |

## 测试文件

- `tests/Unit/Exam.StudentUnitTests/AnswerSessionTests.cs`（含 `FakeLocalStateStore`）
- `tests/Unit/Exam.StudentUnitTests/SqliteLocalStateStoreTests.cs`
