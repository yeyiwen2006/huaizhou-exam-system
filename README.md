# 怀籀传承在线考试系统

本仓库用于开发 Windows 局域网在线考试系统。首版技术栈为 .NET 10、WPF、ASP.NET Core、SignalR 与 PostgreSQL 18；学生端本地状态使用 SQLite。完整业务行为以 [需求文档](requirements.md) 为准，分工和里程碑见 [开发总计划](development-plan.md)，接口约定、任务卡和验收场景见 [Agent 执行指南](agent-implementation-guide.md)。

## 当前状态

仓库现处于 M0/A01 与 A 组服务器核心的参考实现阶段。解决方案、公共契约、核心状态机、自动检查入口和协作模板已经建立。内存实现用于验证并发、幂等和状态转换，不等同于 PostgreSQL 持久化、SignalR 可靠通知、双机复制或生产验收已经完成。逐项完成度和限制见 [A 组实施状态](docs/a-implementation-status.md)。

## 开发环境

- Windows 10/11 x64
- .NET SDK 10.0.401，版本由 `global.json` 锁定
- 后续持久化集成使用 PostgreSQL 18 的同一补丁版本
- Visual Studio、Rider 或 `dotnet` CLI

```powershell
dotnet restore Huaizhou.ExamSystem.sln
dotnet build Huaizhou.ExamSystem.sln --no-restore
dotnet test Huaizhou.ExamSystem.sln --no-build
```

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

日常集成以 `main` 为准，但不直接向远端 `main` 提交。每项任务从最新 `main` 建立 `codex/任务编号-简短说明` 分支，经测试、代码审查和 PR 检查后合并。详细流程见 [协作说明](CONTRIBUTING.md)。

本系统面向真实考试，自动检查通过只代表当前代码检查通过，不代表真实设备、网络、主备接管、断电恢复或 300 人完整考试已经验收。
