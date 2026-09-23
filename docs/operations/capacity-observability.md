# 连接容量、入口保护与运行观测

这里交付的是容量约束和观测手段，不是 300 人性能验收结论。CI 中 `Maximum Pool Size=16` 只用于避免隔离测试把数据库连接耗尽；未配置连接池时的 32 也只是开发环境的有界回退值。任何数值都不能直接视为生产推荐。

## 生产连接预算

生产服务要求显式提供以下配置，否则拒绝启动：

| 配置 | 含义 |
| --- | --- |
| `Database:MaximumPoolSize` | 每个服务进程的业务连接池上限 |
| `Database:Capacity:MaximumServerProcesses` | 可能同时连接同一数据库的服务进程最大数，包含部署重叠、维护实例和恢复期间的额外进程 |
| `Database:Capacity:PostgresMaxConnections` | 实际 PostgreSQL 的 `max_connections` |
| `Database:Capacity:PostgresReservedConnections` | `superuser_reserved_connections + reserved_connections` 的实际总数 |
| `Database:Capacity:OtherConnections` | 其他应用、备份工具、CLI、自检或独立数据源需要的连接预算 |
| `Database:Capacity:OperationsReserveConnections` | 在 PostgreSQL 保留槽之外额外留给运维的余量，至少 3 |

检查公式为：

```text
每进程连接池上限 × 同库服务进程最大数
  + 其他连接预算 + 额外运维余量 + PostgreSQL 保留连接
  <= PostgreSQL max_connections
```

仓库的 [配置样例](../../deploy/server-capacity.example.json) 演示 `32×2+5+10+3=82≤100`。它不自动加载，必须按实际部署填写受保护的配置；这个算式只说明不会在声明的同时运行规模下用完全部连接，不证明 CPU、磁盘、同步复制、锁竞争或网络已经达标。每个数据库主机分别计算预算。服务使用一个共享业务数据源；若增加其他池或多主机数据源，其全部可能连接须另计，不能仍声称只有一个池的预算。

生产启动会读取数据库实际连接上限和保留槽数，与声明严格比较；不匹配时拒绝启动。`/health/ready` 也会重新检查。普通数据库连接配置不够时不会自动调高数据库上限、切换为无池连接或降级到内存。

`Database:MinimumPoolSize` 默认为 0。`Database:ConnectionTimeoutSeconds` 默认 15，限制打开连接和等待连接池的时间；`Database:CommandTimeoutSeconds` 仍单独控制数据库命令。若连接字符串和显式配置同时提供池上限/下限，两处必须一致。Npgsql 的参数定义见 [官方连接字符串文档](https://www.npgsql.org/doc/connection-string-parameters.html)。

## 有界入口与重试

入口在身份验证和活动节点数据库检查之前分为三组，各自持有有限的许可和 FIFO 等待队列。尚未通过入口的请求不执行身份查询或业务写入。

| 组别 | 主要请求 |
| --- | --- |
| `critical` | 最终交卷、原回执查询、指令/确认、解锁、设备裁决及就绪检查 |
| `api` | 答题进度、普通管理与查询、校时、SignalR 协商 |
| `resource` | 加密题包下载、备份与恢复长任务 |

三个并发上限分别由 `RequestCapacity:CriticalPermits`、`ApiPermits`、`ResourcePermits` 配置。省略时按当前连接池上限计算开发基线；生产应在真实同步复制和混合负载下确定。普通 API 与资源许可之和必须小于业务池上限，避免这两组单独占用全部连接空间。关键流量和后台任务仍共享 Npgsql 池，此机制不是严格的数据库优先级调度。

`RequestCapacity:QueueLimit` 是每组的排队人数，默认 300；`QueueTimeoutMilliseconds` 默认 5000。排队满或等待超时返回 HTTP 503、`Retry-After: 1` 和完整服务器身份封套，错误标为可重试，说明本次尚未进入业务处理。客户端必须保留原 `requestId` / `submissionId` 及原载荷，退避重试；压测必须把排队、503、重试及最终回执计入端到端耗时。不能剔除被拒请求后只统计成功请求的百分位。五秒排队上限是保护界限，不是满足 P95 指标的证明。

已升级的 SignalR 连接不长期持有 HTTP 许可，继续使用原有 `Realtime:MaxConnections` 和 `MaxConcurrentDispatches` 上限。Kestrel 的普通/升级连接各自受到 `RequestCapacity:TransportConnectionLimit` 限制，默认 2048；HTTP/2 复用的请求仍逐个经过入口。该保护不是防火墙、认证或抵抗分布式攻击的替代品。请求路径不形成动态分区，不会因大量伪造考生或题包 ID 无界创建队列。

`Database:MaxConcurrentWrites` 配置答题进度/最终提交共享的写入槽，默认 32。写槽内部现在为最终提交和进度提供分别有界的等待队列，释放许可时优先处理已经等待的最终提交，不中断已在执行的进度事务。这是应用层的优先安排，不是 PostgreSQL 连接池或数据库锁的严格优先级；排队、过载与重试仍须一起观察。数据库事务或连接超时可能表示业务结果未明，必须通过原业务标识重试核对，不能套用入口拒绝的“尚未进入业务”结论。

## 现场可读取的观测

服务提供 `GET /api/v1/diagnostics/runtime`。必须使用当前考务操作身份；设备凭据、未认证请求和仅猜测 URL 都不能读取。响应带 `serverId/nodeId/serverEpoch`，但不含连接字符串、请求路径、查询参数、考生、凭据、题目、答案或 SQL 文本。

快照包含：

- 进程当前/峰值工作集、最近 GC 堆大小、线程池线程数与待处理项、累计 CPU 时间。
- 每个固定请求组的当前/峰值并发、当前排队数、累计完成/拒绝数、累计及最大服务器耗时。耗时包含入口排队；计数为进程启动以来的累计值，并发采样不是数据库事务快照。最近一次 GC 的堆大小不等于操作系统的完整内存占用。
- 生效的连接预算、HTTP 许可/队列上限及提交写槽配置。
- 当前数据库的客户端连接数、活动连接数和锁等待连接数，仅查询 `pg_stat_activity` 的聚合数值。采样包含本次诊断连接；无权查看的会话状态另计 `stateNotVisibleConnections`，不能把不可见的锁等待误认为不存在。

数据库观测最多等两秒。失败时 `available=false`，数值为 null，明确提示样本缺失，不返回虚假的零连接、零等待。快照不是健康状态证明；实际启动/就绪检查仍走容量探针及既有活动节点检查。

服务也通过 .NET `System.Diagnostics.Metrics` 发布 `Huaizhou.Exam.Server` Meter：`exam.http.duration`、`exam.http.queue.duration`、`exam.http.active`、`exam.http.queued`、`exam.http.rejected`，以及进程工作集、GC 堆、线程池待处理量。标签只取固定的 workload/outcome/reason，不使用用户输入。持续分位数据可由授权管理员使用已有监测工具采集；快照中的累计值不能自行还原 P95/P99。

可以在目标服务器用已安装的 .NET 诊断工具采集：

```powershell
dotnet-counters monitor --process-id <服务PID> --counters Huaizhou.Exam.Server,Npgsql,System.Runtime,Microsoft.AspNetCore.Hosting,Microsoft.AspNetCore.Server.Kestrel
```

Npgsql 10 的 Meter 提供连接池使用量和数据库操作耗时等指标；业务数据源的指标名固定为 `Exam.BusinessDatabase`，不使用默认连接字符串作为池标签。见 [Npgsql 指标说明](https://www.npgsql.org/doc/diagnostics/metrics.html) 和 [ASP.NET Core HTTP 指标](https://learn.microsoft.com/en-us/aspnet/core/metrics/http?view=aspnetcore-10.0)。这里不启用包含 SQL、头部或载荷的诊断日志。

## 验证边界

定向自动测试覆盖预算乘法、运维余量、显式生产配置、真实 PostgreSQL 参数匹配、旧 CI 池值兼容、独立队列、排队上限、超时、取消及身份封套。它们验证保护行为，不替代性能验收。

正式结论仍须按执行指南记录硬件、网络、最大实际题包、主备同步状态、独立负载机，以及 300 会话的完整业务、集中下载/交卷、监测与指令混合负载。分别报告每类请求的 P95/P99、重试与最终失败、CPU/内存、数据库连接与锁等待、恢复后的数据核对结果。本机回环、合成小包、单机提交与短时间测试必须标明范围，不得作为双机或整场考试已验收的结论。
