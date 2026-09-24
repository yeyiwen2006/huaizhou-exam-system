# 怀籀传承在线考试系统

本仓库用于开发 Windows 局域网在线考试系统。首版技术栈为 .NET 10、WPF、ASP.NET Core、SignalR 与 PostgreSQL 18；学生端本地状态使用 SQLite。完整业务行为以 [需求文档](requirements.md) 为准，六人分工和测试顺序见 [开发总计划](development-plan.md)，接口约定、任务卡和验收场景见 [Agent 执行指南](agent-implementation-guide.md)。

## 当前状态

仓库已经具备 A02 至 A06 的 PostgreSQL 18 持久化、正式 HTTP API、设备授权的 SignalR 持久指令补发、通用 HTTP CLI、版本化加密题包存储、基础备份/隔离恢复以及现归F02的主备控制基础设施。内存实现仅供单元测试；生产环境缺少 PostgreSQL 连接或考务令牌时会拒绝启用业务入口。逐项完成度、实测范围和仍需两台真实主机完成的限制见 [A 组实施状态](docs/a-implementation-status.md)。

当前任务与Issue对应见[任务映射](docs/task-map.md)。六人账号为A（@yeyiwen2006）、B（@takeanothernap-maker）、C（@whaler-1）、D（@huangjaiqi）、E（@WorldChangerczx）、F（@Nostalgia-cy）。

F负责服务管理、主备与恢复；D03保持由D开发；E负责测试基础设施。全部代码和真实接线完成后，全员依次执行T01单机测试及修复、T02多机联调联试及修复，T02通过后由A完成T03最终版本固定与交付。开发期间的模块测试和CI持续执行，不能后置。详见[测试安排](docs/testing-plan.md)。

## 开发环境

- Windows 10/11 x64
- .NET SDK 10.0.401，版本由 `global.json` 锁定
- PostgreSQL 18；主备部署必须使用同一补丁版本
- Visual Studio、Rider 或 `dotnet` CLI

```powershell
dotnet restore Huaizhou.ExamSystem.sln
dotnet build Huaizhou.ExamSystem.sln --no-restore
dotnet test Huaizhou.ExamSystem.sln --no-build
```

PostgreSQL 集成测试要求只在当前进程提供 `EXAM_TEST_POSTGRES`，测试会为每次运行创建并删除随机数据库，不会把连接信息写入仓库。生产运行至少通过受保护环境配置提供 `ConnectionStrings__Exam`、`ExamSecurity__OperatorToken`、与该令牌绑定的 `ExamSecurity__OperatorId` 和 `ExamServer__*`；示例和双机部署步骤见 [PostgreSQL 双机部署基线](deploy/postgresql/README.md)。

学生端和考务端项目仅在 Windows 上构建。真实试卷、答案、考生数据、数据库、证书、密钥、日志、备份和本地发布产物不得提交。

## 项目边界

- `src/Exam.Contracts`：跨端请求、响应、事件、错误码及版本字段。
- `src/Exam.Domain`：不访问文件、网络和数据库的状态与规则。
- `src/Exam.Application`：应用服务、权限和事务边界。
- `src/Exam.Infrastructure`：数据库、文件、事件存储、加密和导出适配。
- `src/Exam.Server`：API、SignalR、定时与后台入口。
- `src/Exam.Desktop.Shared`：学生端和考务端共同的 WPF 主题与组件。
- `src/Exam.Student`、`src/Exam.Admin`、`src/Exam.Cli`：三类用户入口。
- `tests`：单元、集成与负载验证入口。
- `samples`：只能存放独立合成材料。

## 协作

日常集成以 `main` 为准，但不直接向远端 `main` 提交。每项任务从最新 `main` 建立 `codex/任务编号-简短说明` 分支，开发期间持续模块测试，经A代码审查及PR检查后由A合并。详细流程见 [协作说明](CONTRIBUTING.md)。

本系统面向真实考试，自动检查通过只代表当前代码检查通过，不代表真实设备、网络、主备接管、断电恢复或 300 人完整考试已经验收。
