# F01 集中实现与 Windows 交接

本轮关联 [F01 #7](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/7)，基线 `origin/main@0c4b8df`，集中分支 `codex/f01-local-management`。用户确认自行关闭 #57、#58、#59，将适用成果接续到一个实现 PR；不删除或覆盖旧分支，不处理已合并分支。

## 接续来源与当前能力

| 来源 | 接续内容 |
| --- | --- |
| 已合并 #60 | [ADR 0002](decisions/0002-local-management-access.md)，直接继承 main |
| 已关闭 #57，`3a02023` | 节点证书私钥检查、真实文件探针接线测试 |
| 已关闭 #58，`e2d8b8e` | 受控进程启动后核对 HTTPS 业务就绪与服务身份 |
| 已关闭 #59，`d016ca3` | 严格备份清单、恢复标记校验 |
| 原分支 `aa37f15` | 备份工具错误输出保护；远端 PR 引用中未找到对应 PR |

本轮新增独立 `Exam.Management` 宿主、共用操作者验证、管理封套与操作契约、本机文件操作记录、管理接口和读写接线测试。业务中间件保留原 ActiveNodeGuard 前置检查。管理认证、状态和停止不要求业务数据库可用；业务配置损坏时状态包含阻断检查，配置不会被默认值覆盖。

所有管理写请求明确声明预期服务器/节点头，启停、配置、自检的请求体还声明目标。真实操作者来自受保护凭据验证。记录绑定本机、操作者、操作类型与规范化请求内容 SHA-256；同标识不同内容拒绝执行。副作用前持久受理，结果落盘后才返回完成，多个管理实例通过本机独占锁串行执行。操作结果落盘失败或受理后取消时不得盲目重做；进程内仍在执行的操作显示 Accepted。

重启查询和原请求重试可以在锁内执行只读核对：Stop 要求实际进程已停止；Configure 要求原请求的完整配置与持久版本 expectedVersion+1 同时相符；Start 要求实际进程运行、重新自检通过及当前 HTTPS 就绪身份/代次核对通过。确认结果重新落盘后才返回 Completed，不重复原副作用。原请求 JSON 与摘要一同保存在受保护记录中，旧格式缺少原请求仍可加载，但不能据此推断配置保存成功。未知结果、较新或不同配置、备份和隔离恢复保持 NeedsVerification/Staged，不自动清除记录。

管理状态与普通配置查询不依赖操作记录目录可写。操作目录缺失、损坏或不可写时拒绝变更；初始化不自动创建部署目录。配置和操作文件使用同目录临时写入、刷新及完整替换。目录必须预先由部署人员设置 ACL，不能把其放进仓库、备份源目录或其他用户可写目录；操作目录专用于操作记录，不放服务配置等其他 JSON。

## 部署配置与运行

两个宿主分开运行，业务服务停止不停止管理进程。管理进程当前按独立 console/进程宿主提供，尚未实现 SCM Windows Service 生命周期托管；不得仅用 `sc create` 把普通 console 程序冒充可运行的管理 Windows 服务。业务 SCM 适配器只操作部署时固定的业务服务名称，不能由请求传入命令或任意服务名。

通过受保护的本机部署配置或进程环境提供以下项。模板只描述键，不保存令牌、连接口令、证书或本机运行数据。

| 键 | 要求 |
| --- | --- |
| `Management:ServerId` / `NodeId` | 本机稳定身份，与外部服务配置一致 |
| `Management:HttpsPort` | 1—65535，区别于业务 HTTPS 端口 |
| `Management:BindAddress` | 可省略；如设置仅允许 localhost/127.0.0.1/::1；当前实际监听 IPv4 回环 |
| `Management:CertificateThumbprint` | LocalMachine/My 中唯一、有效且私钥可用的管理 TLS 证书，客户端信任并校验回环地址 SAN |
| `Management:BusinessServiceName` | 已部署的受控 Windows 业务服务名称，部署账户有查询/启停权限 |
| `Management:ServiceConfigurationPath` | 仓库外初始非秘密 ServiceConfiguration JSON 的绝对路径，父目录已受保护；不能默认创建 |
| `Management:OperationDirectory` | 仓库外专用受保护记录目录，已创建且不经过联接点 |
| `Management:MinimumAvailableBytes` | 正数，未设置时为 1 GiB；现场按实际容量配置 |
| `Management:BusinessReadyEndpoint` | 固定 HTTPS `/health/ready`，不跳过证书校验或自动跟随重定向 |
| `ExamSecurity:OperatorToken` / `OperatorId` | 同业务宿主的令牌规则，至少 32 字符与有效身份；缺失时管理访问返回 401 |
| `ExamServer:ServerEpoch` | 仅供启动时与权威数据库状态及业务就绪响应比较，不填入未核实管理封套 |
| `ConnectionStrings:Exam` | 业务依赖；缺失/无效/数据库故障不会阻止管理认证与状态 |
| `PackageSecurity:CertificateThumbprint` | 业务节点解密证书，独立于管理 TLS 加载，缺失时自检阻断 |
| `Backup:*` | 沿用业务宿主现有 PostgresBinDirectory、AllowedBackupRoot、RestoreStagingRoot、VersionedResourcesDirectory、ProtectedConfigurationDirectory、ToolTimeoutMinutes |

拒绝 `Urls` / `Kestrel:Endpoints` 额外监听设置。管理证书本身失效时宿主无法提供 HTTPS，按部署恢复处理；不会降级 HTTP。管理进程使用受保护配置，但当前不自动验证 Windows ACL，也没有替代现场权限验证。

```powershell
dotnet run --project src/Exam.Management/Exam.Management.csproj --configuration Release --no-launch-profile
```

配置保存在仓库外后启动；不得在命令行直接传口令。GUI/CLI 使用独立管理连接，不先访问业务 `/health/ready`。F04/C 公共界面及 E CLI 注册本轮没有修改，后续按 ADR 协调接入，尚无端到端 GUI/CLI 验收。

管理备份目前要求业务进程停止，避免与旧业务备份端点并行操作本机备份源；PostgreSQL 本身仍需在线以执行物理备份。管理记录不依赖业务数据库，不替代考试数据或主备权威记录。

## 明确未完成的闭环

这版不是 F01 功能完成或现场验收。2026-09-27 的追加实现已用真实只读恢复探针替换固定失败的 `recovery-verification` 占位。探针在 PostgreSQL RepeatableRead、READ ONLY 快照中检查以下证据，失败保留数据并阻断启动：

- 已分配客户端编号的日期/序号与持久日序列一致。
- 版本流修订连续、投影头及关键版本/时间/生命周期一致，定时入口投影与版本投影一致。
- 当前发布题包及入口资源引用存在、长度相符；使用节点私钥解包数据密钥并认证全部密文分块及结束标记，明文输出丢弃。
- 已提交状态与持久答卷、原回执的成功幂等结果、计分 outbox 一致。
- 可靠事件的参试/资源引用与已确认事件的原确认记录一致。

同一探针在业务宿主启动时独立运行。恢复检查通过前，HTTP 业务 API、SignalR 建连/协商、定时触发、收卷恢复及可靠通知后台任务都被门禁阻断，`/health/ready` 返回 503。后台任务通过注册包装器延迟启动，原业务实现和 ActiveNodeGuard 继续保留；后台任务自身故障继续传递给宿主。探针失败每 10 秒重查，通过后完成本次启动核对；它不是持续完整性扫描。

这些检查不是完整领域事件语义重放，也不校验解密后 ZIP/PDF 的内容。尚未提供隔离数据库选择/启动/切换公共流程，不将原数据库的观察当作隔离恢复成功。后续恢复继续收卷验收仍需 A 协调既有模块并在隔离环境完成。

现有 PostgreSQL 恢复适配器只做隔离暂存。新管理接口返回 Staged，`isolationStaged=true`、`recoveryVerified=false`、`businessReady=false`，不调用原库自检来冒充隔离库验证。存在 Staged/Accepted/NeedsVerification 时阻止新启动、配置修改及再次恢复；状态、自检和停止仍可用。只读核对探针可对部署连接运行，但尚未接入隔离 PostgreSQL 的启动、连接选择、受控切换及继续收卷。不得手工删记录绕过恢复门禁。额外恢复确认操作或迁移如超出 ADR 清单，先由 A 确认。

管理封套当前一律不声称观察到了权威代次，返回 null/false/null；数据库自检的比较不充当每个响应的当前代次证据。业务就绪核对适配器按部署预期代次比较，不替代 F02 的权威代次更新与接管流程。

样例见 [合成管理响应](../samples/service-management/README.md)。恢复/回退保留原库、暂存与不确定操作原件；配置失败保留旧版本；宿主出错先核对 SCM，不能自动覆盖较新答卷、恢复原库或升级迁移。

## 首批实际检查（7d7e9a8）

macOS Arm64，.NET SDK 10.0.401，真实文件测试使用 `TMPDIR=/private/tmp/`，避免 macOS 默认 `/var` 链接路径。测试运行器需要本机通信端口，沙箱内首次测试被 SocketException(13) 中止；经权限允许后实跑。

- 管理宿主与接口测试项目锁定还原成功，无新增 NuGet 包；新增项目及项目引用锁文件同步。
- 管理宿主首次 Release build 0 警告、0 错误；接口编译发现入口 Program 类型同名后改为命名空间内 ManagementEntryPoint。
- F01 专项单元 100/100 通过；增加正在执行状态的测试后，最终通用单元分类回归 1014/1014 通过，构建无警告/错误。
- 新管理接口最初 9/9 通过；接口分类回归最初 33/33 通过。随后补齐 HTTP 与错误目标拒绝检查，最终非 Postgres/Latex 接口回归 34/34 通过，构建无警告/错误。合成配置曾因相对目录导致 9 项失败，已改用绝对路径；新增测试曾在请求后修改 HttpClient 地址，已改用绝对请求 URI。
- 完整 `dotnet restore Huaizhou.ExamSystem.sln --locked-mode` 实跑返回 NETSDK1100（WPF/Windows 项目）；完整解决方案 build/test 未执行，因为完整还原未成功且当前为 macOS。
- 未执行真实 PostgreSQL、TeX、Windows SCM/管理证书 ACL、真实 HTTPS 监听、断电、隔离库继续收卷及 T01/T02；历史 Windows 结果不作为当前整合版本通过证据。

实际模块命令（目标项目 restore 已分别成功；均未启用 WindowsTargeting 绕过本机限制）：

```sh
dotnet restore src/Exam.Management/Exam.Management.csproj --locked-mode
dotnet restore tests/Unit/Exam.UnitTests/Exam.UnitTests.csproj --locked-mode
dotnet restore tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --locked-mode
dotnet build src/Exam.Management/Exam.Management.csproj --no-restore --configuration Release -m:1 -nr:false
TMPDIR=/private/tmp/ dotnet test tests/Unit/Exam.UnitTests/Exam.UnitTests.csproj --no-restore --configuration Release --filter 'FullyQualifiedName~ServiceManagement' -m:1 -nr:false
TMPDIR=/private/tmp/ dotnet test tests/Unit/Exam.UnitTests/Exam.UnitTests.csproj --no-restore --configuration Release --filter 'Category!=Postgres&Category!=Latex' -m:1 -nr:false
TMPDIR=/private/tmp/ dotnet test tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --no-restore --configuration Release --filter 'FullyQualifiedName~LocalManagementApiTests' -m:1 -nr:false
TMPDIR=/private/tmp/ dotnet test tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --no-restore --configuration Release --filter 'Category!=Postgres&Category!=Latex' -m:1 -nr:false
```

## 追加实现实际检查（2026-09-27）

同一 macOS/.NET 环境，基线再次 fetch 后仍为 `origin/main@0c4b8df`。使用独立 Docker PostgreSQL 18.6（镜像 `postgres:18`，摘要 `sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722`）进行真实数据库测试；仅回环随机端口、容器 tmpfs、合成数据，没有挂载考试目录。每个恢复探针测试独占随机创建的迁移数据库，结束删除其测试库；容器最后清理。

- 通用单元分类回归 1017/1017 通过，包含新增 3 项操作核对/内容绑定检查；构建未报告警告/错误。
- 最终非 TeX 集成回归 134/134 通过；随后新增生产注册/宿主生命周期接线检查 1/1 通过。共覆盖 135 项，其中新增恢复探针真实 PostgreSQL 测试 15 项、管理中断接口测试 5 项、后台门禁测试 3 项。测试包括实际版本发布、原交卷保存及回执查询、投影损坏、资源缺失、密文篡改、缺少密钥、幂等记录损坏、事件缺失参试、活动节点代次拒绝和后台故障传播。
- Release 集成项目构建 0 警告、0 错误；早期编译的 CS9124 警告及测试缺少命名空间已修正。首次较大集成回归 129 通过/2 失败，原因是已有测试宿主没有新门禁依赖；改为构造注入后 134 项回归通过。生产注册验证确认恢复 worker 和三类受门禁保护的业务 worker 实际可启动/停止。
- 完整解决方案锁定还原本轮再次实跑，正常本机权限下返回 NETSDK1100，仍受 WPF/Windows 目标限制；没有运行完整解决方案 build/test。沙箱内还原停滞及测试通信端口限制均未记为通过。
- `git diff --check`、样例 JSON 与文档链接静态检查通过；本轮无数据库迁移、NuGet 新包、CLI 注册或公共 GUI 文件修改。未执行 TeX、原生 Windows SCM/TLS/ACL、断电、真实双机接管、隔离恢复继续收卷、T01/T02。

实际追加命令如下；`EXAM_TEST_POSTGRES` 仅提供隔离合成数据库连接，不提交本机值。测试运行器及 Docker 连接经本机权限允许后实跑。

```sh
TMPDIR=/private/tmp/ dotnet test tests/Unit/Exam.UnitTests/Exam.UnitTests.csproj --no-restore --configuration Release --filter 'Category!=Postgres&Category!=Latex' -m:1 -nr:false
TMPDIR=/private/tmp/ dotnet test tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --no-restore --configuration Release --filter 'Category!=Latex' -m:1 -nr:false
TMPDIR=/private/tmp/ dotnet test tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --no-restore --configuration Release --filter 'FullyQualifiedName~ActualRuntimeRegistrationRunsRecoveryBeforeBusinessWorkers' -m:1 -nr:false
dotnet restore Huaizhou.ExamSystem.sln --locked-mode -m:1 -nr:false
```

## 晚上交给 Windows Codex 的指令

请在同一分支 `codex/f01-local-management` 继续 F01 #7，先读 AGENTS.md、ADR 0002 和本文件。保留当前成果与用户修改；已关闭 #57/#58/#59 已接续，不重开细碎 PR，不删除旧分支，不合并或推送 main。获取最新 origin/main，在集中分支同步基线，不强推。启动恢复检查、业务/后台门禁及停机/配置/启动的中断核对已在 macOS 完成模块接线验证，先复用现有实现。优先验证 Windows 实际 SCM、管理 TLS 与 ACL，再推进隔离恢复后的连接选择、核对、受控切换及继续收卷。若缺 A 模块公共恢复入口，列出具体调用与缺口请 A 协调，不能猜接口、自动迁移或覆盖原库。

在隔离 Windows 环境检查真实 SCM/管理 TLS、ACL、数据库故障时认证和状态、备机管理、请求持久重试与崩溃窗口、配置冲突、启动失败及恢复继续收卷。真实业务配置、日志、证书、备份与测试报告保存在仓库外；仅提交脱敏结果。执行仓库三条检查并保存实际结果：

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release
dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release
```

外部依赖分类按仓库规则实跑；缺环境记录未执行原因。先跑 F01 专项 `FullyQualifiedName~ServiceManagement` 与 LocalManagementApiTests，再执行受影响回归；Release 必须 0 警告、0 错误。依赖/恢复接线未完成则如实标记阶段交付，不能声称全部 F01 完成。最终只提一个按仓库模板填写的集中 PR，关联 #7（不自动关闭），列实际输入输出、实测/未测、来源、依赖及回退，交 A 审查；创建后用 attach_artifact 关联当前任务。
