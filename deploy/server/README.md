# 服务器部署与跨设备连接

考务端运行在 Windows 办公电脑上，通过 HTTPS 连接服务器。业务服务默认使用 443 端口，考务登录页填写 IP 或域名即可。登录后直接进入“服务器管理”，当前业务令牌用于安全换取管理会话，无须再次输入账号和密码。服务器管理仍由独立的 `Exam.Management` 进程提供，业务停止时可从考务登录页进入独立服务器管理，以原账号连接备机并执行方案 A 接管；一般启停继续由管理 API、CLI 或服务器运维工具处理。

这份说明用于准备真实服务器部署。示例中的域名、节点标识和路径都需要替换。安装脚本注册服务后不会启动服务，也不会创建数据库或导入旧测试数据；初次空主库可按下述显式初始化步骤建立权威状态。

## 平台与文件

| 项目 | Windows 服务器 | Linux 服务器 |
| --- | --- | --- |
| 考试业务 HTTPS | 支持 | 支持 |
| 状态、状态检测、配置、日志 | 支持，GUI跟随考务登录 | 支持，认证需要 PostgreSQL 可达 |
| 业务服务启停，使用管理API或CLI | Windows 服务 | systemd 服务 |
| 备份 | PostgreSQL Windows 工具 | PostgreSQL Linux 工具 |
| 管理账号离线缓存 | Windows 本机加密缓存 | 不保存离线缓存 |
| 隔离恢复、接管、重建、恢复保护 | 使用现有 Windows 部署流程 | 当前管理宿主不提供这些操作 |
| 考务端、学生端和考务 CLI | Windows | 在独立 Windows 电脑上运行 |

Linux 基础业务同样支持本机持久确认和自动补齐复制；单节点时不要求另一台确认。正式数据库复制、节点身份和持久确认仍须按[PostgreSQL 部署基线](../postgresql/README.md)准备；其中现有 Windows 归档、恢复和主备维护脚本不能直接拿到 Linux 执行。当前没有真实 Linux 主机验收记录。

在装有项目指定 .NET SDK 的构建电脑上发布服务器程序。输出使用一个新目录。

```powershell
pwsh -File deploy/server/Publish-Server.ps1 -OutputDirectory "D:\ExamRelease\windows" -Runtime win-x64
pwsh -File deploy/server/Publish-Server.ps1 -OutputDirectory "D:\ExamRelease\linux" -Runtime linux-x64
```

输出包含 `Exam.Server` 和 `Exam.Management` 两个目录。运行包自带 .NET 运行时，目标服务器还需要 PostgreSQL 及相应平台的系统依赖。Linux 发布目录传到服务器后，为两个无扩展名的程序赋予执行权限。考务端和学生端使用各自的 Windows 发布包，学生端部署见[学生端说明](../student/README.md)。

## 配置实际连接

Windows 从以下三个模板开始准备。

- [业务配置](../../samples/service-management/appsettings.business.example.json)放在 `Exam.Server/appsettings.json`。
- [管理配置](../../samples/service-management/appsettings.management.example.json)放在 `Exam.Management/appsettings.json`。
- [服务配置](../../samples/service-management/service-configuration.example.json)放到两个宿主共同指定的 `Management:ServiceConfigurationPath`。

Linux 对应使用本目录的 [业务配置](appsettings.business.linux.example.json)、[管理配置](appsettings.management.linux.example.json)和[服务配置](service-configuration.linux.example.json)。Linux 例子采用 `/srv/exam`，PostgreSQL 工具目录要按实际安装位置调整。

这些文件只给出监听、服务和目录接线。还需在受保护的部署文件或服务环境中补齐数据库、节点确认、容量和学生主备连接配置。`appsettings.json` 不会自动合并别的示例文件，使用时要把所需节合并进去。

| 配置 | 应当填写的内容 |
| --- | --- |
| `ConnectionStrings:Exam` | 当前业务节点的真实 PostgreSQL 连接串，管理宿主也需要它认证考务账号 |
| `ExamServer:ServerId/NodeId/ServerEpoch` | 与数据库中已初始化的权威节点状态一致的服务器、节点和代次 |
| `Management:ServerId/NodeId` | 与这台服务器的业务身份一致 |
| `HighAvailability:RequiredNodeIds` | 一个或两个不同受管节点，包含本机，与发布公钥和学生信任拓扑一致 |
| `Database`、`RequestCapacity` | 参考[容量配置](../server-capacity.example.json)，按实际 PostgreSQL 连接预算填写 |
| `Confirmation` | 本机独立确认目录必需；双机时另配主备标识、对端 HTTPS 地址及受保护的 `PeerToken` |
| `StudentFailover:Nodes` | 使用可从学生机访问的真实地址、443 端口及实际证书信任值，参考[主备拓扑模板](../student-failover.example.json) |
| `Backup` | 工具、资源、受保护材料和备份目录；管理状态检测还使用已选定备份的目录及标识 |

Windows 主备管理的详细配置继续参照[主备配置模板](../../samples/service-management/appsettings.high-availability.example.json)和[恢复运行手册](../../docs/backup-and-failover-runbook.md)。新数据库的迁移、初始节点状态和复制准备必须在业务开放前完成，不能直接复用旧调试宿主生成的状态。

业务配置与管理配置在同一台服务器上共用服务配置文件、业务日志路径及节点证书。所有数据、备份、资源和证书路径均指服务器本身。考务端会把 Windows 盘符路径、UNC 路径或 Linux 绝对路径传给服务器，不在办公电脑上创建这些目录。

## HTTPS 与端口

业务服务的 `service.json` 设置 `httpsPort` 为 `443`。`bindAddress` 填服务器网卡 IP，或用 `0.0.0.0` 监听所有 IPv4 网卡。管理服务单独设置 `Management:BindAddress` 和 `Management:HttpsPort`，例子使用 `9443`。未配置管理监听地址时仍只监听本机。

业务和管理使用不同端口或不同监听 IP。同一 IP 上不要让两个独立进程同时占用 443。管理端口写入部署连接文件，正常登录后复用会话；故障接管可在登录页直接进入管理服务并使用原考务账号独立登录。考务业务登录默认 HTTPS 443；非标准业务端口仍可由部署人员提供完整地址，例如 `https://exam.example.invalid:8443/`。

考务端和服务器都保留正常 HTTPS 证书验证。证书中的域名或 IP 要和实际访问地址相符。使用内部 CA 时，在考务电脑及需要访问对端 HTTPS 的服务器服务账户环境中部署信任链。

| 用途 | 证书配置 |
| --- | --- |
| 业务 HTTPS | `Kestrel:Certificates:Default:Path` 和 `Password` |
| 管理 HTTPS | `Management:CertificatePath` 和 `CertificatePassword`；Windows 也支持 `CertificateThumbprint` |
| 节点题包解密 | `PackageSecurity:CertificatePath` 和 `CertificatePassword`；Windows 也支持原有证书指纹方式 |
| 已配置节点的发布公钥 | `PackageSecurity:RecipientCertificatePaths:<节点标识>` 指向 CER 或 PEM 公钥；Windows 原有 `RecipientCertificates` 指纹配置继续可用 |

配置了证书文件时优先从文件加载；同时提供指纹时要求匹配。节点题包证书需要 RSA 私钥，公钥发布配置要覆盖已配置的所有受管节点：单台为一个，双机为两个。HTTPS 证书和题包证书可分别配置。密码和私钥只保存在服务器受保护位置，不随考务连接文件分发。

`Management:BusinessReadyEndpoint` 要指向这台服务器业务证书覆盖的地址，例如 `https://exam-primary.example.invalid/health/ready`。管理程序在服务器上访问它，因此这个域名也要能从服务器自身正确解析。业务使用共享服务配置时，不再额外设置 `Urls`、`HTTP_PORTS`、`HTTPS_PORTS` 或 `Kestrel:Endpoints`。

防火墙允许考务机和学生机访问业务 HTTPS；管理端口按实际考务机或管理网段开放。PostgreSQL 与节点间确认通信按原主备部署规则开放。`localhost` 永远指发起连接的电脑，跨设备连接文件不能填写办公电脑自身的回环地址。

## Windows 服务安装

准备好配置、服务器目录、证书和服务账户后，在服务器的管理员 PowerShell 中注册两个服务。

```powershell
$serviceCredential = Get-Credential
pwsh -File deploy/server/Install-WindowsServices.ps1 -DeploymentDirectory "C:\Exam" -ServiceCredential $serviceCredential
```

脚本从两个 `appsettings.json` 读取服务名称，业务服务设为手动启动，管理服务设为自动启动，并授予指定账户查询、启动和停止该业务服务的权限。服务账户还需具有“作为服务登录”权限，并能读取相应证书、连接配置和资源，写入数据、日志与操作记录目录。若实际部署选用 LocalSystem，安装时显式传 `-UseLocalSystem`。

脚本拒绝覆盖已有同名服务。升级已有部署时先停止相关服务，再更换程序文件并保留实际配置、证书和数据，不向现有部署目录重新发布默认配置。

首次在新建的空主数据库完成迁移，选定首台设置 HighAvailability:BootstrapPrimary=true、ExamServer:ServerEpoch=1，并配置一个或两个受管节点。业务启动以本机持久事务建立身份，再完成第一个管理员账号注册；已有环境不重新初始化。初始化成功后关闭 BootstrapPrimary 和迁移开关。随后启动独立管理服务并配置选定备份，业务启停继续使用管理 CLI 或服务器运维工具，考务 GUI 不提供启停按钮。已有部署继续使用原账号与数据。管理启动检查要求真实备份证据，空库首次部署也应建立初始备份后配置 `Backup:StartupVerificationDirectory` 和 `StartupVerificationBackupId`。

## Linux 服务安装

将 Linux 发布包放到服务器，创建专用非 root 账户并准备目录读写权限，安装 systemd 与 polkit。用以下形式注册服务，参数中的账户必须已存在。

```bash
sudo bash deploy/server/install-systemd.sh /srv/exam/apps examservice huaizhou-business huaizhou-management
```

管理配置中的 `Management:BusinessServiceName` 填 `huaizhou-business.service`。脚本生成两个 unit，将管理服务设为开机启动，并为指定账户授予这一业务 unit 的启动和停止权限。业务服务不自动随系统启动。unit 允许非 root 进程绑定 HTTPS 443，工作目录设为各程序目录，配置按各自的 `appsettings.json` 加载。

Linux 管理认证直接核对数据库，业务停机期间应保持 PostgreSQL 和管理服务运行，运维人员使用 CLI 独立登录。GUI使用考务业务会话交换管理会话，退出或换账号时清除旧管理权限。隔离恢复和主备变更请求会得到明确的不支持提示，不会执行 Windows 命令。

## 把管理连接交给考务端

在具有实际管理配置的机器上导出连接文件。输出目录须已存在，域名或 IP 必须能从考务电脑访问。

```powershell
pwsh -File deploy/management/Export-AdminConnection.ps1 -ManagementConfigurationPath "C:\Exam\Exam.Management\appsettings.json" -AdminDirectory "C:\Exam\AdminConnection" -HostName "manage.exam.example.invalid"
```

生成的 `management-connection.json` 只含管理 HTTPS 地址、服务器标识和节点标识。把文件放到考务端程序旁，或在“服务器管理”页面点“导入连接配置”。更换节点或管理端口后重新导入，已有用户保存的连接优先于程序旁的文件。

## 目标设备联调

在服务器和考务电脑分开的环境中，检查以下操作即可验证本次连接改造。

1. 考务端只填写业务域名或 IP，完成注册或登录，确认实际使用 HTTPS 443。
2. 考务登录后进入“服务器管理”，导入目标节点连接，确认无须第二次登录。核对合并后的服务器状态、状态检测，以及最近和历史请求处理状态查询。
3. 使用管理 CLI 停止业务，保持管理服务和 PostgreSQL 运行，再通过 CLI 启动并确认就绪。考务界面不出现业务启停、按原内容重试或续办原恢复按钮；页面不再单列待核对请求，处理情况由操作结果展示。
4. 使用测试考试完成题目导入、学生机登录和答卷回收。目录填写目标服务器路径，确认备份实际生成于服务器。
5. 分别检查错误账号、错误证书名称和未登录管理请求被拒绝，退出或换账号后不能沿用旧管理权限；再按[验收标准](../../docs/acceptance-criteria.md)执行现场考试及双机验收。

本次代码检查不包含目标服务器安装、证书颁发、真实网络连通或 Linux 实机运行结果。部署后应记录使用的系统、节点地址、证书信任及上述操作的实际结果。


自动备份和归档保留由管理服务内部执行，部署配置见 [自动维护说明](../../eng/maintenance/README.md)。启用前填写该节点独立的维护状态目录；默认不启用自动删除。

## 单节点与接管操作

只装一台时，RequiredNodeIds、HighAvailability:Nodes 和 StudentFailover:Nodes 配置本机一项，发布公钥只配置本机。Confirmation:Directory 和版本资源目录仍须可靠落盘，对端 HTTPS 地址和 PeerToken 可省略。配置双机但暂时只启动主机时，保留已部署的节点身份、公钥与拓扑，主机业务不会等待备机上线。备用依赖的公钥是预部署的本地材料，不要求在线下载对端私钥。

准备第二台时从活动节点建立 PostgreSQL 备用库，并准备各自证书、相同版本资源和受信节点连接。普通备用失联后自动追平；发生接管的旧主保持隔离，通过管理重建加入。醒目的接管按钮只提升已核实的本机备库，须先真实完成旧主断电或全部业务网络隔离。界面确认不会自动隔离设备。接管会启动新主业务并等待就绪，不需要再找单机应急或另行启动业务按钮。

升级已有部署时更新两台程序及 PostgreSQL 通用配置，保持本机持久提交和 fsync，保留所有数据库、考试、确认日志及部署选择；不清库、不导入调试数据库。暂时不能升级另一台时，健康活动节点继续单节点服务，待对端版本和资源一致后恢复复制。现场操作细节见主备管理和恢复运行手册。

单节点模板见 samples/service-management/appsettings.single-node.example.json，全部为合成占位值。将相应节完整替换到宿主配置，并移除旧的备用节点条目及确认地址；不能通过追加配置源保留另一节中的旧节点。模板保留 BootstrapPrimary=false，只有全新空库首次初始化才按上述步骤显式启用。数据库、证书、容量、共享服务配置及备份设置仍按本说明填写，真实配置保存在源码之外。
