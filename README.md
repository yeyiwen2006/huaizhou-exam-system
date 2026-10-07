# 怀籀传承在线考试系统

面向学生和考务人员的 Windows 考试系统，支持考试配置、名册与试场管理、逐题导入、原版试题显示、答卷保存和回收、精确计分、五类成绩表及主备恢复。

## 使用入口

学生使用 `Exam.Student` 完成设备激活、身份确认、等待、作答和交卷。考务人员使用 `Exam.Admin` 管理考试与成绩；`Exam.Cli` 提供对应命令。`Exam.Server` 承担考试业务，`Exam.Management` 提供独立本机管理，业务服务停止时仍可查询和启停。

[系统需求](requirements.md)定义业务规则；[导入规范](docs/import-package-spec.md)说明题目材料；[学生端部署](deploy/student/README.md)、[PostgreSQL 部署](deploy/postgresql/README.md)和[本机服务管理](docs/service-management.md)说明运行准备。考务命令见[CLI 使用](docs/cli-usage.md)，备份与接管见[运行手册](docs/backup-and-failover-runbook.md)。

## 测试与运行边界

当前进入测试阶段。[测试安排](docs/testing-plan.md)和[验收标准](docs/acceptance-criteria.md)区分本机回归、真实双机和现场设备验收。测试通过范围以对应提交的实际报告为准，不把构建成功或合成负载当成正式考试已经验收。

运行包使用受保护的部署配置。正式业务服务缺少数据库或确认保护时拒绝启动，不提供测试专用状态修改接口。学生端不需要安装 SDK 或 TeX；考试材料编译在考务侧使用 XeLaTeX。

## 构建与检查

使用 `global.json` 固定的 .NET SDK：

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release
python eng/testing/run_checks.py --profile core --profile wpf --output artifacts/checks
```

数据库、真实编译、故障和发布检查见[测试工具](docs/test-infrastructure.md)。输出使用新的项目内忽略目录或系统临时目录，不覆盖已有报告。

[系统架构](docs/architecture.md)、[数据契约](docs/system-contracts.md)和[界面规范](docs/gui-spec.md)供维护与故障排查使用。真实试卷、答案、考生资料、连接配置及私钥不提交仓库。
