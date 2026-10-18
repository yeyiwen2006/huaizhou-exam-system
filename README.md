# 怀籀传承在线考试系统

面向学生和考务人员的考试系统，支持考试配置、名册与试场管理、逐题导入、原版试题显示、答卷保存和回收、精确计分、五类成绩表及主备恢复。学生端和考务端运行于 Windows，服务器以 Windows 部署为主，并提供 Linux 基础运行配置。

## 部署入口

双击根目录的 `生成部署包.cmd`，统一生成服务器、考务端和学生端三个 Windows x64 部署包。输出在 `artifacts/deployment/日期时间`，分别对应 `server`、`admin`、`student`。每个包附带部署说明，程序自带 .NET 运行时。跨设备部署的路径、启动入口和连接方式见[部署指南](部署指南.md)。

## 使用入口

学生使用 `Exam.Student` 填写服务器地址和考生信息后直接登录，完成等待、作答和交卷。学生端和考务端 `Exam.Admin` 均只需填写服务器 IP 或域名，默认连接 HTTPS 443；也支持包含自定义端口的完整 HTTPS 地址。考务端登录后可直接进入“服务器管理”，沿用当前考务身份，无须再次输入账号和密码。业务故障时，登录页“服务器管理 / 故障接管”可直接使用原考务账号登录备机管理服务。`Exam.Server` 承担考试业务，`Exam.Management` 提供独立服务器管理。业务服务启停由部署人员通过正式管理 API 或 `Exam.Cli` 完成。考务界面分别刷新主机与备机的本机检查，提供状态检测、日志和故障接管；原请求由后台继续核对，人工查询使用管理 API 或 CLI。

[系统需求](requirements.md)定义业务规则；[导入规范](docs/import-package-spec.md)说明题目材料；[服务器部署](deploy/server/README.md)、[学生端部署](deploy/student/README.md)、[PostgreSQL 部署](deploy/postgresql/README.md)和[服务器管理](docs/service-management.md)说明运行准备。考务命令见[CLI 使用](docs/cli-usage.md)，备份与接管见[运行手册](docs/backup-and-failover-runbook.md)。

## 单节点与故障接管

一个健康活动节点即可运行正式考试。备机缺席时自动以本机可靠落盘返回成功，并显示单节点状态；双机恢复后继续补齐复制。主机故障采用方案 A：先实际关闭旧主或隔离其全部业务网络，再在备机服务器管理点击“接管为主服务器”。接管完成后自动启动业务，学生沿用原身份、答案和提交标识重连。旧主回归必须保持隔离并重建为备机。详见[主备管理](docs/high-availability-management.md)。

## 运行依赖

| 设备 | 需要准备的依赖 |
| --- | --- |
| 考务电脑 | Windows。编译或重新编译 TeX 试题时，需要预先安装带 XeLaTeX 的 TeX 发行版（TeX Live 或 MiKTeX），并将其可执行文件目录加入 `PATH`，使程序能找到 `xelatex`。题目使用的字体、宏包和图片也须齐备。仅管理考试、查看成绩或导入完整考试归档时不调用 XeLaTeX。 |
| 服务器 | 可访问的 PostgreSQL 18 数据库，并完成数据库、HTTPS 证书、题包证书及服务配置。执行备份、恢复或主备运维的服务器还需安装对应 PostgreSQL 18 工具，例如 `pg_basebackup`、`pg_verifybackup`、`pg_ctl`，并在部署配置中填写工具目录。服务器处理已编译的题包，无须安装 XeLaTeX。 |
| 学生电脑 | Windows 10 19041 及以上或 Windows 11，使用与系统架构匹配的学生端发布包。无需安装 XeLaTeX、PostgreSQL 或 Office。 |

**.NET 是否需要另装取决于发布方式。** 自包含发布包自带 .NET 运行时；仓库的服务器和学生端发布脚本采用这种方式。使用普通构建产物或非自包含发布包时，考务端、学生端需要 .NET 10 Desktop Runtime，业务服务和独立管理服务需要 ASP.NET Core Runtime 10 及其所需的 .NET Runtime 10。Windows 安装包的区别见[微软运行时安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/windows)。Linux 服务器即使使用自包含包，也需要相应发行版的系统库，例如 ICU 和 OpenSSL，具体包名见[微软 Linux 安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/linux)。发布方式说明见[微软 .NET 部署文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/)。

考务端的名册和成绩 Excel 文件由程序自带库读写，无须预先安装 Microsoft Excel。仅构建或发布源码的电脑需要安装 `global.json` 指定的 .NET SDK；使用 `pwsh` 执行部署脚本时，还需要 PowerShell 7。

## 构建与检查

使用 `global.json` 固定的 .NET SDK：

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release
```

正式目录及 GitHub 只包含正式源码；根解决方案和远端流程只构建正式项目。测试工具、测试计划和完整测试解决方案仅保留在本地 exam-system-test 目录，具体位置及同步规则见[本地同步说明](docs/local-debug.md)。测试输出保存在测试目录的忽略目录中，原有报告及同步备份继续保留。

[系统架构](docs/architecture.md)、[数据契约](docs/system-contracts.md)和[界面规范](docs/gui-spec.md)供维护与故障排查使用。真实试卷、答案、考生资料、连接配置及私钥不提交仓库。
