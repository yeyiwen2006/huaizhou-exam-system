# 考务 CLI 操作与部署排障

这些命令与考务 GUI 使用同一服务器接口、登录身份及业务校验。学生端每次填写连接信息后直接登录，无需 CLI 或考务 GUI 批准。

## 在考务电脑开始使用

考务部署包的 `cli\Exam.Cli.exe` 是统一命令行入口，已携带 .NET 运行时。将整个考务包解压后，本机 Codex 可直接执行这个程序，无须获取服务器源码、安装 .NET SDK 或打开考务界面。以下 PowerShell 示例使用 `C:\Exam\Admin`，应替换为实际解压位置。

```powershell
$cli = 'C:\Exam\Admin\cli\Exam.Cli.exe'
& $cli version
& $cli help
$env:EXAM_SERVER_URL = 'https://exam.example.invalid/'
& $cli health --error-format json --timeout-seconds 15
& $cli login --username '实际考务用户名'
& $cli whoami --error-format json
& $cli exams list --error-format json
```

业务地址须包含 `https://`。CLI 使用 Windows 信任链校验证书，地址中的域名或 IP 须与证书匹配。内部 CA 由部署人员安装到考务电脑的信任库。CLI 不提供跳过证书验证的部署选项。

使用 Radmin VPN 的 `26.*` IPv4 地址时，业务命令直接连接虚拟局域网；访问公网域名仍使用系统代理设置，与考务 GUI 保持一致。

登录时口令通过终端隐藏输入。自动化调用可以通过标准输入传入口令的一行文本，不把口令放进命令参数、命令历史或输出。注册 `register --username 用户名 --name 姓名` 会依次读取口令及确认口令。业务 `logout` 退出当前目标的业务会话。

CLI 登录会话与考务 GUI 分开保存。默认目录是当前 Windows 用户的 `%LOCALAPPDATA%\HuaizhouExam\Cli`，内容由该用户加密保护；`EXAM_CLI_SESSION_DIRECTORY` 可指定独立目录。业务会话按服务地址隔离，管理会话按管理地址、服务器标识和节点标识隔离。Codex 与登录命令须使用同一个 Windows 用户及会话目录。更换服务器或管理节点后，先登录对应目标。

从未区分目标地址的旧版 CLI 升级后，需要重新登录各目标，旧版会话文件不自动用于新连接。

`EXAM_OPERATOR_TOKEN` 可以为当前进程显式提供会话令牌，优先于已保存会话。业务令牌和独立管理令牌用途不同。使用普通登录流程时应清除不再使用的令牌环境变量，避免覆盖本机保存的登录状态。

## 业务停止时连接独立管理服务

`health`、`time` 和考试管理命令访问业务地址。`management` 命令访问独立管理服务，即使业务进程停止，仍可读取状态、日志并执行恢复操作。管理服务本身、管理网络和 HTTPS 证书需要可用；远程 CLI 无法启动一台已经断电的服务器。

CLI 显式接收管理地址和身份，不自动读取 GUI 的连接文件。部署人员提供的 `management-connection.json` 位于考务程序旁，可按下列方式读取其中字段。

```powershell
$connection = Get-Content -LiteralPath 'C:\Exam\Admin\management-connection.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$env:EXAM_MANAGEMENT_URL = $connection.address
$env:EXAM_SERVER_ID = $connection.serverId
$env:EXAM_NODE_ID = $connection.nodeId
& $cli management login
& $cli management status --error-format json
& $cli management ha-local-status --error-format json
& $cli management configuration --error-format json
& $cli management logs --error-format json
& $cli management runtime-logs --error-format json
```

管理登录依次读取用户名、口令，自动化标准输入也按此顺序提供两行。使用原考务账号登录目标管理节点，成功后后续命令读取已保存的管理会话。Windows 管理服务在数据库暂不可用时可使用此前核实并保存的账号缓存；新账号需要在数据库可用时先完成认证。

双节点配置还提供 `peerAddress` 和 `peerNodeId`。检查另一节点时，只替换管理目标并登录该节点，服务器标识沿用 `serverId`。

```powershell
$env:EXAM_MANAGEMENT_URL = $connection.peerAddress
$env:EXAM_NODE_ID = $connection.peerNodeId
& $cli management login
& $cli management ha-local-status --error-format json
& $cli management ha-status --error-format json
```

`ha-local-status` 只检查当前目标节点，适合另一台失联时排查本机数据库、资源与角色。`ha-status` 检查整体主备保护状态。`status` 的 `processStatus` 与 `readiness` 分别表示进程状态和业务就绪状态，自动备份及磁盘信息在 `automaticBackup`。`logs` 返回最近 50 条操作记录，`runtime-logs` 返回最近 50 条限定运行事件，包含时间、来源、组件与事件编号。

管理连接参数也可逐次写在命令后面，例如 `--management-url https://manage.example.invalid:9443/ --server-id 部署服务器标识 --node-id 部署节点标识`。地址与身份必须来自部署文件，跨设备时 `localhost` 指的是考务电脑自身。

## 检查、启停与原请求查询

`checks`、`start`、`stop` 都使用带稳定请求编号的 JSON。下面生成一次检查请求，文件保存后可用于重试。

```powershell
$request = [ordered]@{
    requestId = [guid]::NewGuid().ToString('N')
    serverId = $env:EXAM_SERVER_ID
    nodeId = $env:EXAM_NODE_ID
}
$requestFile = Join-Path $PWD 'management-checks.json'
[IO.File]::WriteAllText($requestFile, ($request | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
& $cli management checks --input $requestFile --error-format json
& $cli management operation --request-id $request.requestId --error-format json
```

启停使用相同字段，各自创建新请求文件，再执行 `management start --input start.json` 或 `management stop --input stop.json`。已有请求超时、断线或尚在执行时，保留文件和编号，先运行 `management operation --request-id 原编号`。重试继续使用原文件，同一个编号不能改作另一次操作。

`management cancel --request-id 原编号` 请求取消正在执行的接管或重建，随后继续查询原操作。已生效的提升或目录替换保留实际结果。关闭 CLI 或按 Ctrl+C 仅停止本次等待，已受理的服务器操作须继续按原编号核实。

| 命令 | 输入及用途 |
| --- | --- |
| `management configure --input FILE` | `requestId`、`serverId`、`nodeId`、`expectedVersion`、`configuration`。先读取 `configuration`，保留当前版本并修改所需字段 |
| `management backup --input FILE` | `requestId`、`destinationDirectory`、`actorId`。目标为服务器上的备份目录，操作者填写当前管理登录用户名 |
| `management backup-info --source-directory 服务器目录` | 只读取得备份标识、来源目录及创建时间，供恢复请求使用 |
| `management restore --input FILE` | `requestId`、`backupId`、`sourceDirectory`、`actorId`。从服务器备份目录执行隔离恢复，继续核对恢复结果和业务就绪 |
| `management takeover --input FILE` | 使用当前备机的代次与版本，附已完成的旧主隔离记录 |
| `management rebuild --input FILE` | 从当前主机重建隔离节点，明确确认保留旧目录后重建 |
| `management reprotect --input FILE` | 手工核对已经追平的备用节点；普通复制恢复由后台自动进行 |

接管的完整字段见[请求模板](../samples/service-management/ha-takeover-request.example.json)，其隔离确认默认关闭。恢复步骤和旧主处理见[恢复运行手册](backup-and-failover-runbook.md)及[主备管理](high-availability-management.md)。管理请求中的数据、备份和恢复路径均属于目标服务器。

## 给 Codex 和脚本读取的输出

查询结果写入 stdout，错误写入 stderr。`--error-format json` 使失败输出使用单个 JSON 错误信封，包含 `schemaVersion`、`code`、`message`、`exitCode`。管理状态或操作已经取得有效响应但检查未通过时，stdout 保留可读取的诊断结果，同时返回非零退出码。脚本同时保存 stdout、stderr 和退出码，不能仅以输出非空判断成功。

```powershell
& $cli management status --error-format json --timeout-seconds 30 1> status.json 2> status-error.json
$statusExitCode = $LASTEXITCODE
```

输入文件采用 UTF-8 JSON，`--input -` 可从标准输入读取整个 JSON。名称含空格时加引号。`monitor stream`、`monitor events` 等连续命令按行输出 JSON。普通查询一般输出 JSON，`version`、`help`、注册和登录成功提示为文本。

| 退出码 | 含义与后续动作 |
| --- | --- |
| 0 | 命令成功；读取返回数据中的业务状态 |
| 2 | 命令、参数或输入无效，核对 `help` 与请求文件 |
| 3 | 尚未登录或权限不足，登录正确目标并核对账号权限 |
| 4 | 对象不存在、版本冲突或业务条件未满足；管理请求仍在执行或隔离恢复尚未完成时，也返回此值 |
| 5 | 网络连接失败或超时；写操作先查原请求 |
| 6 | 服务依赖、检查或执行失败，读取管理诊断及运行事件 |
| 130 | 本次等待已取消；写操作继续查原请求 |

`import verify` 的退出码 3 和 4 分别表示材料校验失败和编译失败，以 `IMPORT_VALIDATION_FAILED`、`IMPORT_COMPILATION_FAILED` 区分。缺少 XeLaTeX 或本地编译工具超时返回 5，错误码为 `IMPORT_TOOLCHAIN_UNAVAILABLE`。`--timeout-seconds` 设置本次调用的总等待时间，也约束等待口令和标准输入，不延长命令内部的较短时限。长操作可以设短等待后按原请求持续查询。

## 业务命令范围

| 命令组 | 用途 |
| --- | --- |
| `register / login / logout / whoami / operators` | 注册、登录、查询身份和管理考务账号 |
| `exams` | 考试查询、创建、设置、时间、学生可见性及考务隐藏范围 |
| `rooms / devices / roster` | 试场和设备管理、名册查询及导入更正 |
| `import / publications` | 本机材料检查、逐题编译预览、发布及取回源材料 |
| `monitor / decisions / commands` | 考试进度、回收、设备冲突裁决及指令补发 |
| `grading / reports` | 计分规则修订、重算状态、成绩五表及 Excel 导出 |
| `health / time` | 业务就绪状态和服务器时间 |
| `api METHOD PATH` | 使用当前业务会话调用正式业务 API，例如 `api GET /api/v1/diagnostics/runtime` 读取运行容量快照 |

运行 `help` 查看各命令完整参数。考试管理以名称选择考试，设备使用完整公开编号。只有本机编译 TeX 试题需要 XeLaTeX；状态、日志、监控和报表操作不需要安装 TeX 或 PostgreSQL 客户端。

## 设备查询、裁决和补传

| 命令 | 用途与输入 |
| --- | --- |
| `devices list` | 查看学生端编号、试场和连接信息 |
| `decisions keep 考试名称 学生端编号 --input FILE` | 从该考生的冲突设备中选择保留端；JSON 包含 `device`（保留设备的公开编号）和 `reason` |
| `commands resend 考试名称 学生端编号 start/end` | 最后一个位置参数为 `start` 或 `end`；只选择当前可重发的对应指令 |
| `commands reupload 考试名称 学生端编号` | 对尚未回收的考生重发原结束指令，请求原答卷补传，不恢复作答 |

考试、试场按名称选择，设备按完整公开编号精确匹配。裁决的操作者来自 `/operators/me`，期望版本来自实际读取的参试快照，不接受输入伪造操作者。重发响应读取实际 `CommandDelivery`，终端仅输出允许的回执字段，不输出解锁引用或未经筛选的元数据。

为可恢复操作提供稳定的 `--request-id`，如下例所示。

```text
commands reupload 合成考试 202610060001 --request-id synthetic-reupload-001
```

发送前，CLI 将逻辑服务器、登录账号、请求编号、原始输入指纹及解析后的请求加密保存在当前 Windows 账号的 CLI 目录。使用当前用户 DPAPI；同一请求的并发准备由文件锁保护。目录默认位于本机用户配置目录，可沿用 `EXAM_CLI_SESSION_DIRECTORY` 指定独立目录。

回执丢失后，重新运行同一命令、相同 `--request-id` 和原输入。即使申请已经从待审批列表消失、冲突已经裁决或出现了更新的指令，也仍发送原设备、原版本和原事件。相同请求编号换输入会被拒绝，不自动换成新的业务操作。每次重试都重新登录鉴权并调用服务器；本机记录不是授权凭据，也不以缓存结果冒充服务器成功。服务端确认过期、撤权或已收卷后仍可拒绝原重发操作。

不提供请求号时会生成并显示请求号，但自动化脚本宜事先固定请求号。不同 Windows 账号或丢失本机请求记录后不能保证重新解析出的内容等同原请求；应先核实原操作，避免生成新请求重复执行。

## 连续快照与通知

`monitor show/missing 考试名称` 保持一次性查询。

`monitor stream 考试名称 [间隔秒] [轮数]` 连续输出完整 `MonitoringSnapshot`，每行一个 JSON，默认每秒读取、不限轮数。它包含权威名单、业务与连接状态、页面、当前题号、答案进度、指令和回收情况，不依赖收齐事件端点。输出含考务数据，应按考务权限保存，不作为公开日志。

`monitor events 考试名称 [起始名单版本] [间隔秒] [轮数]` 同时读取收齐事件及当前冲突/失败。未给间隔时读取一次；指定间隔后持续读取。`monitor watch/follow` 仍只处理收齐事件，保持既有协议和独立性。

收齐事件沿用原 `CollectionCompletedNotification`（含 `eventId`、`rosterVersion` 等字段，不新增 `kind`）；冲突/失败为 `MonitorEventNotification`，`kind` 分别是 `conflict`、`failure`。可读提醒写入 stderr。指定 `--error-format json` 时，失败的 stderr 仅保留一个公共错误信封，实时业务数据继续写 stdout。

冲突按考试、参试代次和涉事设备集合去重；消失后再次出现可重新通知。失败覆盖题包下载失败、修订下载失败、本地答案保存失败和指令资源更新失败，按相关版本与失败状态去重，不因姓名或文案变化重复提醒。明细使用公开编号与试场名称，不输出内部设备标识。

读取失败时不输出伪造空快照。连续模式对网络故障、限流、5xx、损坏响应或迟到快照提示后重试，成功轮数不包含失败；鉴权失败立即退出。另一逻辑服务器或服务代次回退立即终止。快照须整页有效且时间、名单及试卷等版本不回退，拒绝的数据不改变通知去重状态。总超时返回5，用户取消返回130，一次读取失败也返回非零。

冲突和失败来自当前快照，不是持久事件历史，断线期间出现又消失的短暂异常不能补造。跨进程冲突/失败重新建立状态；收齐使用服务器持久事件和名单版本，调用方保存已处理版本并在重启时传入，可避免重播历史。零实际参试不产生收齐通知。

## 验证


[GUI/CLI覆盖矩阵](gui-cli-coverage.md) 记录正式入口；测试清单和一致性测试仅保留在本地测试目录。现场2秒显示、300会话、真实双机和最终发布验收仍归完整考试与发布核对，不由模块测试代替。

## 修改考试时间

先用 `exams show "考试名称"` 读取当前 `scheduleVersion`。`exams schedule "考试名称" --input time-change.json` 接受 `requestId`、`expectedVersion`、`schedule`（`startsAt` 和 `endsAt`）及选填的 `reason`。原因可以留空，填写时不超过1000字；操作者由服务器从当前登录会话取得，不能由输入文件冒充。

重试使用同一文件、原请求标识和原预期版本。其他人已修改时返回冲突，不自动读取新版本覆盖其修改。新建考试仍用 `exams create`；时间修改入口不创建考试。服务器把操作者、原因、修改时间、预期版本和新时间窗写入同一持久事件，历史事件不被重写。

保存后按服务器当前时间立即重算状态。早于开始时间为未开始，达到开始时间但早于结束时间为进行中，达到结束时间为已结束。把正在进行的考试推迟到未来会暂停学生作答并保留答案；到新的开始时间后继续未固定的答卷。已经固定或提交的答卷不重新开放。

旧版 `activation` 命令仅为兼容既有客户端保留，新学生端不使用激活或批准流程。

## 单节点与主备管理

management ha-status 对已核实的单节点运行返回退出码 0；双份复制尚未核实不会单独使健康主机业务失败。状态未知或本机关键检查失败仍返回非零。

主机故障后按方案 A 先实际断电或隔离旧主的全部业务网络，再使用 management takeover 的原请求 JSON（含隔离记录、预期代次和版本）。正常 GUI 的服务器管理页提供同一接口的醒目“接管为主服务器”按钮，登录页亦可直接进入管理。接管成功会启动业务并核对就绪，学生端保留原身份和请求自动重连。旧 emergency 路由不再接受新应急操作。

原请求失败或回执丢失时查询原 requestId 或重试原请求，不生成新提升操作。旧主必须保持隔离，经 management rebuild 从当前主机重建为备机；普通备机恢复连接则自动追平数据库、资源和持久确认。详见[主备管理](high-availability-management.md)。
