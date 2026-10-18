# 学生端发布包（部署信任）

本目录包含学生端的发布配置、部署配置模板与冒烟测试脚本。发布产物是一个自包含的
文件夹，拷贝到全新目标设备即可用普通账户运行。

## 目标环境

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 19041 及以上 / Windows 11，x64 |
| 账户 | 普通用户账户，**无需管理员权限** |
| .NET | **无需安装**任何 SDK 或运行时（自包含随包携带） |
| TeX | 无需安装（试题在考务端编译成 PDF 题包下发） |
| 证书 | 无需在学生机安装服务器证书（见下方“信任配置”） |
| 显示 | 100% / 125% / 150% 缩放下界面清晰（PerMonitorV2） |

## 统一部署入口

同时部署三端时，双击仓库根目录的 `生成部署包.cmd`。学生端位于 `artifacts/deployment/日期时间/student`，对应压缩包为 `Exam.student-win-x64.zip`。完整解压后，双击 `启动学生端.cmd` 或 `Exam.Student.exe`。下面的单端脚本仍可单独发布学生端。

## 发布

在仓库根目录执行（需要 .NET 10 SDK，见 `global.json`）：

```powershell
powershell -ExecutionPolicy Bypass -File deploy\student\publish-student.ps1
```

- 产物：`artifacts\student\win-x64\publish\`（文件夹）与 `Exam.Student-win-x64.zip`
- 脚本内置布局校验：`asInvoker`、`PerMonitorV2`、自包含运行时（WPF 程序集、
  hostpolicy）、原生 SQLite（`e_sqlite3.dll`）、不预置 `server.json` 与本地数据库
- 构建溯源写入 `build-info.json`（UTC 时间、SDK 版本、提交号）；无法用 git 取
  提交号时可显式传 `-Commit <sha>`
- 锁文件说明：自包含发布按锁定版本解析（还原会临时给锁文件加 win-x64 段，
  脚本在发布完成后立即恢复仓库原锁文件，保证 CI 的无 RID 锁定还原不受影响）

## 部署到考试机

1. 把 `publish` 文件夹（或 zip 解压结果）拷贝到目标机任意可写位置，例如
   `D:\ExamStudent`。
2. 复制 `server.example.json` 为 `server.json`，填写实际服务器地址：

   ```json
   {
     "formatVersion": 1,
     "host": "服务器IP",
     "port": 443,
     "certificateSha256": "服务器HTTPS证书SHA-256指纹（可选）"
   }
   ```

3. 双击 `Exam.Student.exe` 运行，填写服务器 IP 或域名及考生信息。默认连接 HTTPS 443，无需单独填写端口；非标准端口使用完整地址，例如 `https://exam.example.com:5443`。

`server.json` 必须放在 exe 同目录。应用启动时读取：

- 文件有效时读取证书信任配置；无既有连接记录时以配置地址预填登录页，未指定端口时使用 HTTPS 443。
- 文件缺失时按 Windows 标准证书链验证服务器，登录页手动填写连接信息。
- 文件无效时不应用其中的证书信任配置，需修正文件后重新启动。
- 手动输入在三种情况下都保留，作为部署遗漏时的应急通道。

已登记设备启动时优先恢复受保护记录中的活动节点。接管后自动连接已登记的新主机，身份、参试记录、通知和原答卷提交任务继续使用；重启无需每次修改地址，但仍需输入考生信息登录。只有一个健康活动节点时无需等另一节点上线。两端业务入口须对该学生机可达，客户端不会创建网络通道或自动提升只读备库。

## 考试桌面

学生端从启动起使用独立 Windows 桌面，其中不启动 Explorer 或任务视图，触控板手势无法借用原桌面的虚拟桌面切换应用。登录窗口可移动、缩放和关闭，登录后使用全屏和键盘限制；关闭整个学生端才返回原桌面。独立守护进程负责在界面退出或异常终止后恢复原桌面，无需修改驱动、注册表或永久策略。Windows 安全界面和管理员操作仍由系统控制。

部署前在目标机验证三指/四指手势、任务视图、外接显示器、正常退出、界面异常终止及锁屏再解锁。独立桌面上的输入法和辅助工具也须现场验证；本地隐藏桌面启动检查不能替代这些实机检查。

## 信任配置（证书指纹 pin）

服务器以 HTTPS 提供服务。学校内网通常使用自签证书，学生机默认不信任它。两种处理方式：

1. **推荐：指纹 pin**。在 `server.json` 填写 `certificateSha256`（证书 SHA-256
   指纹，64 位十六进制，允许空格/冒号分隔）。学生端 TLS 握手时只信任与该指纹
   完全一致的服务器证书，指纹不符立即拒绝连接并明确提示“服务器不受信任”。
   无需在学生机安装证书，无需管理员权限。
2. 不填 `certificateSha256` 时使用 Windows 标准证书链校验（适合服务器证书由
   域/机构 CA 签发的环境）。

在**服务器机器**上读取 Kestrel 所用证书的 SHA-256 指纹：

```powershell
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq '<Kestrel配置的证书缩略图>' }
[System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($cert.RawData)) -replace '-',''
```

## 通过公网 HTTPS 入口连接主备节点

使用 Cloudflare 等 HTTPS 代理时，学生端可以将事先部署的公网域名对应到服务器返回的原主备节点。两个公网入口使用 [server.public.example.json](server.public.example.json)，仅一个入口使用 [server.public.single.example.json](server.public.single.example.json)。复制为 exe 同目录的 `server.json`，替换逻辑服务器标识、节点标识、公网域名及原节点地址。示例中的身份和地址均为合成占位值。

顶层 `host`、`port` 必须对应其中一个公网入口。`publicEndpoints.nodes` 可包含一至两个不同节点，各自的 `originHost`、`originPort` 与服务器下发的节点地址一致。只配置当前可用节点即可登录；未配置公网入口的已登记节点保留原地址、身份及证书指纹。显式 `publicEndpoints` 使用 Windows 标准 HTTPS 证书校验，不能同时配置顶层 `certificateSha256`。登录后的 API、通知和资源下载沿用该映射，直接访问原节点时仍使用原证书指纹。

公网映射从本机部署文件读取；首次可信 HTTPS 登录的输入域名也可绑定到该响应明确标识的当前节点，原节点身份与指纹另行保存。服务器不能借此任意新增公网域名。已登记设备会保留原信任集合，临时通道更换域名后须安排明确的信任迁移，不能通过删除设备记录或答卷处理。每个公网入口须能访问对应节点；客户端配置不会创建通道或处理通道自身的故障接管。

未配置显式映射而以顶层指纹验证首次输入域名时，该指纹必须匹配公网入口证书。登录后将公网入口指纹与服务器返回的原节点指纹分开保存，重复登录不会再将两者混为同一证书。

一个健康活动节点即可承接登录，成功登录不等待另一节点确认。原主机单独运行时可直接使用其入口；备机单独运行时须先由管理端按隔离及持久确认流程完成接管，再使用该节点的入口。仍处于物理只读状态的备库不能因客户端发现另一台不可达而获得写入权。

若内网连接正常、输入公网代理域名却提示证书校验失败，核对正在运行的 exe 同目录 `server.json`：内网指纹与公网代理证书不同。部署正确的公网配置并重启学生端即可切换校验方式；不能关闭 TLS 验证，也不能复制代理证书指纹冒充原节点指纹。保留原设备登记与答卷。临时隧道域名变化后，由部署方更新映射后重启客户端。

## 冒烟测试（发布产物）

```powershell
powershell -ExecutionPolicy Bypass -File deploy\student\smoke-test-student.ps1 -DedicatedInteractiveTestAccount
```

仅在专用、无真实数据的 Windows 测试账号和未放置 `server.json` 的发布目录运行，脚本会切换交互桌面。依次检查无配置 / 有效配置 / 无效配置，按守护进程的实际子进程与桌面查找窗口，关闭后验证退出码，并复查 DPI 清单与自包含布局。改变 `LOCALAPPDATA` 环境变量不能可靠隔离 Windows 已知用户目录，因此不将其当作隔离措施。脚本不删除用户目录数据，不覆盖原部署配置。首次启动可能受防病毒扫描影响，保留启动失败信息后核对。

## 恢复用例在发布包重现

发布包与仓库代码同源，恢复/交卷/更新等行为由学生端测试套件覆盖（含真实 SQLite
落盘）；在发布机上执行：

```powershell
dotnet test tests\Unit\Exam.StudentUnitTests -c Release
```

（发布机需要 SDK 才能跑测试；只做考试部署的机器无需此步骤。）S16 的整机恢复
演练（退出恢复、换账号、全新部署恢复）以本发布包为对象，由 T 阶段执行并记录。

## 文件清单

| 文件 | 用途 |
| --- | --- |
| `publish-student.ps1` | 自包含发布 + 布局校验 + 溯源 + 打包 |
| `smoke-test-student.ps1` | 发布产物隔离启动冒烟（三场景） |
| `server.example.json` | 部署配置模板（占位值，不含真实地址/凭据） |
| `server.public.example.json` | 公网 HTTPS 与原主备节点的对应关系模板 |
| `server.public.single.example.json` | 仅一个已批准公网入口的配置模板（合成样例） |
| `README.md` | 本说明（随包复制，供监考/部署人员查阅） |
