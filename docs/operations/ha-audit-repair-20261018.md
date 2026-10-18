# 主备审计问题修复与验证记录

整理来源：`ha-audit-report-20261018.md`、远端 `main`、隔离工作副本中的源码与回归结果。脱敏声明：本文不记录现场地址、真实身份、口令、证书、连接配置或考试数据；测试均使用合成数据。

## 结论与基线

审计记录的 19 项待处理问题已补充修复，HA09 保留主分支已有修复并继续回归。修复覆盖管理入口、持久请求、重建恢复、确认目录、资源交换、管理授权和学生端换主行为。

审计报告的最后基线为 `04880c8`。本次从远端 `aed80db986923ce705ba9207cda3093c2a956fd4` 开始，交付前再次拉取并合入 `741ddf43221e3e93a45608353f310b5edf86c45c`，保留其“复用备机成功启动核验”的优化。后述主备专项回归在合入后的代码上运行；较大范围旧测试的基线对照是在 `aed80db` 上完成。

该交付是源码修复和隔离验证。真实 PostgreSQL 流复制、提升、基础备份、校验、重复重建和故障阶段恢复已实际执行；Windows 服务控制、证书材料加载和部分节点 HTTP 通道由测试适配器提供。尚未进行生产部署、两台独立物理主机断电或实际考试规模负载验收。

## 问题对应关系

| 编号 | 修复后的行为 | 已执行验证 |
|---|---|---|
| HA01 | 已配置备机可独立进入只读核验，包括首次打开页面、当前主机 HA 查询失败的情况；最终接管仍要求目标认证、身份、代次、角色、完整证据和旧主隔离确认。 | 真实管理 ViewModel 的冷启动与故障分支；实际数据库接管。 |
| HA02 | 已隔离旧主的只读 `Checks` 未决请求继续保留，允许核验备机接管；未决提升、重建等写操作仍阻止冲突操作。 | 旧主无请求、只读请求和重建请求三种门禁组合。 |
| HA03 | 统一刷新按所属节点查询原 RequestId，核对操作者和内容摘要；有效 Completed 或 Failed 终态结算原 pending。查询失败不删除请求，也不重发写操作。 | Completed 和 Failed 两条真实刷新路径，均没有额外 mutation。 |
| HA04 | 相同逻辑事件通过业务内容和授权元组校验后，更新更高 epoch 的持久传输封套；保留安装状态、Applied 状态和原回执 ID。 | 未安装及安装完成待回执场景，结合既有试卷更新回归。 |
| HA05 | 持久接受后的 HA 操作使用独立期限，不随页面取消。重建保存 Preparing / Prepared / Installing / Completed / Aborted 阶段；准备失败仅在原目录身份未变、未交换、外部备份进程已退出时安全结案；安装中断沿原意图续做。 | 请求取消；三处真实目录改名边界；真实准备阶段结案和安装阶段拒绝结案；识别实际运行的 `pg_basebackup`；提升前后中断恢复。 |
| HA06 | 用原请求的完成状态和当前权威分别表达“曾经完成”与“当前角色”；合法后继版本覆盖历史审计时可结案，不重做旧角色操作，也不回退版本。 | 实际重建完成后模拟丢失最终 journal，再恢复保护推进版本，原重建成功结案；客户端拒绝缺失历史或请求错配证据。 |
| HA07 | 同 epoch 的恢复保护沿用当前确认目录，包括初始根目录和已有选择的 epoch 目录；不会让运行中的生产者与管理读取分叉。 | 初次恢复保护与提升后恢复保护，核对两端确认头；旧选择目录兼容回归。 |
| HA08 | 保持业务行、schema、序列定义和归属的精确比较；允许正向、不循环、归属业务整数列的序列因 WAL 预留而合法超前，同时核实下一编号范围和高于已用最大值。其他序列仍精确比较。 | 真实 PostgreSQL 安全序列超前、下一编号冲突、定义改变及原有恢复比较回归。 |
| HA09 | 保留前台 750 ms、后台 10 s 的 peer 预算及后台向前台让路行为。 | 1.2 s 正常 peer 回包可建立保护；前台可打断同实例后台等待。 |
| HA10 | 资源交换先写入持久意图，绑定目录、旧新摘要和节点权威；重启后确定性续做，或仅回滚可核实原件。目标路径缺失时也有恢复入口。 | 改名前、两次改名之间、第二次改名后的实际文件状态；错误权威拒绝恢复。 |
| HA11 | 操作日志枚举仅排除通过结构校验的已知旧 `node-selection.json`；损坏选择或未知 JSON 继续阻止不安全操作。 | 真实文件存储的合法选择、损坏选择和未知日志。 |
| HA12 | 管理写入与业务读取共用默认路径 `<本节点确认根目录>/node-selection.json`；显式路径优先。新默认文件缺失时可读取配置中已知的旧操作目录选择。 | 默认、显式既有用例、旧默认文件及旧 epoch 确认目录。 |
| HA13 | 管理会话绑定账号凭据、管理员权限和授权版本；在线刷新观察到停用或变更后撤销旧 token，串行发布账号快照防止旧结果覆盖撤销。离线缓存及会话有 24 h 有效窗口。 | 停用、密码改变、权限改变和授权版本改变后的旧 token 拒绝。 |
| HA14 | 未确认业务请求按逻辑服务器、操作者、方法、路径及内容摘要加密持久保存；换主、重新登录及客户端重启保留原 ID；旧 epoch 迟到响应不能清除新节点上的待确认请求。 | 实际业务客户端响应丢失后换地址、重新创建客户端；服务器/操作者/内容隔离及旧回包保护。 |
| HA15 | 摘要缓存绑定资源根目录的文件系统对象身份；根目录替换后重建 watcher 和缓存。普通不变内容继续复用摘要。 | Windows 实际目录替换、同长度内容修改并恢复时间戳。 |
| HA16 | 本次创建的永久复制槽有持久所有权记录；新备机和源库核实合法消费者后，只回收已完成或安全失败任务拥有且不活跃的旧槽。无归属证据及仍在使用的槽保留。 | 实际连续两次重建后只保留一个受管槽，未知槽和原库隔离目录保留；准备失败槽回收。 |
| HA17 | 资源交换日志只回收明确属于本节点权威、已被替代的一份历史副本，保留当前资源及一份已知上一版；未决交换、未知遗留目录和隔离原件不清理。 | 四轮不同资源传输后历史数量有界，隔离原件仍存在。 |
| HA18 | 校时只使用最终成功时间请求本身的 RTT；故障发现、换主和重连等待不计入时间补偿。样本编号及 epoch 阻止迟到旧样本覆盖。 | 确定性 60 s 故障发现、40 ms 成功请求及并发旧样本。 |
| HA19 | 资源条目上限只计算根目录下的文件和子目录，根目录哨兵不占用容量。 | 可配置小上限下达到上限成功、超过上限拒绝，无需构造十万文件。 |
| HA20 | 共享文件锁只保护本地确认读写；peer 等待在锁外。先保存本机持久确认，再按原确认头比较后写入双份结果，旧回包不能覆盖更新的本机提交。短暂锁争用有限重试。 | 独立管理/业务确认器并发、慢 peer、同实例抢占和晚到 ACK；实际管理恢复保护流程。 |

## 大数据场景中的确认成本

1. 业务成功仍以活动节点本机持久化为最低条件。备机不可达时，以 `EmergencySingleNode` 表示本机成功，后台再补齐保护；不将普通保存改成等待整个考试数据库的跨机比对。
2. peer 前台等待最多 750 ms，后台尝试预算 10 s，已有失败退避继续生效。后台同实例任务向前台让路；跨实例管理确认不再持有文件锁等待网络。两个数值是 peer 等待预算，不是整个 API 的响应时限或数据丢失窗口承诺。
3. 常规确认只核对必要的节点/集群身份、权威代次、WAL 边界、资源版本摘要和持久回执。沿用主分支的成功启动核验结果与资源 revision 缓存；资源 revision 未变时不重新扫描考试业务表。
4. 文件摘要缓存复用未变化的文件内容，仍检查目录身份、条目元数据和变更通知。资源发生变化才重新核验内容；管理普通观察也复用本地资源摘要。管理默认材料探针按资源 revision 复用结果；正式运行时继续使用业务服务提供的已核验启动状态。
5. 完整数据库一致性检查留在启动、恢复与相关材料变更边界。实际基础备份和恢复校验仍按操作要求执行，不把恢复验收放到心跳或每次答卷保存中。请求持久记录只保存业务内容摘要，不复制大型试卷或名单正文。

异步 WAL 复制在主机永久损坏时仍可能丢失尚未复制的数据；单份成功不等于双份保护。序列在 WAL 恢复后跳过预留编号也属于正常行为，不能仅因 `last_value` 不同就拒绝有效恢复。参见 [PostgreSQL 流复制说明](https://www.postgresql.org/docs/18/warm-standby.html) 与 [序列函数说明](https://www.postgresql.org/docs/18/functions-sequence.html)。

## 验证记录

工具为 .NET SDK 10.0.402、Windows、PostgreSQL 18.6；数据库全部为 loopback 监听的隔离合成测试实例。按仓库现有约定，测试夹具、测试运行设置、TRX 和执行脚本保存在本地被忽略的 `tests/`、`eng/manual-test/`、`artifacts/`，不混入正式源码发布。

| 验证集合 | 最终结果 | 本地证据 |
|---|---:|---|
| HA / 管理操作 / 管理客户端单元回归 | 115 通过，0 失败 | `artifacts/ha-tests/ha-unit-final.trx` |
| 当前考务端专项组件回归 | 7 通过，0 失败 | `artifacts/ha-tests/ha-admin-final.trx` |
| 学生试卷更新与校时回归 | 40 通过，0 失败 | `artifacts/ha-tests/ha-student.trx` |
| PostgreSQL / 物理重建专项 | 22 通过，0 失败 | `artifacts/ha-tests/ha-native-final.trx` |
| 根解决方案锁定还原与 Release 构建 | 成功，0 警告、0 错误 | 构建控制台输出 |

最终专项共 184 项，全部通过、无跳过；数据库专项约 2 分 58 秒。测试结束后已停止本次隔离数据库源实例。新增源码与文档通过隐私模式扫描，文档相对链接与 `git diff --check` 均通过。

数据库专项覆盖真实流复制、停止原主库、`pg_promote`、`pg_basebackup`、`pg_verifybackup`、重建目录交换、恢复双份保护、准备失败结案、重复重建槽回收和确认锁并发。故障注入包含异常中断及构造持久崩溃阶段；不是实际机器断电实验。历史重建结案检查了重建启动次数没有增加。

较大范围旧测试另作基线对照，没有将其写成全绿：

- 后端旧测试在修复工作副本中 1,635 项，1,629 通过、6 失败；未修改的 `aed80db` 同套旧测试为 1,620 项、1,613 通过、7 失败。本次 6 个失败测试均在未修改基线中同样失败，主要涉及旧地址规则预期、旧提示文本、启动夹具、编译依赖及 CLI 取消行为；基线另有进程资源相关失败。
- 学生端旧测试两边均为 437 项，419 通过、18 失败，失败名称集合相同，涉及旧 TLS 预期、旧授权/Hub 夹具和题目编译依赖。最终专项另补了试卷换主及校时用例。
- 旧完整考务端测试项目引用已移除的界面属性而不能编译。本次单独建立使用当前正式项目的 HA 组件回归，没有为迁就过时测试改变产品接口。

这些对照在合入 `741ddf4` 前执行；合入后复验的是上述主备专项和正式构建。因此结论是主备专项通过，并非整个历史测试库全部通过。

实际使用的命令如下；`dotnet` 指向本机受控 SDK，测试项目和原生测试脚本来自本次本地回归目录：

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode -p:NuGetAudit=false --disable-parallel -m:1 -nr:false
dotnet build Huaizhou.ExamSystem.sln --no-restore -c Release -m:1 -nr:false

dotnet test tests/Unit/Exam.UnitTests/Exam.UnitTests.csproj --no-restore -c Release -m:1 -nr:false --filter 'FullyQualifiedName~HighAvailability|FullyQualifiedName~ManagementOperation|FullyQualifiedName~LocalManagementClientTests&FullyQualifiedName!~InvalidEndpointIsRejectedBeforeSendingCredentials' --logger 'trx;LogFileName=ha-unit-final.trx' --results-directory artifacts/ha-tests
dotnet test tests/Unit/Exam.HaAdminAuditTests/Exam.HaAdminAuditTests.csproj --no-restore -c Release -m:1 -nr:false --logger 'trx;LogFileName=ha-admin-final.trx' --results-directory artifacts/ha-tests
dotnet test tests/Unit/Exam.StudentUnitTests/Exam.StudentUnitTests.csproj --no-restore -c Release -m:1 -nr:false --filter 'FullyQualifiedName~HaClockAuditTests|FullyQualifiedName~PaperUpdateTests' --logger 'trx;LogFileName=ha-student.trx' --results-directory artifacts/ha-tests

# The local script starts and stops only its isolated synthetic PostgreSQL source.
./eng/manual-test/Invoke-HaAuditNative.ps1 -Filter 'FullyQualifiedName~NativeManagementFailoverTests|FullyQualifiedName~HaConfirmationAuditTests|FullyQualifiedName~PostgresRecoveryStateComparisonTests|FullyQualifiedName~HaRebuildRecoveryAuditTests' -Result 'ha-native-final.trx'
```

`NuGetAudit=false` 只关闭此处还原时的在线漏洞查询，不改变锁文件或依赖版本；不能把构建结果视为已完成依赖安全审计。旧地址预期用例被明确排除出专项，已计入上述全量基线失败记录。

## 升级与后续现场验收

- 在维护窗口一起升级两端 `Exam.Server`、`Exam.Management`、考务端和学生端。保留实际部署配置、证书、数据库、资源、确认日志及原请求记录；本修复不需要新增数据库迁移，也不重写已有迁移。
- 两个宿主对本节点必须使用相同的 `HighAvailability:SelectionPath`。推荐显式配置；若省略，则共同使用本节点确认根目录下的 `node-selection.json`。旧选择位于操作目录时，两宿主需能读到一致的 `Management:OperationDirectory`，或将显式选择路径指向已核实的旧文件。配置缺失时不能猜测另一个目录，更不能手工伪造选择内容。
- 首次升级后读取角色、epoch、确认目录与原请求终态。未决重建先查询原 RequestId；可证明尚未安装的失败准备将安全结案，已开始安装的操作按原阶段恢复。旧版本留下的无阶段证据目录继续保留并等待受保护维护，不能删除 journal 强行解锁。
- 旧主接管前仍须人工完整隔离；旧主恢复后通过重建成为备机。新代码不会自动提升，也不会为使界面显示双机而绕过身份、隔离或资源证明。
- 只有本次持久记录能够证明归属的槽和资源副本才自动回收。升级前遗留的未知槽、旧资源副本和数据库隔离原件需另行核对用途，不能按名称批量删除。暂存或隔离证据保留可能需要额外磁盘空间。
- 离线管理认证窗口为 24 h；在线观察到停用、权限或凭据版本变化后旧会话失效。故障期间应使用已部署并授权的本节点管理身份，不依赖旧主在线认证。

以下现场验收仍需部署环境完成，不能由本机测试代替：

- [ ] 两台独立 Windows 主机上的实际服务账户、目录 DACL、TLS 证书与受保护配置验收。
- [ ] 故障主机断电及完整网络隔离后，备机独立核验、人工接管、考务与学生连接恢复。
- [ ] 真实大容量试卷/资源与 300 会话持续保存期间，慢 peer、丢包、超时、恢复保护和恢复后的延迟分布。
- [ ] 各重建/交换边界的实际进程强制终止和机器断电、磁盘写满及受控恢复。
- [ ] 原主隔离重建后的长期复制、槽占用、WAL 与历史资源容量观察。

部署入口与原有边界见 [PostgreSQL 部署基线](../../deploy/postgresql/README.md)、[维护流程](postgresql-maintenance.md) 和 [验收标准](../acceptance-criteria.md)。
