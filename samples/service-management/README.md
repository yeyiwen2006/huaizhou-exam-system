# F01 本机管理合成响应与部署模板

这些 JSON 均人工构造，仅用于管理契约说明，不包含真实试题、账号、配置或恢复数据。

- `status-success.json`：业务停止但管理状态可读；进程状态与业务就绪分开，当前代次未核实。
- `request-conflict.json`：同 requestId 的节点、操作者或内容不同，HTTP 409。
- `restore-staged.json`：隔离暂存完成，恢复核对与业务就绪均未完成；不能作为恢复成功证据。
- `restore-recovery-verified.json`：恢复目标及领域检查通过，业务尚未开放，仍为 Staged；CLI 不报告操作完成。
- `restore-activated.json`：部署引用切换后业务 HTTPS 就绪通过，三个恢复结果均为 true，才报告 Completed。
- `restore-rolled-back.json`：启动失败后确认业务停止且无新增业务状态，回退原连接引用；保持 Staged，不自动启动原库。
- `checks-blocked.json`：自检操作完成，但资源核对失败并阻断业务启动；Completed 不表示所有检查通过。
- `stop-reconciled.json`：原停机结果落盘中断后，从实际已停止状态确认原请求，未再次执行 SCM 停机。
- `restore-needs-verification.json`：原恢复结果中断且暂存证据缺失或损坏，继续等待核对，不重复恢复或开放业务。
- `logs-summary.json`：受保护操作记录的合成摘要，不含原请求内容或本机路径。
- `runtime-logs-summary.json`：业务运行故障的合成元数据，不含异常正文、参数或个人数据。

正式 CLI 使用 `exam-cli management status --management-url https://localhost:9443 --server-id synthetic-server --node-id synthetic-node`；
`configuration`、`logs`、`runtime-logs` 和 `operation --request-id ID` 为只读命令。`start`、`stop`、`checks`、`configure`、`backup`、`restore`
须附 `--input FILE|-`，请求 JSON 内携带稳定 requestId，超时后用原标识查询，不重新生成请求。
上述地址和身份也可由 `EXAM_MANAGEMENT_URL`、`EXAM_SERVER_ID`、`EXAM_NODE_ID` 提供。使用前先执行
`exam-cli management login` 以考务账号登录，会话按当前 Windows 用户加密保存在本机，`management logout` 删除。CLI 保持系统证书验证，不支持管理 HTTP 降级。

`GET /management/v1/service/logs?limit=1..100` 仅返回受保护本机操作记录的审计摘要：请求标识、类型、阶段、
受理/更新时间和错误码。它不读取任意文件，也不返回原请求 JSON、内容摘要、恢复路径或业务日志正文；
部署账户须保护 `Management:OperationDirectory`，缺失或损坏记录导致查询失败，不以不完整列表冒充成功。
`GET /management/v1/service/runtime-logs?limit=1..100` 读取业务宿主在受保护固定路径写入的结构化运行事件。
业务宿主须配置 `Management:ProtectedBusinessLogPath`，管理宿主须配置相同路径；目录由部署人员预建并只授权
对应服务账户。日志仅写警告及以上级别的时间、来源、组件名和事件编号，绝不调用消息格式化器，也不写异常、
参数或请求正文。达到 8 MiB 时轮换为固定 `.previous` 文件；读取最多返回 100 条，拒绝符号链接和损坏记录。
未配置或不可读时返回依赖不可用，不把空列表当成功；管理宿主的状态和停止入口仍可使用。
当前 `runtime-logs` 只展示业务来源。真实 Windows 服务账户和目录 ACL 须在部署环境核对。

写请求必须附 `Authorization: Bearer …`、`X-Exam-Server-Id` 与 `X-Exam-Node-Id`；后两个值是预期目标节点，不是操作者身份。启停、自检请求体为 `{ "requestId": "synthetic-stop", "serverId": "synthetic-server", "nodeId": "synthetic-node" }`。保存配置另外附 `expectedVersion` 与完整 `configuration`；备份/恢复请求体复用既有 `BackupRequest`/`RestoreRequest`。

所有输出沿用 `JsonDefaults` 的大小写和字符串枚举规则。普通状态的 `readiness=unknown` 表示未执行当前业务就绪核对；操作结果中的状态为该次操作的观察快照，不能当作当前状态。客户端须核对管理封套的 serverId/nodeId，不能将未核实代次用于业务写入或接管。

操作记录包含可选 `requestJson`（规范化原请求）、`recoveryWorkingDirectory`（已绑定的非秘密恢复目标路径）及 `recoveryActivation`（选择待执行、已选择、业务启动中、业务就绪、回退待执行、已回退）。旧记录缺省仍可读取；目标在副本准备前持久保存，配置变化不能把原恢复重定向到另一工作槽。每项启用副作用前写入 NeedsVerification 意图，暂存后的故障保留阶段及目标并继续阻止另一个请求启动。人工响应中的摘要只是合成占位，不能直接写入操作目录；客户端仍须校验 requestId、目标及预期版本。

备份中断后可凭已绑定清单、文件摘要与 PostgreSQL 校验恢复为 Completed；恢复中断后凭暂存证据恢复为 Staged，输出如 `restore-staged.json`。配置工作槽后，同一恢复 POST 可显式续行准备工作副本及核对恢复数据库。未启用部署选择时核对通过仍保持 Staged 和 businessReady=false。启用后的中断查询只观察原请求的部署选择、绑定工作副本、实际目标、领域检查和 HTTPS 就绪；不复制、切换或重启。不能核实时保留 NeedsVerification。

`appsettings.management.example.json` 和 `service-configuration.example.json` 是人工构造的 **Windows 部署模板**，不是可直接投入使用的部署配置或操作记录。主、备机分别设置实际节点身份、独立服务名、端口和仓库外受控路径，逻辑服务器身份须与业务一致；示例路径没有自动创建。管理配置模板在仓库外配置为可执行文件目录的 `appsettings.json`，初始非秘密服务配置在仓库外配置到 `Management:ServiceConfigurationPath`。服务模式按可执行文件目录加载配置；不要从 system32 寻找文件。

业务宿主注册为原生 Windows 服务时，其受保护配置还须设置 `ExamServer:WindowsServiceName` 为实际已注册的业务服务名。业务与管理宿主均在服务模式从自身可执行文件目录读取配置并向 SCM 报告生命周期；部署工具先注册独立服务账户及证书权限，再验证原生启停。普通控制台运行不需要此项。

业务配置接线示例见 `appsettings.business.example.json`。业务和管理宿主须显式配置相同的 `Management:ServiceConfigurationPath`，业务节点身份须与文件一致。业务进程每次启动读取完整文件，并实际使用其中的 `bindAddress`（IP 地址或 `localhost`）和 `httpsPort` 监听 HTTPS；TLS 证书及其密码仍由独立受保护的 `Kestrel:Certificates:Default` 配置提供，示例不含密码或连接串。托管方式拒绝同时设置 `Urls`、`HTTP_PORTS`、`HTTPS_PORTS`、`preferHostingUrls=true` 或 `Kestrel:Endpoints`，避免额外监听或旧端口覆盖文件。未配置该路径的已有业务部署保留原有启动方式，但新管理宿主不能据此确认托管启动成功。

`dataDirectory` 是业务启动时实际检查路径、可用空间及持久写入的存储检查目录；修改它不会重定向 PostgreSQL、搬迁数据库或改动现有资源目录。业务侧空间下限使用自身受保护配置的 `Management:MinimumAvailableBytes`，默认 1 GiB。`highAvailabilityEnabled=true` 要求当前同节点、同代次的权威保护模式为 `SynchronousDualNode`，启动恢复门禁和每次业务就绪查询均执行检查；设为 `false` 仅取消这项附加要求，不会启用应急模式、提升节点或绕过既有写入权限。主备状态仍由已授权的主备流程管理。

业务 `/health/ready` 在全部现有检查通过后返回实际加载的非秘密 `serviceConfiguration` 快照。管理端按本次期望配置逐字段、逐版本核对；旧宿主缺少该证据或字段不一致时不报告启动成功。探测保留 `Management:BusinessReadyEndpoint` 的 HTTPS 证书主机名及 `/health/ready` 路径，端口使用持久配置中的 `httpsPort`。保存配置只表示文件已持久化，启动成功还必须取得这份实际应用证据；修改配置后需按既有停机、保存和启动流程处理。

模板刻意不包含数据库连接串。按既有受保护部署配置/服务账户配置提供，不放命令行，不把填好的文件提交到仓库。服务管理凭考务账号登录（不再配置共享令牌）；数据库停止时按 `Management:OperatorCachePath`（默认在操作目录下的 `operators.cache`）中的本机加密缓存核对，该文件须与操作目录一样受保护。证书指纹占位必须替换，证书/私钥必须实际部署并受保护，不能通过关闭 TLS 校验使用模板。缺少管理 TLS 证书时不会启动管理 HTTPS；未登录或登录失效时返回 401；缺失业务数据库/节点证书时允许管理认证和状态诊断，但对应操作或自检失败。

服务配置、操作目录及备份/暂存根目录分别保护。操作目录不放其他 JSON、不经过联接点，也不放在备份材料源目录内。模板的 `version=1` 仅为合成首次配置版本，不能用于覆盖现有配置；后续保存必须读取当前版本并携带 expectedVersion。`highAvailabilityEnabled=true` 不代表已完成配对或复制，`ExamServer:ServerEpoch=1` 仅为合成部署比较值，不是当前权威代次。模板不默认启用连接切换。

`Backup:StartupVerificationDirectory` 与 `Backup:StartupVerificationBackupId` 指定启动自检的备份及其固定标识，必须在允许备份根目录内；不能保留占位值或自动选“最新”。先通过管理备份接口创建完整备份，再由部署人员更新受保护配置中的选择并重启管理宿主（当前无动态重载）。缺少选择、摘要损坏、工具失败或标识不符均产生阻止启动的 `backup-integrity` 检查；初始化时仍可认证、查询状态和创建备份。检查不依赖业务数据库、不写恢复目标，但不证明备份新鲜度、所属服务器、WAL 连续覆盖或密钥可实际解锁，完整恢复演练仍需执行。

`Recovery:WorkingRoot` 须为预建受保护目录，固定工作槽为 `slot/database`，不得与备份、暂存或原材料目录交叠。缺失原材料源目录不阻断从完整备份暂存，但新建备份仍要求原材料完整。残缺槽保留并拒绝覆盖；完整槽绑定原 requestId，不用于另一项恢复。不配置工作根时仍只返回隔离暂存。

`ConnectionStrings:ExamRecovery` 由独立受保护配置提供，不出现在模板或请求中。要求回环地址、与本机原库不同的端口及相同数据库名；SQL 核对实际端口和数据目录必须对应工作槽，拒绝备库角色，再运行该目标的迁移、节点/代次及领域恢复检查。目标未启动、配置不符或检查失败时不报告恢复核对通过。宿主不会导入密钥；部署人员须部署匹配的证书和解锁材料。不能启动暂存原件或手工修改操作记录解除保护。

可选 `Recovery:PostgresServiceName` 启用 Windows 恢复实例受控启动，另须配置 `Recovery:OriginalPostgresServiceName` 和 `Recovery:PostgresConfigurationPath`。恢复、原库、业务、管理服务名须四者不同。恢复服务及独立受保护 PostgreSQL 配置在部署时预建；管理操作不注册服务、不更改服务账户或授予权限。未配置该服务名时仍可由部署人员启动绑定工作副本后续行原恢复请求。原生 SCM、服务账户及文件 ACL 必须在 Windows 部署环境验证，macOS 的替身测试不能替代。

恢复服务应为手动启动的独立进程服务，SCM BinaryPath 必须精确等于 `ControlledRecoveryDatabase.BuildServiceCommand(serviceName, postgresBinDirectory, workingRoot, configurationPath, recoveryPort)` 返回的固定命令；由部署工具设置，不能把原库服务改成恢复服务。命令固定 `pg_ctl.exe runservice`、工作槽、服务名、等待参数、外部配置、数据目录、IPv4 回环地址和恢复连接端口，并关闭归档及预加载库；不接受请求提供的程序或启动参数。独立配置须匹配认证规则及恢复账户，位于工作槽外受保护目录，不能引用原库目录或含会修改原文件的外部命令。相关工具语义见 [PostgreSQL pg_ctl](https://www.postgresql.org/docs/18/app-pg-ctl.html)，只读部署核对使用 [QueryServiceConfigW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryserviceconfigw)。

复制前须观察恢复服务已停止；运行中只读取原请求的完整工作标记，不重复制。启动前拒绝 `postmaster.pid`、`standby.signal` 和未绑定的 `recovery.signal`，并拒绝包含有效设置的 `postgresql.auto.conf`（仅空行/注释可接受）；保留这些文件，不自动删信号、清配置或覆盖目标。下述可选归档恢复只允许本模块生成且有完整原请求绑定的空恢复信号。SCM 命令不符、未知服务状态、取消或启动失败保留待核对状态。恢复进程 Running 后仍需实际 SQL 目标及全部恢复检查，不能直接标为 BusinessReady。复制和启动中断后查询不启动实例；显式重放原请求时，已经运行且标记匹配的目标只核对、不再次启动。

可选 `Recovery:SelectionPath` 启用恢复后的业务连接切换，须为仓库外、预建受保护目录中的绝对文件路径。管理和业务两套受保护配置必须设置同一路径、Exam/ExamRecovery 连接及服务器/节点/预期代次，并使用包含本接线的业务版本；文件缺失表示原连接，损坏、身份不符或已选恢复连接缺失时拒绝回退。选择文件只保存原请求身份、工作槽及连接引用，不保存秘密；不要放在操作 JSON 目录或备份材料源中。业务运行时不要手工改选库文件或自动启动原库。

启用前业务须停止，自检及实际恢复目标检查通过，并核对全部 exam 业务表、列定义和序列（排除备份/恢复操作审计表）。权威保护模式必须与持久服务配置一致，活动节点及代次须匹配；本操作不启用应急模式、不提升节点、不改代次。原库无法核实时，必须使用下述双侧最新持久确认及实际备节点证据，任一证据缺失仍保持阻断。双机保护尚未重建时，恢复业务 `/health/ready` 的实际确认继续失败，不把旧备份当作最新状态。

状态比对的两条连接均在只读事务内设置 `row_security=off`。该设置不绕过 RLS、不授予权限；查询会受行级策略影响时 PostgreSQL 报错，核对失败并阻止启用或自动回退。数据库账户必须实际能读取完整业务表和序列；不能通过授予超级用户或 `BYPASSRLS` 来自动修复部署。表所有者也需考虑 `FORCE ROW LEVEL SECURITY`。设置只在诊断事务中有效，不改变业务连接的安全策略。语义见 [PostgreSQL 行级安全](https://www.postgresql.org/docs/18/ddl-rowsecurity.html)。

`pg_verifybackup` 成功、清单摘要相符或归档目录中存在 WAL 文件，均不证明备份已包含故障前最新已确认业务状态。`Invoke-WalArchive.ps1` 只复制 PostgreSQL 交给它的已完成文件，不提供与业务成功回执绑定的恢复位置；`archive_timeout=60s` 也不保证当前 WAL 段已经归档。原库离线恢复还需绑定故障前已确认状态对应的可靠恢复位置，同时核对资源和密钥版本；不能从备份自身、归档最后文件或恢复库自行生成“最新”证据。保留原件与恢复副本。归档行为见 [PostgreSQL 连续归档与恢复](https://www.postgresql.org/docs/18/continuous-archiving.html)。

生产业务现在在 HTTP 成功响应、SignalR 返回/可靠推送及 `/health/ready` 前完成独立持久确认。
两侧业务进程设置 `Confirmation:Directory` 为分别预建的受保护独立目录，不能用备份中的旧日志
覆盖当前日志；主侧设置 `StandbyNodeId`、可信 HTTPS 根地址 `StandbyHttpsUrl`，备侧设置
`PrimaryNodeId` 并使用其真实节点身份和包解锁证书。两侧资源目录通过
`Backup:VersionedResourcesDirectory` 配置，内容不一致、不能持久冲刷或解锁失败时拒绝成功。
双方 `Confirmation:PeerToken`（至少 32 字符）由受保护配置/环境提供，模板不含令牌。
管理宿主读取与本机业务相同的确认目录，并配置备节点地址、身份、令牌和受保护的
`ConnectionStrings:ExamStandby`。要求实际备库仍在回放、未暂停，接收落盘及回放均覆盖
动态确认位置；全量状态和恢复资源相符后才允许继续。本机独立日志和备节点记录必须都可读；
仅有旧备份、待确认头、旧备节点记录或单机应急记录仍保持 `NeedsVerification`。
数据库权限和节点密钥须按部署基线预配；不得用健康布尔值、静态 LSN 或关闭 TLS 代替。
完整调用链和边界见[恢复运行说明](../../docs/backup-and-failover-runbook.md)。

`deploy/postgresql` 的 Windows 归档与恢复命令使用 `powershell.exe` 的进程级 `-ExecutionPolicy Bypass` 运行固定脚本。主库 `archive_command` 调用 `Invoke-WalArchiveCommand.ps1`，由它调用同目录的 `Invoke-WalArchive.ps1`，再次核对源 WAL 与归档目标的 SHA-256，确认后才向 PostgreSQL 返回成功；脚本缺失、复制失败或内容不一致均返回失败，PostgreSQL 保留未归档段等待重试。部署时须将两个归档脚本和恢复脚本放在受保护目录，只允许管理员修改、相应 PostgreSQL 服务账户读取，不把脚本路径或归档目标交给管理请求。该参数不更改机器或用户的执行策略。

可选归档重放须同时提供 `Recovery:WalArchiveDirectory`（仓库外受保护绝对目录）、`WalSystemIdentifier`（十进制集群标识）、`WalTimeline`（当前只接受原始时间线 `1`）和 `WalTargetLsn`（规范大写十六进制，如合成位置 `0/300028`）。四项均未配置时保留原基础备份恢复路径；部分缺失或无效时拒绝归档恢复，但不阻断管理认证和状态。必须配置受控 `PostgresServiceName`，不接受请求传入目录、目标或命令。目标须大于基础备份 WAL 结束位置；这是固定重放位置，**不是最新已确认状态证明**。

该路径支持 PostgreSQL 18 的标准小端64位、8KiB WAL页格式，核对备份清单的集群标识和单一时间线，逐段核对WAL长页头的版本、集群、时间线、位置及段大小。按基础备份起始位置至目标所在段验证连续覆盖，基础备份自带段可复用；不能仅提供最后一段、`.partial` 或跨时间线历史。单次最多1024段、合计4GiB，逐文件有界流式复制和SHA-256核对，不无界扫描归档目录。段布局依据 [PostgreSQL 18 WAL声明](https://github.com/postgres/postgres/blob/REL_18_STABLE/src/include/access/xlog_internal.h)；页头和摘要只验证材料关联，完整记录CRC及重放由PostgreSQL验证。

只向停止的独立工作槽补充选定WAL，不改备份或归档原件；同名不同内容拒绝覆盖。复制前持久保存不可变 `wal-recovery-plan.json`，中断续行必须保持同一目标及文件摘要；完整后创建空 `recovery.signal` 及受保护 `wal-recovery.json`，记录原requestId/操作者/备份/工作槽、固定部署边界、备份清单摘要及各段摘要。边界改变、配置移除、信号/最终记录缺失或落盘中断均保留副本并拒绝猜测；不能手改记录解除保护。运行中的原请求只核对绑定，不重复制或启动。PostgreSQL完成恢复后会移除其实际数据目录中的信号；若实例已停止且阶段无法核实，本模块不自动重建信号重放，应保留副本并按恢复流程核对。

归档模式下SCM BinaryPath须使用 `BuildServiceCommand(..., archiveRecoveryOptions)` 的完整返回值，原五参数形式仅用于基础备份模式。命令固定目标LSN/时间线，禁用继承的复制连接、恢复/清理命令；缺段时只执行固定 `exit 1` 失败命令，不能从其他位置下载或执行任意恢复脚本。已选段在 `pg_wal` 中完成重放，到指定位置结束归档恢复并产生新的PostgreSQL时间线；这不修改考试系统的活动节点、保护模式或代次，也不执行F02备库接管。配置及实际行为见 [PostgreSQL 恢复目标](https://www.postgresql.org/docs/18/runtime-config-wal.html#RUNTIME-CONFIG-WAL-RECOVERY-TARGET)。仍须实际目标/领域检查和原库完整比较，原库不可读或晚于该位置的新状态未恢复时不能开放业务；恢复至固定位置不等于故障前数据完整。

归档模式的恢复核对还从实际隔离数据库读取 `pg_last_wal_replay_lsn()`、恢复目标与固定 `restore_command`，确认已结束恢复且重放位置达到绑定目标；操作记录中断后重新查询业务就绪时也重复这项核对。领域恢复自检与全库状态比对都在只读事务内设置 `row_security=off`：若行级策略会过滤诊断结果则拒绝开放，不能用看似为空的结果通过检查。

归档恢复部署选择会持久记录归档模式。业务宿主必须配置与管理宿主相同的四项受保护 `Recovery:Wal*` 值；每次业务宿主重启都会用实际业务数据库连接复核原请求绑定及已完成的 WAL 重放位置。选择记录表明归档模式而配置缺失、两者不一致或 WAL 记录残留时，业务守门保持关闭。固定重放位置仍不能替代原库最新状态比较。

选择写入后才启动固定业务服务，业务入口继续保留原活动节点门禁，并核对实际恢复数据目录后运行领域恢复检查。只有 HTTPS 就绪通过才返回恢复完成。失败或取消先停止业务，再重新核实原库与恢复库状态；一致才回退连接引用，原库不自动启动。已有新写入、原库不可读、停止或回退无法确认时保留选中的恢复库与 NeedsVerification，禁止覆盖较新答卷。同一已完成 requestId 只返回原结果。

接口边界以 [ADR 0002](../../docs/decisions/0002-local-management-access.md) 为准，恢复原则见已有 [恢复运行说明](../../docs/backup-and-failover-runbook.md)。实际模块验证与未完成项在实现 PR 中记录。
