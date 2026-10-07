# 主备管理 正式主备管理契约与持久协调


## HTTP 与 CLI

复用 本机管理 HTTPS 回环监听、考务账号/加密离线缓存及独立管理会话。请求绑定逻辑服务器、本机节点和已认证操作者；权限继续由管理后端执行。

| 方法与路径（前缀 `/management/v1`） | 输入 | 输出 |
| --- | --- | --- |
| `GET /high-availability/status` | 身份头和会话 | `HighAvailabilityStatus` |
| `POST /high-availability/takeover` | `HighAvailabilityRequest`，本机备用目标与旧主隔离证据 | `ManagementOperation` |
| `POST /high-availability/emergency` | 同类型，当前本机主节点及原因 | 同上 |
| `POST /high-availability/rebuild` | 同类型，本机隔离旧节点及破坏性重建确认 | 同上 |
| `POST /high-availability/reprotect` | 同类型，当前主节点上的重建备用目标 | 同上 |
| `GET /operations/{requestId}` | 原号、同身份和同节点 | 原持久操作及实际核对结果 |

请求包含 `RequestId, ServerId, NodeId, ActorId, ExpectedEpoch, ExpectedVersion, TargetNodeId, Reason, Isolation, ConfirmDestructiveRebuild`。隔离证据 `ConfirmedBy` 须等于会话操作者，`IsolatedNodeId` 须是旧活动节点，原因须与请求一致，断电或业务网络隔离至少明确一项。原因非空且至多 500 字符，代次/版本取当前已核实正值。

状态包含 `ServerId, NodeId, State, VerifiedAt, SynchronousProtectionVerified, Checks`。State 沿用 `ServerDeploymentState`，节点包含角色、可达、数据库恢复态、差距、材料/证书及时间。不能实际读取时状态或核实时间为空，未知差距不当作 0；副本快照须有双方最新独立确认才能用于接管。

枚举增加 `Takeover, Emergency, Rebuild, Reprotect`；操作附带实际主备结果。内部 `FailoverExecutionPlan` 只存受保护操作日志，HTTP 响应移除，不含连接串或证书。过时代次/版本为 409，身份不符为 403，未知依赖为 503，仍按原号核对。

CLI 沿用 `management` 命令组、共享客户端和部署会话，增加 `ha-status, takeover, emergency, rebuild, reprotect`；写操作通过既有 `--input` JSON 文件输入。未知状态或未证实同步保护的状态读取退出码 6，代次/版本冲突退出码 4。没有默认凭据、远程管理或证书绕过。

## 持久协调与外部副作用

`DurableFailoverCoordinator` 复用 本机管理 文件操作存储及跨进程独占租约。同号绑定节点、操作者、类型和摘要，内容变化拒绝；未核对操作阻止新冲突变更。原内存 `FailoverCoordinator` 只供离线模型/测试，正式宿主不调用。

先核对实际状态、10 秒新鲜度、代次/版本、角色和前置条件，将完整意图持久写入日志后才允许外部副作用。中断保存待核对；重启查询只核对实际 SQL、时间线、选择、完成标记和确认，不再次提升或破坏性重建。没有持久意图的中断可确认未执行副作用，要求重新读状态和确认。

平台核对 PostgreSQL 集群、数据库、时间线和角色，以原代次/版本 CAS 写状态。控制状态切换采用本地持久提交，避免已隔离同步节点阻塞恢复；业务写入原有双侧持久确认不因此放宽。

| 操作 | 条件及实际行为 |
| --- | --- |
| 接管 | 明确旧主隔离，本机最新备用的 WAL 接收/重放边界、资源、材料和证书通过；停止本机业务、实际 `pg_promote`、代次递增、旧角色隔离、进入单机应急、持久选择新代次 |
| 应急 | 仅当前本机活动主节点，明确原因；先停止业务，再持久保存模式、实际复制配置和部署选择。完成后由考务自检并启动，避免运行中仍使用旧确认目录；健康失败不自动触发 |
| 重建 | 本机隔离旧节点或应急模式下的本机备用节点，明确确认；新主基础备份、实际 `pg_verifybackup`、资源摘要、停旧库、隔离保留旧目录、启用新备库并核实集群/角色/状态 |
| 恢复同步 | 当前主节点核对已重建备用，或应急后恢复连接的原备用；实际流复制、同步配置、完整材料和证书及当前代次/版本双方最新独立确认通过后才完成 |

重建槽已存在但证据不足时保持待核对，不清槽重来，不用新请求绕过。旧库和旧资源隔离副本保留；清理属于现场运维决策。接管后通过 本机管理 自检/启动业务，使宿主读取新选择；重建仅启动非活动节点确认监听，业务写入仍受门禁保护。

## 部署配置

| 受保护键 | 约束及用途 |
| --- | --- |
| `HighAvailability:Nodes:<node>:ConnectionString` | 两个实际不同数据库监听端点，适当受控账号；同端点不得冒充双节点 |
| 同前缀 `ResourcesDirectory` | 每节点版本资源，管理可按受控部署核对双方摘要 |
| 同前缀 `CertificateThumbprint` | 查找节点证书，实际私钥挑战与业务材料解锁 |
| 同前缀 `ConfirmationDirectory` | 每侧独立确认根；旧主离线时最新原主记录仍须受保护可访问，否则阻止接管 |
| 同前缀 `BusinessHttpsUrl` | 正常校验的业务确认 HTTPS 根，不含用户信息、路径或查询 |
| `HighAvailability:SelectionPath` | 预建受保护目录内选择，管理可写、业务可读 |
| `HighAvailability:RebuildRoot` | 独立、非根、无链接暂存目录 |
| `Backup:PostgresBinDirectory` | 沿用 本机管理 的匹配版本工具绝对目录 |
| `HighAvailability:PostgresDataDirectory` | 固定本机数据库绝对数据目录 |
| `HighAvailability:PostgresConfigurationPath` | 位于数据目录外的独立受保护配置 |
| `HighAvailability:WalArchiveDirectory` | 预建受保护的本机连续归档目录，与数据/资源/重建/工具目录互不包含 |
| `HighAvailability:WalArchiveScriptsDirectory` | 预部署 WAL 脚本与模块目录，部署账户保护；不能来自备份或重建树 |
| `HighAvailability:PostgresServiceName` | 固定本机服务，实际 SCM 启动命令须匹配受控命令 |
| `Confirmation:PeerToken` | 既有节点间保护凭据，仅宿主使用，不交 GUI |
| `Management:BusinessServiceName` | 既有固定本机业务服务 |

键读取以 `Exam.Management/Program.cs` 为准。状态表与初始双节点由既有 主备管理 初始化；缺表时不自动以临时内存替代。

`BuildPostgresServiceCommand` 生成受控 SCM 命令：固定工具、服务、数据、独立配置及本机端口，固定开启连续归档、使用已核对的归档工具与目录，并禁用外部恢复命令/预加载选项。缺少归档部署绑定时阻断，不能静默关闭归档。重建前读取 SCM 实际命令严格核对。暂存、数据和资源互相独立；链接树、外部表空间、不符服务绑定阻止启用。部署维护 部署须满足此绑定，GUI 不编辑服务命令。

数据库监听地址由独立受保护的 PostgreSQL 配置决定，须允许对端主机按既定网络与认证规则连接；重建命令不强制改为仅回环监听。业务宿主启动和持续运行的管理宿主共用部署选择映射，连接、时间线查询、备用连接、证书、资源和确认目录随实际选择一致更新。每节点可配置 `TimelineConnectionString`；未配置时使用该节点数据库连接，不继承旧主的时间线连接。

接管只读取已核实的本机副本资源及独立持久确认，不依赖已关机原主的资源目录。持久意图写入后发生领域校验或参数异常，也必须保留“待核对”并阻止新请求绕过；原请求查询可在依赖恢复后完成核对，不能用终态失败跳过实际副作用。

持久选择只允许同身份且代次/版本不回退，同版本不替换意图。确认目录绑定 `<本机确认根>/epochs/<代次>/<本机节点>`，旧确认保留。业务启动应用选择后的连接、资源、证书、代次和确认身份，实际数据库及原门禁继续核对。单机应急只有匹配选择和实际 主备管理 审计记录才放行明确应急路径，配置复选框没有此权限。

接管中断后的原请求核对，不以较大的时间线编号单独证明提升正确。登记新主或补齐部署选择前，锁定原代次的双侧独立确认记录，核实集群、数据库、版本及本机资源摘要，实际读取提升后的 WAL 持久边界，并通过 PostgreSQL 复制协议核对后继时间线及分叉点覆盖原确认。缺头、头不一致、资源变化、历史分叉或证据不可读时保留待核对，不再次提升、不递增代次、不写新选择；证据恢复后查询原请求可完成既有提升的登记。核对也适用于状态已登记但最终操作记录尚未落盘的中断。

## 验证和恢复边界

单元检查覆盖并发、同号冲突、跨进程租约、意图先落盘、效果后丢响应、完成记录写入失败、重启核对、版本/隔离/材料拒绝及同步保护证据。正式 API 覆盖四类操作、会话/操作者、内部意图屏蔽和 CLI/HTTP 共用路径；启动检查覆盖持久映射及错误目录、身份、相同端点。

原生演练使用独立临时 PostgreSQL：实际流复制、停旧库、提升、原请求重放、新代次单机确认、基础备份、文件校验、保留旧库重建、追平及恢复双方确认。Windows SCM、证书装载、业务监听托管采用明确合成适配；两台机器、实际 TLS/隔离、断电、RTO、现场服务和容量归 多机验收。模块演练或 PR 合并不能替代该验收。

回退以实际角色和最新持久状态为准，禁止自动切回、重新启用隔离旧主或回灌旧库。人工应急、跨节点最新材料可达性、受控服务绑定和中断保守阻断由 A 审计。
