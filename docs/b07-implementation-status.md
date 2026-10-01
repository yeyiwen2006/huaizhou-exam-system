# B07 学生端发布 · 实现状态

任务卡：#15（B07）。基线：main @ `b3e7157`（B01–B06 已全部合并）。

## 交付内容

### 1. 可重复的独立发布配置/脚本

`deploy/student/publish-student.ps1`：

- **目标**：Windows 10 19041+ x64、普通账户（`asInvoker`，无 UAC 提权）、
  无需 .NET SDK/运行时、无需 TeX、无需管理员预配置
- **自包含**：`dotnet publish -r win-x64 --self-contained`，.NET 10 运行时与 WPF
  全部随包携带；原生依赖为 `e_sqlite3.dll`（随 Microsoft.Data.Sqlite 10.0.12 落在
  布局根目录）；Windows.Data.Pdf（试题渲染）为操作系统组件，不随包分发
- **布局校验**（脚本内强制断言，失败即发布失败）：exe 存在；内嵌清单含
  `asInvoker`（S16）与 `PerMonitorV2`（S17 100/125/150% 缩放）；WPF 程序集与
  hostpolicy（自包含证据）；原生 SQLite；不预置 `server.json` 与本地数据库
- **溯源**：`build-info.json` 记录 UTC 构建时间、SDK 版本、提交号（git 不可用时
  可显式 `-Commit`）；产物打包为 `Exam.Student-win-x64.zip`
- **锁文件**：RID 还原会向 `packages.lock.json` 写入 win-x64 段，该段会使 CI 的
  无 RID 锁定还原失败（NU1004），因此脚本先备份三个相关锁文件、发布完成后原样
  恢复，仓库锁文件保持不变（已验证哈希一致）

### 2. 可信服务地址/信任配置（server.json）

- **解析**：`Core/ServerEndpointConfig.cs`，exe 同目录 `server.json`，
  `formatVersion=1` + `host`（IP）+ `port` + 可选 `certificateSha256`；缺失返回
  空让界面提示"未部署"；字段级严格校验、错误信息面向监考可直接行动
- **证书指纹 pin**：`Core/ServerTrust.cs` + `HttpStudentApi`/`SignalRExamCommandFeed`
  的 TLS 回调（`SocketsHttpHandler.SslOptions`）——配置了指纹时 TLS 只信任指纹
  完全一致的服务器证书（内网自签证书无需在学生机安装、无需管理员），未配置时
  保持 Windows 标准链校验；握手时读取当前值，HTTP 与实时连接同一策略
- **不受信任节点**：TLS 校验失败转成 `UntrustedServerException`
  （继承 `HttpRequestException` 以兼容既有各页面网络异常处理）；激活页与登录页
  给出明确提示"服务器证书校验失败，已拒绝连接不受信任的服务器"
- **界面接线**：激活页按 部署配置 > 已激活设备 预填服务器地址；配置
  有效/缺失/无效三种状态各有横幅提示；手动输入在三种状态下保留（部署遗漏应急通道）
- **不内置真实凭据**：仓库只含 `deploy/student/server.example.json` 模板
  （TEST-NET-1 占位地址与全零指纹）；发布布局断言 `server.json` 不得预置

### 3. 隔离启动/依赖冒烟与配置负面测试

- `deploy/student/smoke-test-student.ps1`：对发布产物执行三场景隔离启动
  （无配置/有效配置/无效配置，`LOCALAPPDATA` 隔离到临时目录），断言真实主窗口
  创建、干净关闭退出码 0，并复查 DPI 清单与自包含布局；退出码 0/1 一致
- 负面测试（单元）：
  - `ServerEndpointConfigTests`（17 用例）：缺失文件、各字段无效形态、指纹
    规范化（空格/冒号/大小写）、错误信息可行动性
  - `ServerTrustTests`（6 用例）：pin 匹配/不匹配、未配置走标准链校验、无效
    指纹拒绝且不改现状、以及本地 SslStream TLS 回路——错误指纹经生产
    `HttpStudentApi` 端到端抛 `UntrustedServerException`，正确指纹请求真实送达
    TLS 服务器并解析业务信封

### 4. 安装运行说明

`deploy/student/README.md`（随包复制）：目标环境表、发布命令、部署步骤
（拷贝 → 复制模板为 server.json → 填地址/指纹 → 双击运行）、指纹获取命令
（服务器侧 PowerShell 读 Kestrel 证书 SHA-256）、冒烟命令、恢复用例重现方式

## 验证结果（main @ b3e7157 + 本 PR，本机）

| 检查 | 结果 |
| --- | --- |
| `restore --locked-mode`（CI 式） | 通过 |
| 全解决方案 Release 构建 | 0 警告 0 错误 |
| 学生端 Exam.StudentUnitTests | 342/343（1 失败=本机无 XeLaTeX，既有环境项） |
| 身份 Exam.StudentIdentityTests | 25/25 |
| 管理 Exam.AdminUnitTests | 203/203 |
| Exam.UnitTests | 1423/1426（3 失败=2 符号链接权限 + 1 TeXLive，既有环境项） |
| publish-student.ps1 | 全部布局断言通过，zip 71 MB，仓库锁文件哈希不变 |
| smoke-test-student.ps1 | 三场景 PASS（真实窗口标题"怀籀传承在线考试系统 · 学生端"） |

测试增量：学生端 315 → 343（+28：配置 17 + 信任 6 + 原有 5 项内联数据用例归并）。

## 与验收判据的对应

- 「全新目标设备无需 SDK、TeX 或管理员预配置即可使用」：自包含发布 + asInvoker
  + 指纹 pin 免装证书；smoke 三场景在隔离用户目录下验证启动/关闭
- 「所有恢复用例能在发布包重现」：发布包与测试套件同源同版本（锁定依赖图），
  恢复/交卷/更新行为由学生端 343 项测试覆盖；整机 S16 演练（退出恢复、换账号、
  全新部署）以本发布包为对象，归 T 阶段（T01/T02）执行并记录
- 「退出恢复限制与显示缩放检查」：退出/恢复限制逻辑为 B02/B04/B06 已交付并在
  本套件回归；缩放为内嵌 PerMonitorV2 清单，发布与冒烟脚本均强制断言

## 已知限制 / 待外部条件

- 冒烟脚本不做 UI 文本断言（无 UI 自动化框架），配置横幅文案的正确性由代码
  审查与手工冒烟保障；如 C 组引入 UIA 工具可补
- 指纹 pin 目前为单节点（B07 范围）；主备双地址信任由 B08（已交接 F）扩展，
  `formatVersion` 字段已为其预留演进空间
- 服务器侧证书由 A07/F 运维在机器证书库维护（Kestrel 默认证书）；README 提供的
  指纹读取命令以 Kestrel 配置的证书缩略图为输入
- 本机无 git 可执行文件，`build-info.json` 的提交号需显式 `-Commit` 传入；
  CI/正式构建机上有 git 时自动获取
