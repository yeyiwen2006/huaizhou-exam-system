# F01 集中实现与 Windows 交接

本轮关联 [F01 #7](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/7)，初始基线 `origin/main@0c4b8df`，收尾已同步最新 `origin/main@1545840`，集中分支 `codex/f01-local-management`。用户确认自行关闭 #57、#58、#59，将适用成果接续到一个实现 PR；不删除或覆盖旧分支，不处理已合并分支。

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

重启查询和原请求重试可以在锁内执行只读核对：Stop 要求实际进程已停止；Configure 要求原请求的完整配置与持久版本 expectedVersion+1 同时相符；Start 要求实际进程运行、重新自检通过及当前 HTTPS 就绪身份/代次核对通过。确认结果重新落盘后才返回 Completed，不重复原副作用。原请求 JSON 与摘要一同保存在受保护记录中，旧格式缺少原请求仍可加载，但不能据此推断配置保存成功。

备份和隔离暂存也可核对中断结果：在实际进程已停止时，只读检查绑定原请求/操作者/路径的备份清单、文件摘要、恢复标记及真实 `pg_verifybackup`。完整备份可恢复为 Completed；已核对暂存只恢复为 Staged，仍是 `recoveryVerified=false`、`businessReady=false`，继续阻止业务启动。核对不访问业务数据库、不产生数据库记录、不执行 `pg_basebackup` 或再次复制恢复；部署工具和受控路径仍需有效。缺少或损坏证据、较新或不同配置继续保持 NeedsVerification，不删除记录。

管理状态与普通配置查询不依赖操作记录目录可写。操作目录缺失、损坏或不可写时拒绝变更；初始化不自动创建部署目录。配置和操作文件使用同目录临时写入、刷新及完整替换。目录必须预先由部署人员设置 ACL，不能把其放进仓库、备份源目录或其他用户可写目录；操作目录专用于操作记录，不放服务配置等其他 JSON。

## 部署配置与运行

两个宿主分开运行，业务服务停止不停止管理进程。管理进程现已接入 Microsoft 官方 WindowsServiceLifetime，在 SCM 启动时使用服务托管，普通启动仍使用 console 生命周期；真实 Windows SCM 安装及正向启停尚未实测。业务 SCM 适配器只操作部署时固定的业务服务名称，不能由请求传入命令或任意服务名。

通过受保护的本机部署配置或进程环境提供以下项。模板只描述键，不保存令牌、连接口令、证书或本机运行数据。

| 键 | 要求 |
| --- | --- |
| `Management:ServerId` / `NodeId` | 本机稳定身份，与外部服务配置一致 |
| `Management:HttpsPort` | 1—65535，区别于业务 HTTPS 端口 |
| `Management:BindAddress` | 可省略；如设置仅允许 localhost/127.0.0.1/::1；当前实际监听 IPv4 回环 |
| `Management:CertificateThumbprint` | LocalMachine/My 中唯一、有效且私钥可用的管理 TLS 证书，客户端信任并校验回环地址 SAN |
| `Management:BusinessServiceName` | 已部署的受控 Windows 业务服务名称，部署账户有查询/启停权限 |
| `Management:ServiceName` | 管理宿主自身在 SCM 注册的服务名称；与业务服务独立，在服务模式下必填 |
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
- 当前发布题包及入口资源引用存在、长度相符；使用节点私钥解包数据密钥并认证全部密文分块及结束标记。复用学生端 ZIP/清单读取器核对考试、版本、路径、文件完整性及显示文件头，当前发布题目结构必须与清单一致。明文仅在共享 256 MiB 上限内的内存中处理，处理后及扩容后的旧缓冲清零，不落盘或返回。
- 已提交状态与持久答卷、原回执的成功幂等结果、计分 outbox 一致。
- 可靠事件的参试/资源引用与已确认事件的原确认记录一致。

同一探针在业务宿主启动时独立运行。恢复检查通过前，HTTP 业务 API、SignalR 建连/协商、定时触发、收卷恢复及可靠通知后台任务都被门禁阻断，`/health/ready` 返回 503。后台任务通过注册包装器延迟启动，原业务实现和 ActiveNodeGuard 继续保留；后台任务自身故障继续传递给宿主。探针失败每 10 秒重查，通过后完成本次启动核对；它不是持续完整性扫描。

这些检查不是完整领域事件语义重放；显示文件检查复用共享读取器，不能当作真实 PDF 渲染验收。尚未提供生产隔离数据库选择/启动/切换公共流程，不将原数据库的观察当作隔离恢复成功。macOS 已完成下述真实隔离副本继续收卷演练，生产接线及 Windows 部署验收仍需补齐。

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

## 物理恢复与题包补强实际检查（2026-09-27）

本批沿同一分支接续 `175d743`，再次明确获取 `origin/main` 后仍为 `0c4b8df`。本地 Codex 项目规则写在仓库上一层的 AGENTS.md，仅保存个人协作偏好，仓库 AGENTS.md 未修改。本批无数据库迁移、公共业务契约变化或 NuGet 新包。

- 通用单元回归 **1021/1021**；最终非 TeX 集成回归 **148/148**，均无失败、无跳过。新增题包内容/发布结构检查 8 项、备份/暂存中断接口核对 4 项、真实物理恢复 1 项及操作/文件证据单元检查 4 项。最终显式执行集成项目 Release build，**0 警告、0 错误**。
- Docker PostgreSQL 18.6 专用合成源容器，回环随机端口、tmpfs；每项数据库测试使用自有随机数据库。物理测试通过真实 `pg_basebackup` 制作备份、真实 `pg_verifybackup` 校验，调用产品协调器复制隔离暂存，再把副本拷入测试专用匿名卷，在另一个 PostgreSQL 实例启动。测试适配器仅将既有 Windows 工具调用委派给 Docker 内真实工具，不修改生产工具路径，也不将模拟工具结果充当实测。
- 从暂存的合成节点密钥材料重新构建解密证书；恢复库的启动探针通过。测试宿主使用现有 PostgreSQL 注册、中间件、JSON 规则与交卷 HTTP 接口，查询备份前的原回执，继续接收另一份答卷，原请求重试返回同一新回执；宿主关闭后重建持久收卷处理器，确认两份答卷收齐。原库仍只有原答卷，备份原件和暂存标记未改动。核对备份/暂存证据还使用不可连接的业务连接配置证明不访问业务数据库。
- **发现并修复产品缺陷：** 物理数据库暂存只复制文件，漏掉 `pg_notify` 等空目录；`pg_verifybackup` 仍可通过，但隔离副本无法启动。现在安全枚举保留空目录，同时继续拒绝符号链接/联接点。真实副本启动和继续收卷测试已覆盖修复。
- 演练初期还修正 macOS 不支持测试 PFX 临时加载标志、Linux 副本数据目录权限、测试宿主遗漏 JSON 字符串枚举配置的问题；文件故障测试最初异常类型断言错误已修正。这些早期失败没有记为通过，最终完整回归才作为证据。
- 原生 Windows SCM、TLS、证书存储及 ACL、WPF、完整解决方案、实际双机与断电尚未验证。生产仍返回暂存阶段；测试启动副本不代表产品已经具有自动切换恢复库的能力。完整解决方案仍沿用本轮早先实跑的 NETSDK1100 限制，未因新增平台无关测试声称 Windows 构建通过。

物理测试同时带 `Category=Postgres` 和 `Category=PostgresPhysical`。未显式提供专用源容器及 Docker 可执行路径时，此项明确显示跳过，普通 PostgreSQL 检查不替代物理演练；本机最终回归显式提供了它们，因此 148 项无跳过。可在 macOS/Linux/Windows 的隔离 Docker 环境重复执行；镜像必须匹配源 PostgreSQL 主版本。

```sh
# 先创建仅含合成数据的专用 postgres:18 源容器：
docker run --detach --name codex-f01-physical-source-test --publish 127.0.0.1::5432 --env POSTGRES_HOST_AUTH_METHOD=trust --tmpfs /var/lib/postgresql postgres:18
docker port codex-f01-physical-source-test 5432/tcp
# 将输出的回环端口配置到 EXAM_TEST_POSTGRES；连接、日志和密钥不得写入仓库。
# 显式设置 EXAM_TEST_PHYSICAL_SOURCE_CONTAINER=codex-f01-physical-source-test
# 以及 EXAM_TEST_DOCKER_EXECUTABLE 为本机 docker 的绝对路径。
TMPDIR=/private/tmp/ dotnet test tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --no-restore --configuration Release --filter 'FullyQualifiedName~PostgresPhysicalRecoveryTests' -m:1 -nr:false
TMPDIR=/private/tmp/ dotnet test tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --no-restore --configuration Release --filter 'Category!=Latex' -m:1 -nr:false
TMPDIR=/private/tmp/ dotnet test tests/Unit/Exam.UnitTests/Exam.UnitTests.csproj --no-restore --configuration Release --filter 'Category!=Postgres&Category!=Latex' -m:1 -nr:false
# 演练结束后只清理自己创建的专用源容器；测试自动删除自己的副本及匿名卷。
docker stop codex-f01-physical-source-test
docker rm codex-f01-physical-source-test
```

### 剩余代码缺口与平台验证分别处理

收尾获取远端时，main 已合并 #61。为保留已推送的集中分支历史，将最新 main 合入任务分支，不强推。该更新仅改 D03 约定、计划和合成 TeX 样包，C# 源码、测试项目及依赖锁没有变化；1021/148 的模块检查证据对应同一 C# 实现，同步后的检查为差异、引用和接口约定静态核对，没有把它称作重跑业务测试。TeX 样包未在本轮编译。

已核对 #61 的[后续坐标接线约定](module-interface-integration.md)：当前共享学生读取器仍为 v1，不能读取新增 `option-regions.json`。本次恢复检查复用当前严格读取器，未放宽路径或文件允许清单；待 D/B 交付清单 v2、组包与读取器后，需沿同一入口补齐恢复回归，不能把本次合成 v1 演练当作坐标功能验证。

生产恢复缺少受控隔离 PostgreSQL 宿主、固定恢复连接的目标核对、恢复阶段持久推进和业务连接切换。普通配置保存目前被 Staged 门禁阻止，不能删除记录绕过。需要由 A 确认恢复目标与旧库的隔离证据、允许切换的条件、秘密连接配置的版本及失败回退方式，再把现有探针和收卷接口接到该目标；不能仅凭隔离库可连或旧备份完整就开放业务写入。这是接口/恢复语义缺口，不是必须等 Windows 才能写的普通代码。

管理进程的 Windows Service 生命周期代码已补齐，原生 SCM、TLS/私钥/ACL 与完整 WPF 解决方案仍需要 Windows 实测。F01 共享客户端及 CLI 处理器已提供，CLI 注册及考务管理界面仍分别与 E/C 按任务边界协调。

## 2026-09-27 接口复核补充

本轮通过已登录的 Edge 实际读取 #7 正文及全部可见评论；最新 A 接口确认是 2026-09-26 12:05，与 ADR 0002 及用户转述一致。GitHub 连接器能识别账号，但仓库元信息、Issue 和评论均返回 404；不能据此认定 Issue 不存在。读取替代方法仅记入本地项目 AGENTS.md。

已逐项复核回环独立 HTTPS、数据库无关认证/状态/停止、共享 Bearer 操作者校验、九条管理路径、请求与配置版本、受保护本机记录及中断核对、未知代次、恢复分阶段和业务 ActiveNodeGuard 保留，当前已实现部分遵循上述约定。操作仍同步等待结果，未实现异步受理，因此不声称具有 202 后台任务模式。恢复仍只返回 Staged，生产恢复闭环未完成；CLI 处理器/注册及 F04 消费接线不能以管理 API 完成代替。

发现并修正超大 JSON 请求的错误分类：超过 64 KiB（含不提供 Content-Length 的流式请求）在副作用和操作记录之前返回 400/INVALID_INPUT，不再把缓冲上限异常报告为 503 依赖故障。新增两个接口场景证明不发停止命令且不产生记录。实际运行管理接口专项 **21/21**，非 PostgreSQL/非 TeX 集成回归 **48/48**；本轮未重跑先前的 1021 单元及 148 数据库/物理恢复回归，不将新专项结果与历史结果相加。生产恢复、原生 Windows、CLI 接线及 T01/T02 的缺口仍按本文件记录保留。约 75% 为 F01 开发工作量估算，不是 A 审查结论或验收比例。

本轮显式集成项目 Release 构建在正常本机权限下通过，0 警告、0 错误（1.76 秒）；沙箱构建停滞并取消，不记为通过。再次获取远端 main，仍为 `1545840`，集中分支已包含该基线。本次没有新增依赖、迁移或修改仓库 AGENTS.md。

## 共享管理客户端、CLI 处理器及服务托管接续（2026-09-27）

新增 `Exam.Infrastructure.ServiceManagement.LocalManagementClient`，GUI/CLI 可共用。它仅接受回环 HTTPS 根地址及显式预期服务器/节点，不访问业务健康检查，不接受 URL 凭据、非根路径或查询串。生产默认传输禁用代理、重定向及 Cookie，使用系统 TLS 校验；注入传输仅供可信组合及测试，不应给生产传入绕过证书校验的 handler。凭据在每次请求时从调用者的受保护配置提供，不接受命令行明文。客户端验证响应身份、未知代次语义及操作请求/类型绑定；完成阶段须有对应实际状态/配置/检查/备份/恢复证据，不能把 Staged 或空结果显示为完整恢复。

新增 `LocalManagementCliHandler.RunAsync(command, inputJson, requestId, output, errors, cancellationToken)`，提供给 E 注册，不改 `Exam.Cli/Program.cs`。`status`、`configuration` 无输入；`operation` 使用原 requestId；`start`、`stop`、`checks`、`configure`、`backup`、`restore` 使用原始 JSON 输入，其 requestId 保留在请求体，不能再在外层生成另一标识。直接复用服务端九条路径和 JSON/身份/幂等规则，不维护另一套业务校验。JSON 原文保留伪造 actor 等字段，由管理宿主拒绝；配置修改继续由服务端核对 expectedVersion。

E 接线示意（参数解析、文件/标准输入读取与命令注册由现有 CLI 框架负责）：

```csharp
using var client = new LocalManagementClient(managementUri, expectedServerId, expectedNodeId,
    () => protectedConfiguration["ExamSecurity:OperatorToken"]);
return await new LocalManagementCliHandler(client).RunAsync(command, inputJson, queryRequestId,
    Console.Out, Console.Error, cancellationToken);
```

管理地址和预期身份须使用独立部署配置，不能用 EXAM_SERVER_URL 或业务健康检查结果决定是否允许连接。所有结果保留管理封套及阶段；成功查询/完成返回 0，输入 2、身份 3、状态/版本冲突 4、网络或响应不可核实 5、服务或持久化失败 6。Accepted/Staged 返回 4 并输出原请求状态，表示未完成的状态，而非恢复成功；关键自检失败和 NeedsVerification 返回 6。取消返回现有 CLI 约定的 130；超时、取消或连接失败均不自动重试变更，按原 requestId 查询。

管理宿主增加精确版本 `Microsoft.Extensions.Hosting.WindowsServices 10.0.12`（与当前 ASP.NET Core 依赖补丁一致），同步管理及集成测试锁文件。使用官方 `AddWindowsService`，SCM 模式的配置根目录取可执行文件目录，避免从 system32 加载配置；服务名称独立于受控业务服务。正常启动保持 ConsoleLifetime。清除包括服务注册可能添加的 EventLog 日志提供者，不输出管理请求、凭据、路径或工具内容；本机持久操作记录仍是审计入口。依据 [Microsoft Windows 服务托管说明](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-10.0)。

晚上部署时：在仓库外发布并准备受保护配置、操作目录及管理 HTTPS 证书；为管理宿主注册独立服务，名称与 Management:ServiceName 一致，使用专用部署账户；确认其配置/私钥权限、受控业务服务查询启停权限及两个宿主生命周期独立。不要将 localhost 开发证书作为服务证书，不修改业务服务注册来掩盖管理注册问题。首先实测管理 SCM 启停、业务停止后的认证/状态与启动，以及管理重启后原操作查询；本轮不将 macOS 的 console/TestServer 生命周期当作 SCM 正向验证。

### 本批实际验证

macOS ARM64、SDK 10.0.401，TMPDIR=/private/tmp：

- `dotnet restore tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --locked-mode` 通过；`dotnet restore src/Exam.Cli/Exam.Cli.csproj --locked-mode` 通过。新增官方依赖按精确版本还原并同步锁，未改变原依赖版本。
- `dotnet test tests/Unit/Exam.UnitTests/Exam.UnitTests.csproj --no-restore --configuration Release --filter 'Category!=Postgres&Category!=Latex'` **1043/1043**，无失败、无跳过。新增 22 项客户端地址、身份/代次、无凭据、重定向/格式/大小及结果证据校验。
- `dotnet test tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --no-restore --configuration Release --filter 'Category!=Postgres&Category!=Latex'` 最终 **56/56**，无失败、无跳过。新增 CLI/管理路由接线 6 项、宿主启动生命周期 1 项、真实回环 TLS 拒绝未信任证书 1 项。TLS 测试使用临时合成自签证书且不导入任何系统证书库，确认没有管理变更到达服务端。
- 显式 CLI 及集成项目 Release build 均 **0 警告、0 错误**。CLI 首次 no-restore 缺本机 assets（NETSDK1004），按现有锁还原后通过。生命周期测试先后发现应用名使用完整程序集名、TestServer 主动替换为 NoopHostLifetime；分别改为短应用名、在注入 TestServer 前核对原宿主生命周期后通过。新增 TLS 测试首次缺两个 using，补齐后通过。
- `dotnet restore Huaizhou.ExamSystem.sln --locked-mode` 本批再次实际返回 **NETSDK1100**（Windows/WPF 目标），未执行完整解决方案 build/test；不以分项目构建替代该检查。
- 本批未重跑 PostgreSQL/物理恢复及 TeX，未改变数据库/恢复适配器或探针；此前 148 项的记录仍是历史证据。未验证原生 SCM、Windows 服务账户/证书私钥 ACL、WPF、真实双机或断电、T01/T02。CLI 命令注册未实现，测试调用的是可供注册的实际处理器，不声称 CLI 可执行文件已支持这些新命令。

## 工作边界查漏与故障处理补强（2026-09-27）

依据仓库需求、任务映射、实施指南、恢复边界及 ADR 0002 新增 [责任与缺口清单](f01-scope-checklist.md)，明确生产恢复运行闭环仍属 F01，不能以 T01/T02 归属为理由留作测试期实现。F02 提升/复制、F04 界面、F05 工具与 E 的注册各有边界，F01 的接口、开发期模块验证及与工具衔接仍需完成。尤其补记可恢复备份可用性、归档/程序提交关联、事件/定时实际运行、恢复后计分及新 Windows 账户/主机密钥恢复证据的未完成项；约 75%—80% 只是开发工作量估算。

本轮修正操作目录扫描：先检查已打开文件长度，再有界读取，损坏/超大记录不触发无界文件分配，也不产生新的启动副作用，保留原件。共享管理客户端对响应头与响应体使用同一截止时间；读取中断或响应超时返回网络不可核实，调用者取消仍为 130，不自动重试变更，须用原 requestId 核对。

提取 `ManagementEntryPoint.AddManagementRuntime` 供生产 Main 和接线测试使用同一延迟注册代码；业务数据库/配置/备份依赖仍按操作加载。业务 readiness HttpClient 由 DI 工厂创建并随宿主释放，禁止代理、重定向及 Cookie；Main 也释放应用容器。生产注册测试注入缺失/损坏/有效服务配置、缺失操作目录、错误业务服务名/就绪 URL、无效数据库连接及备份超时配置，证明有效身份仍能查询状态，无凭据仍拒绝，秘密不出现在响应且不创建部署目录。业务服务名刻意无效，测试不访问真实 SCM。

新增两个无凭据的 Windows 部署模板及说明，模板只含合成身份/路径和证书指纹占位；填好的配置、证书/私钥或实际连接材料仍留在仓库外。模板不是可运行配置，不新增恢复切换协议。

本轮实际命令（同一 macOS ARM64、SDK 10.0.401、TMPDIR=/private/tmp）：

```sh
dotnet test tests/Unit/Exam.UnitTests/Exam.UnitTests.csproj --no-restore --configuration Release --filter 'Category!=Postgres&Category!=Latex'
dotnet test tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --no-restore --configuration Release --filter 'Category!=Postgres&Category!=Latex'
```

通用单元 **1047/1047**，非 PostgreSQL/非 TeX 集成 **60/60**，均无失败、无跳过。新增响应体超时/取消/读取故障 3 项、真实文件大小/阻断 1 项、生产注册故障诊断 4 项。此次未重跑 PostgreSQL/物理恢复、TeX 或完整解决方案；无新增依赖/迁移，沿用上一批完整还原 NETSDK1100 的平台限制，不记为完整构建或验收通过。

另执行集成项目 `dotnet build tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --no-restore --configuration Release --verbosity quiet`，0 警告、0 错误。在仓库外 `/private/tmp/f01-process-audit-20260927` 构建临时验证程序（Release，0 警告、0 错误），运行 `python3 /private/tmp/f01-process-audit-20260927/check.py`：独立 .NET 进程通过实际 ManagementOperationRunner/FileManagementOperationStore 写入受理记录和一条合成副作用后，被 Python 强制终止；全新进程重放原请求得到 NeedsVerification，不重复副作用，并阻止新启动请求。结果通过。此检查覆盖 macOS 真实进程退出及锁释放，不覆盖 SCM、断电、文件系统持久性或真实业务服务恢复，不计入上述测试项数。临时程序和记录不提交仓库。

## 启动备份完整性补齐（2026-09-27）

生产管理宿主接入 `BackupIntegrityStartupReadinessProbe`，按部署固定的 `Backup:StartupVerificationDirectory`/`StartupVerificationBackupId` 核对备份清单、数据库工具校验和资源/受保护材料摘要，结果为 `backup-integrity`。缺失或不一致时阻止启动，但不阻断认证/状态/备份初始化。只读核验路径不连接业务数据库、不依赖原资源源目录、不创建恢复目标，不能据此声明完整恢复成功。选择配置当前需重启管理宿主生效。备份元数据也改为先核对打开文件长度，再有界读取，避免长度检查和重新打开之间的无界分配。

本轮实际运行（macOS ARM64 / SDK 10.0.401）：

```sh
TMPDIR=/private/tmp dotnet test tests/Unit/Exam.UnitTests/Exam.UnitTests.csproj --no-build --configuration Release --filter 'Category!=Postgres&Category!=Latex'
TMPDIR=/private/tmp dotnet test tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --no-restore --configuration Release --filter 'Category!=Postgres&Category!=Latex'
```

单元 **1053/1053**、集成 **61/61**，无失败/跳过。首次单元运行未指定 TMPDIR，默认 /var 路径经过符号链接，86 项路径保护场景失败；保留保护并按已约定的 /private/tmp 重跑通过，没有放宽校验。新增 6 项单元、1 项生产自检路由/持久查询接线。另以仓库外临时程序配置独立合成 PostgreSQL 18 容器、随机凭据、回环随机端口，实跑 `FullyQualifiedName~PostgresPhysicalRecoveryTests` **1/1**：真实 pg_verifybackup 的新生产核验路径通过，继而恢复独立实例、恢复密钥解锁、原回执查询、剩余收卷及收齐重建通过；源库/暂存证据保留，本次容器均清理。无完整解决方案、Windows、TeX 或计分恢复新证据，不合并历史测试数量。

[生产恢复切换方案](f01-recovery-switch-proposal.md)把仍未接通的步骤列成实际调用差异和目标/权限/旧备份投入使用/续行请求绑定决定。它尚未实施，不当作闭环完成。开发估算仍为 75%—80%，不因自检修正宣称生产恢复完成。

最终集成项目 Release 构建 `dotnet build tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --no-restore --configuration Release --verbosity quiet`：0 警告、0 错误；合成 JSON 与本地文档引用校验通过。

## 晚上交给 Windows Codex 的指令

请在同一分支 `codex/f01-local-management` 继续 F01 #7，先读 AGENTS.md、ADR 0002 和本文件。保留当前成果与用户修改；已关闭 #57/#58/#59 已接续，不重开细碎 PR，不删除旧分支，不合并或推送 main。获取最新 origin/main，在集中分支同步基线，不强推。启动恢复、题包内容、业务/后台门禁、各类中断操作核对及真实物理副本继续收卷已在 macOS 完成模块验证，先复用这些实现和演练。优先验证 Windows 实际 SCM、管理 TLS 与 ACL，再按上节明确的边界接通生产恢复目标选择、核对和受控切换。不能将已通过演练写成生产恢复入口已完成。若缺 A 模块公共恢复入口，列出具体调用与缺口请 A 协调，不能猜接口、自动迁移或覆盖原库。

在隔离 Windows 环境检查真实 SCM/管理 TLS、ACL、数据库故障时认证和状态、备机管理、请求持久重试与崩溃窗口、配置冲突、启动失败及恢复继续收卷。真实业务配置、日志、证书、备份与测试报告保存在仓库外；仅提交脱敏结果。执行仓库三条检查并保存实际结果：

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release
dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release
```

外部依赖分类按仓库规则实跑；缺环境记录未执行原因。先跑 F01 专项 `FullyQualifiedName~ServiceManagement` 与 LocalManagementApiTests，再执行受影响回归；Release 必须 0 警告、0 错误。依赖/恢复接线未完成则如实标记阶段交付，不能声称全部 F01 完成。最终只提一个按仓库模板填写的集中 PR，关联 #7（不自动关闭），列实际输入输出、实测/未测、来源、依赖及回退，交 A 审查；创建后用 attach_artifact 关联当前任务。
