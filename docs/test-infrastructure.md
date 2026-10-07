# 测试、证据与发布工具

使用隔离环境和合成数据，按实际执行的范围记录结果。会话、数据库连接配置、私钥和原始诊断不作为公开证据。

## 分类检查

先执行锁定还原和 Release 构建，使用 `global.json` 固定的 SDK。公共入口会发现六个真正的 xUnit 项目及所有类别；工具程序不冒充测试项目。新增项目、类别或过滤范围未登记会失败。

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release
python eng/testing/run_checks.py --profile core --profile wpf --output .artifacts/checks
python eng/testing/run_checks.py --profile postgres --profile postgres-physical --postgres-bin <POSTGRES_BIN> --native-physical --output .artifacts/database-checks
python eng/testing/run_checks.py --profile latex --output .artifacts/latex-checks
python eng/testing/run_checks.py --profile import-e2e --postgres-bin <POSTGRES_BIN> --output .artifacts/import-checks
python eng/testing/validate_results.py .artifacts/checks/run-report.json
```

每条命令返回非零时，其结果不能标为通过。`postgres-physical` 显式启用专用 Windows 原生 PostgreSQL 18 集群；普通 PostgreSQL profile 保留真实适配器检查，且明确排除物理恢复。XeLaTeX 需在 PATH 中。缺依赖、零匹配、损坏 TRX、跳过、非零进程退出、取消和半份证据都会拒绝通过。原始输出只在独占临时目录中保留到清理，公开 TRX 仅含测试标识、结果及计数。

报告包含提交 SHA、源码指纹、脏树状态、请求 profile、各 suite 的准确过滤器与计数、证据相对路径和 SHA256。未请求的重型检查总会列出。运行期间源码变化也失败。本机检查可使用脏树；CI 聚合与候选发布必须为干净提交，多个作业须来自相同源码且 profile 齐全。指纹按 Git 文本规则规范化换行，以便 Windows/Linux 核对。

CI 保留 Windows 普通/WPF 分离和 PostgreSQL 18 作业，增加工具负例与五组件两次发布、完整性及重建检查。汇总作业要求全部必需作业成功、证据齐全。托管 CI 未提供 XeLaTeX、专用物理恢复或真实考试设备时，报告如实列出；本机可分别补测。没有使用 `continue-on-error`。

## CLI 公共边界

`--error-format text|json` 统一受控 stderr；JSON 为 `schemaVersion/code/message/exitCode`。错误响应中的码仅允许公共契约常量，正文、参数和异常原文不回显。退出码：0 成功；2 输入/配置；3 鉴权；4 业务拒绝；5 网络/超时；6 其他失败；130 取消。

`--timeout-seconds 1..3900` 控制整条命令的总期限，仍保留模块自己的较短时限。成功 stdout、文件导出及现有独立管理语义沿用原实现。统一重复选项、缺值、服务器原点、相对路径和禁止重定向。`artifact-info FILE` 只读 PE 元数据，不加载目标程序集；供发布核对实际构建身份。真实子进程测试连同既有报表、计分和管理测试纳入普通 profile。

本地 `import verify` 保留退出码 3（清单校验）和 4（编译/定位失败），错误码分别为 `IMPORT_VALIDATION_FAILED` 与 `IMPORT_COMPILATION_FAILED`，仅附公开契约规定的问题标识，不输出输入路径或题包诊断原文。该命令不受业务服务器环境地址影响。

## 隔离、故障与合成负载

```powershell
python eng/testing/run_synthetic.py --postgres-bin <POSTGRES_BIN> --sessions 3 --duration-seconds 4 --mode load --output .artifacts/load
python eng/testing/run_synthetic.py --postgres-bin <POSTGRES_BIN> --sessions 3 --duration-seconds 4 --mode faults --output .artifacts/faults
python eng/testing/run_synthetic.py --postgres-bin <POSTGRES_BIN> --sessions 300 --duration-seconds 120 --mode separate --output .artifacts/capacity
```

规模和时长是显式请求，不能缩小后标成原范围通过。保留现有 burst/steady/commands/download/final/mixed 运行器及性能阈值。Python 和 PowerShell 旧入口改为共享编排包装，支持 1..86400 秒；已有私有夹具通过 `--fixture` 使用，原创建者负责删除数据库。

自动模式创建全新的回环 PostgreSQL 18 集群及 `exam_load_*` 数据库，随机 capability 与数据库 comment 双向绑定。不按名称前缀推断清理权限。每次运行只持有自己创建的进程、Windows Job 或 Linux 进程组；拒绝链接、目录越界、被替换的标记和外来进程。物理恢复测试可能重启同一集群，清理会核对实际数据目录并停止该集群。所有私有材料随本次临时目录删除。

夹具先通过正式建考服务生成考试目录和考试说明，再通过版本服务发布 120 题加密显示 ZIP 和完整合成计分规则；CLI 和 HTTP API 必须查到同一场考试。显示 PDF 是测试字节，未经真实 XeLaTeX 编译或人工预览验收。测试宿主复用业务路由、传输配置、鉴权、数据库适配器和后台任务，但使用独立的回环启动入口、临时证书及单节点确认适配。它显式允许隔离夹具的未初始化主备状态，不执行正式节点部署与双机确认。因此不能把该宿主的通过结果视为正式启动、证书部署或主备保护验收。只有此宿主提供 `/api/v1/testing/commands` 来生成合成时间通知；正式 `Exam.Server` 不注册该入口。

故障代理只对明确回环地址生效，按固定请求次序触发。服务成功处理后丢弃响应，原请求重试 20 次仍须返回原回执；重启后再次读取回执；网络延迟、下载截断与等长损坏必须被识别，并核对恢复后的资源 SHA256。事件必须记录真实上游 200，未触发不能通过。

最终审计使用现有 Repeatable Read 只读事务，逐题核对固定独立预期答案、实际参试、最终答卷、唯一回执、计分任务、收齐事件。另读取既有计分状态 API，确认当前规则版本和全部最终答卷已计分。公开汇总核对 run、考试指纹、源码与所有子证据哈希。资源 SHA256 为可选字段，旧输入仍能做长度及身份核对，不能声称完成摘要校验。

局部合成验证不能证明真实设备、最长考试、最大实际题包、主备接管时限或断电持久性。数据库恢复测试与故障代理各自保留准确范围。

## 发布与重建

```powershell
python eng/packaging/package.py build --output .artifacts/package-a
python eng/packaging/package.py build --output .artifacts/package-b
python eng/packaging/package.py verify .artifacts/package-a
python eng/packaging/package.py compare .artifacts/package-a .artifacts/package-b --output .artifacts/rebuild.json
python eng/packaging/package.py smoke .artifacts/package-a --output .artifacts/package-smoke.json
```

候选默认拒绝脏树；`--development` 仅用于明确标识的本机检查。目标固定 win-x64、自包含、Release，五组件含独立 `Exam.Management`。普通构建用原 `packages.lock.json`，指定 RID 时用 `packages.win-x64.lock.json`，都严格锁定还原。新增 RID 锁需随源码审查，不在发布时绕过锁定或自动更新。

清单绑定 SDK、源码、依赖锁、迁移 SQL、实际程序集元数据、组件入口、全部相对文件长度与 SHA256。拒绝缺失、多余、混版、错误 RID、路径越界、链接、被篡改资源、缺原生/WPF资源和无完成标记。清单自身和完成标记不加入文件摘要以免自引用；完成标记反向绑定清单 SHA256。发布到随机 partial 目录，全部校验成功才改名；失败保留半成品，不覆盖既有输出。

每次发布都强制重新编译组件及其引用，重建逐文件比较，不忽略整个二进制或资源目录。CLI 通过实际自包含 exe 做 help/version/元数据冒烟，禁用全局运行时查找；服务和独立管理服务还检查缺少部署配置时的受控拒绝。开发机启动不能证明全新 Windows 独立运行；受保护的服务配置、证书部署、真实 GUI 操作及正式候选交接由对应现场验证。PostgreSQL 18 和考务编译需要的 XeLaTeX 均是外部依赖，自包含没有把它们纳入。

GUI/CLI 的实际入口、消费方缺口与检查依据见 `gui-cli-coverage.md` 和 `test-coverage.json`。保留 WPF 分类回归，不能把通用 `api` 传输命令算成每个业务专用 CLI 已实现。

`eng/testing/collect_results.py` 将全部请求分类、实际合成负载与故障、五组件候选和重建结果绑定到同一个干净提交。CI 的 `validate_ci.py` 核对两个发布清单、完成标记、重建与启动记录以及 Windows/PostgreSQL 的分类报告；任何缺项、异源或必需作业失败都不能形成通过结论。
