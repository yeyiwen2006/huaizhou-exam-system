# F01 开工核对与首批实施范围

日期：2026-09-24。负责人：F（@Nostalgia-cy）。关联任务：[F01 #7](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/7)。代码审查、修正复核与合并：A（@yeyiwen2006）。

## 2026-09-26 接线约定确认

当前任务边界以需求、执行指南、F01 #7 及 ADR 0002 为准。以下阶段记录保留历史，不作为当前完成状态。

A 已确认 [ADR 0002 本机管理入口、身份验证与接口边界](decisions/0002-local-management-access.md)。主、备机各有独立管理宿主，GUI/CLI 使用本机 HTTPS 管理地址，共用从现有业务中间件提取的令牌与操作者校验；管理认证及诊断不依赖业务进程、活动节点检查或数据库可用。启动、恢复和接管保留各自的权限与一致性检查。

F 可以在该决策范围内补充公共管理请求、响应封套、操作阶段和共享鉴权接口，并完成宿主接线，通过 PR 交 A 审查。下一批须提供接口清单、合成成功失败样例和实际模块验证结果；CLI 注册与 E 协调，F04 管理页与 C 协调公共界面接入。考务用户名和密码注册登录仍按需求实现，本次不将令牌接线记为登录功能完成。

本次仅确认实施约定，未新增代码或运行测试。独立管理宿主、完整业务就绪检查和恢复后继续收卷仍待实现及验收，#7 保持打开。以下保留先前阶段的实现与实测记录，历史环境或提交号不代表当前基线。

## 2026-09-24 阶段记录

当前状态：完成仓库规则与现有代码核对，已实现本机配置文件适配、应用服务的持久保存接线和数据目录启动探针；独立宿主、完整自检及恢复运行闭环尚未完成。本文件为实施方案，不改变公共契约或任务完成状态。已执行 `git fetch origin`，`codex/f01-service-management` 分支现已快进至 `origin/main` 的 `b4d4893`。F 转述 A 已确认相关目录改动须经 PR，由 A 审查合并。GitHub Issue 的匿名 API 读取返回 404，未能核实最新讨论；以下范围依据已合并仓库文件及该确认。

## 依据与交接

- [AGENTS.md](../AGENTS.md)、[协作说明](../CONTRIBUTING.md)、[总计划](../development-plan.md)与[执行指南](../agent-implementation-guide.md)约束代码和提交。
- [任务映射](task-map.md)：F01 接续旧 A07/#7；F02 接续 A08/#8；F03 接续 B08/#16；F04 接续 C07/#23 并接收 #22 服务管理部分；F05 接续 E07/#36。
- [接线顺序](integration-sequence.md)：先 F01 独立管理宿主、启停、配置、自检，再 F02 与配套 F05，再 F03/F04。D03 仍由 D 开发。
- [需求](../requirements.md)与[恢复边界](backup-and-failover-runbook.md)要求业务停机后管理可用、关键缺失阻止启动、隔离恢复后核对并继续收卷。
- [架构](architecture.md)要求应用层声明能力接口，基础设施实现，GUI/API/CLI 共用业务规则。

## 现有代码与缺口

| 位置 | 已有能力 | 首批需处理的问题 |
| --- | --- | --- |
| `src/Exam.Application/ServiceManagement/ServiceManagementService.cs` | 启停编排、配置版本检查、自检和备份恢复接口；本批接入配置存储并核对实际宿主状态 | 备份/恢复操作结果仍在内存；尚无生产 `IManagedBusinessHost` 实现 |
| `src/Exam.Contracts/ServiceManagement/ServiceManagementContracts.cs` | 配置、状态、自检、备份及恢复记录 | 优先复用；新增字段、管理操作请求标识和恢复阶段语义须先由 A 确认 |
| `src/Exam.Server/Program.cs` | 在线备份与隔离暂存恢复端点 | 与业务进程同生共死；没有独立管理入口 |
| `src/Exam.Server/Runtime/ExamApiAccessMiddleware.cs` | 考务令牌及操作者匹配校验 | 先调用活动节点门禁；不能直接作为业务停机或备机管理入口的鉴权链 |
| `src/Exam.Server/Runtime/PostgresRuntimeRegistration.cs` | 数据库、迁移、容量及节点就绪检查 | 缺完整目录/空间、证书私钥、题包和恢复核对链；不是完整 F01 自检 |
| `src/Exam.Infrastructure/ServiceManagement/PostgresBackupCoordinator.cs` | 基础备份、清单校验、受控路径、隔离暂存；接入数据库时有持久请求记录 | 隔离暂存不等于恢复后的数据库和业务可运行 |
| `src/Exam.Cli/Program.cs` | 通用 HTTPS API 调用、凭据环境变量、退出码 | 依赖可访问的 HTTP 入口；命令注册由 E 维护，F 提供处理器与接线说明 |

现有 `ServiceManagementServiceTests` 使用假宿主和假探针；其通过不能证明真实 Windows 服务启停或停机后管理可用。历史实测记录保留在 [A 组实施状态](a-implementation-status.md)，本轮不沿用为新测试结果。

第一步新增 `FileServiceConfigurationStore`：仅保存现有 `ServiceConfiguration` 中不含秘密的本机字段，在受部署账户保护的既有目录用同目录临时文件写入、刷新并替换；并发实例通过锁文件和 `expectedVersion` 拒绝旧写入，重新创建对象可读取已保存版本。文件缺失、损坏、节点身份变化及路径经过联接点均明确失败；不自动创建部署目录或默认配置。`ServiceManagementService` 现在要求配置存储器，保存成功后才发布新版本，也可从存储器重新加载；生产独立宿主尚未接线，因此不能称为“持久配置功能已交付”。

本批另增 `LocalStorageStartupReadinessProbe`，核对数据目录存在、路径无联接点、空间达到配置下限及实际写入/刷新/清理；失败项阻止业务写入，不自动创建目录。`CompositeStartupReadinessProbe` 要求每个子探针返回结果，避免空检查被视为通过。尚未与数据库、证书、题包、事件和定时恢复探针完整组合，也未接到独立管理宿主。

## 首批可审阅目标：独立管理与可靠启停

以下保留首批实施目标。F01 的归属和开工顺序已由总计划、执行指南及任务映射明确；后续涉及 CODEOWNERS 目录的修改按下一节和 ADR 0002 已确认范围实施。

1. 独立管理宿主承载同一个 `ServiceManagementService`，业务进程停止、启动失败和数据库不可用时仍可查询本机管理状态。明确管理宿主与业务宿主的配置、生命周期及故障边界。
2. 基础设施实现 Windows 服务/进程控制，使用受控程序或服务标识，不开放任意命令执行。实际运行状态与业务就绪状态分别核对；创建进程成功不能直接表示可接收考试业务。启动超时、进程退出、停止失败均返回明确状态。
3. 配置持久保存成功后才发布新版本；并发写、取消及写入失败保留上一个完整版本。宿主重启读取原配置，损坏配置阻止业务启动，不自动生成默认配置覆盖原件。密码、私钥和连接秘密使用受保护材料，不进入普通配置响应或日志。
4. 启动检查先覆盖目录读写/空间、数据库连接/迁移/角色/代次、证书私钥及已发布资源；对事件与定时恢复使用 A 提供的核心入口。缺公共约定先协调，不将空检查集合当作完整自检。
5. GUI 与 CLI 共用管理模块和认证规则，F04 后续接现有考务外壳。首批不修改 E 的 CLI 命令注册，也不修改 C/B 的界面和会话实现。

首批输入为受控部署配置、已认证操作身份、既有配置版本及启停请求。输出包括实际宿主状态、自检项目、持久配置版本、稳定错误与关联标识。管理传输、请求及响应语义和鉴权边界现按 [ADR 0002](decisions/0002-local-management-access.md) 固定，具体类型与接线由 F 在实现 PR 中补齐。

## 改动范围与 A 确认

[AGENTS.md](../AGENTS.md)要求修改公共契约、Server 和相应 Application 目录前先由 A 确认。A 在 [2026-09-24 的 Issue 回复](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/7#issuecomment-5808991251)中同意 F01 的应用服务及 Server 接线范围；2026-09-26 通过 ADR 0002 补充管理传输、停机授权、公共契约与共享鉴权范围。变更仍须通过 PR，由 A 审查和合并。

| 已确认项 | 改动范围与边界 |
| --- | --- |
| 应用服务接线 | `src/Exam.Application/ServiceManagement`：持久配置接口、实际宿主状态协调、启停失败和重启恢复语义 |
| 独立宿主与业务入口 | 新管理宿主及 `src/Exam.Server` 的必要接线，采用本机 HTTPS 独立端口，管理生命周期独立，区分进程状态与业务就绪 |
| 管理授权边界 | 提取共用令牌及操作者校验，管理宿主独立加载受保护配置；本机诊断不依赖活动节点，业务门禁及危险操作检查保留 |
| 公共契约与共享接口 | 允许在 `src/Exam.Contracts/ServiceManagement` 及必要的通用管理契约中补充请求、结果和代次可未知的管理封套；同步合成样例和文档，共用鉴权接口遵守现有依赖方向 |
| F02 接口 | 复用稳定身份与代次、节点角色检查、独立管理入口；本批不实现提升或主备状态迁移 |

2026-09-24 首批只在上述 CODEOWNERS 目录中修改了 `src/Exam.Application/ServiceManagement`。后续按已确认范围完成独立宿主、共享鉴权和公共管理契约，由 A 审查；本次授权不包含新增数据库迁移。若后续需要迁移，按[迁移约定](database-migrations.md)另行明确权限、一致性和恢复边界。

## 模块验证清单

| 范围 | 必须验证的行为 | 环境 |
| --- | --- | --- |
| 状态与并发 | 重复启停、并发配置/启动、启动中取消、停止失败、子进程异常退出；不得虚报运行或停止成功 | 单元测试及真实进程测试 |
| 配置文件 | 重建管理对象仍读到原版本；冲突、只读目录、损坏文件、替换中断不覆盖可靠配置；秘密不出现在输出 | 隔离临时目录、真实文件 |
| 管理入口 | 业务未运行/启动失败/数据库不可用时仍可查询；无权限、伪造操作者、错误版本被拒绝 | 管理宿主接口测试 |
| 启动自检 | 缺迁移、错误角色/代次、资源缺失、证书私钥不可用、写入失败均阻止业务就绪 | 单元测试、真实文件/数据库与 Windows |
| 隔离恢复后续批次 | 加载隔离 PostgreSQL，核对编号、答卷、原回执、题包、事件、定时，继续收卷；保留原库和故障原件 | `Category=Postgres`、隔离 Windows 部署 |

代码交付前实际执行仓库三条检查：

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release
dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release
```

外部依赖按既有 Category 分类；当前 Windows CI 为 `Category!=Postgres&Category!=Latex`，PostgreSQL 独立流水线为 `Category=Postgres`。Release 要求 0 警告、0 错误。新增包须精确版本并提交锁文件。Windows 服务、断电及恢复完整流程需在对应隔离环境执行，不能以 macOS 检查替代。T01/T02 整体验收仍由 A 组织。

## 本轮记录与提交要求

- 本轮新增本说明、独立配置文件适配、本机存储启动探针及其组合器，覆盖重新打开、并发冲突、损坏文件、无效更新、缺失目录、空间不足和空子探针；未改权限、公共契约或数据库。
- `git fetch origin` 成功；分支从 `4749b4f` 快进至 `origin/main` 的 `b4d4893`。该提交调整成员账号与交付分工：E04负责候选包及测试期间重建支持，T02通过后由A负责T03最终交付；F01主责、范围及开发顺序未变。`gh` 不在 PATH，Issue 匿名读取 404；Issue 最新讨论依据 F 转述的 A 确认。
- macOS 15.7.4、Arm64；使用 `/usr/local/share/dotnet/dotnet` 10.0.401。目标单元项目 `restore --locked-mode` 成功；`TMPDIR=/private/tmp/` 下运行 `dotnet test tests/Unit/Exam.UnitTests/Exam.UnitTests.csproj --no-restore --configuration Release --filter 'FullyQualifiedName~ServiceManagement'`，31/31 通过，构建 0 警告、0 错误；同项目 `--no-build --filter 'Category!=Postgres&Category!=Latex'`，735/735 通过。
- 默认 macOS 临时路径经过 `/var` 符号链接，安全路径检查会拒绝它；模块测试使用真实目录 `/private/tmp/`。首次测试发现并已修正两条 xUnit 分析器警告，最终重跑无警告。
- 完整解决方案 `dotnet restore Huaizhou.ExamSystem.sln --locked-mode` 在 macOS 上返回 `NETSDK1100`，因为仓库包含 WPF/Windows 目标项目；完整三项检查及 Windows 服务、故障恢复验证须在 Windows 环境补跑，不记为已通过。
- 本轮已做差异、相对链接和文档内容核对；这是静态校验，不是业务测试。
- 后续代码提交按 `F01: 中文行为变化` 命名，修正以“修正…”开头；PR 按[模板](../.github/pull_request_template.md)关联 #7，列明输入输出、运行方法、实测结果、未测原因及回退限制，由 A 审查合并。
- 回退原则：保留原部署与数据；新管理宿主故障时业务写入保持关闭，先核对实际进程；配置失败保留旧版本；不自动恢复旧数据库、不覆盖较新答卷、不自动迁移或升级。

下一步：继续完成独立管理宿主、受控 Windows 服务启停、启动自检和恢复接线；在 Windows 上补跑完整三项检查与真实服务/恢复模块验证。通过 PR 交 A 审查，不自行合并主分支。

## Windows 后续实施与实测（2026-09-24）

从远端 `codex/f01-service-management` 的 `32f97fde` 核对并检出，随后获取最新 `origin/main` (`5d2233c`)。为避免强制推送既有共享分支，从最新 main 新建 `codex/f01-service-management-followup` 并拣入原 F01 提交；远端原分支和 main 均未修改。

本批增加受控 Windows 服务控制适配器：服务名称在部署时固定并校验，使用 Windows Service Control Manager 查询、启动和停止，仅在观察到目标服务状态后返回；超时、查询失败及取消不报告启停成功。`ServiceManagementService` 同样在宿主操作返回后复核实际状态，恢复后的启动检查若无结果则拒绝通过。新增针对未达目标状态、取消、空恢复检查和服务状态轮询的单元测试。

截至该轮，适配器仅实现 **Windows 服务进程状态**，尚未接入独立管理宿主，也不代表 `/health/ready` 已通过。管理传输与停机授权后来已由 2026-09-26 的 ADR 0002 确认；数据库、证书、资源、事件及定时检查组合和隔离恢复后的业务核对仍需完成接线与审查。该轮不将 F01 标记完成。

Windows 10.0.26200、x64，SDK 10.0.401：锁定模式还原已在正常用户 PowerShell 完成，Release 构建 0 警告、0 错误；F01 专项 `FullyQualifiedName~ServiceManagement` 40/40 通过。正常用户 PowerShell 把 `TEMP` 指向隔离工作目录后，报告标准 Windows 分类测试 `Category!=Postgres&Category!=Latex` 全部通过；该次仅有用户结果反馈，未保存原始测试报告。另用隔离程序检查受控启停及 Windows SCM 不存在服务的错误路径。外部依赖验证按仓库测试计划继续；本批没有安装和操作真实考试业务服务。

真实 Windows 服务启停、管理宿主停机可用性及隔离恢复后的继续收卷仍未验收。仓库不包含本机日志、配置秘密或运行数据。

## PR #54 审查修正记录

本 PR 已接续 main 合并 #53 后的共享接口。A 的本轮审查补充实际宿主状态门禁、持久配置启动核对、取消与探针校验、备份/恢复请求内容绑定、配置文件完整性及 SQLite/WPF CI 回归修正。实跑命令、结果、复现过程和原生 SCM 未验收范围见 [PR #54 审查记录](reviews/pr54-f01-ci-review.md)。这是阶段代码交付，#7 的独立管理宿主、业务就绪和恢复运行闭环仍保持未完成，不以本 PR 合并代替验收。

## F01 集中实现接续（2026-09-27）

用户确认关闭 #57/#58/#59，并将证书检查、业务就绪核对、备份清单校验接续到 `codex/f01-local-management`，基线为已合并 #60 的 `origin/main@0c4b8df`。旧分支保留，已合并分支不再处理；本轮不提 PR，先交 Windows 继续实现与验证，再集中提一个 PR 由 A 审查。

新增独立管理进程、共用操作者验证、本机持久操作记录、离线诊断与操作查询。配置损坏或业务数据库缺失不阻止认证、状态与停止；未核实代次保持为空。恢复明确只报告隔离暂存，完整恢复核对链未接齐时阻止业务启动，不将本轮记为 F01 完成。部署模板及合成接口响应见 [服务管理样例说明](../samples/service-management/README.md)，实际模块验证与未完成项在实现 PR 中记录。

2026-09-27 追加：接通真实只读 PostgreSQL 启动恢复核对、HTTP/SignalR 及后台业务门禁，替换固定失败占位；增加停机、保存配置与启动的中断后核对，不重复副作用。macOS 实跑通用单元 1017 项通过、非 TeX 集成回归 134 项及最后新增的生产注册检查 1 项通过。隔离数据库切换、继续收卷与原生 Windows 验证仍未完成，完整事件语义重放不在本轮检查范围内；详细证据及失败修正记录见集中实现交接。

2026-09-27 后续：完成解密后题包共享清单与当前发布结构核对、备份/暂存的只读中断证据恢复；修复真实物理恢复发现的 PostgreSQL 空目录复制遗漏。macOS 最终通用单元 1021/1021、非 TeX 集成 148/148 通过，无跳过；其中实跑物理备份、校验、独立副本启动、原回执 HTTP 查询、剩余交卷重试及持久收卷完成。生产恢复连接切换仍未接线，不能把测试副本演练当作产品入口或现场验收完成。Windows 待测与具体恢复语义缺口见集中实现交接。
