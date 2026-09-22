# AGENTS.md

本文件给在本仓库工作的编码 Agent 写入必须遵守的既有团队规则。规则来源为 `CONTRIBUTING.md`、
`development-plan.md`、`agent-implementation-guide.md` 与团队约定；**本文件不新增政策**，
冲突时以那三份文档与 A 的决定为准。Agent 每次开工前先读本文件。

## 分支与 PR

- **PR 基线必须是 GitHub 上 `main` 的当前最新版本。** 开工前 `git fetch origin`，
  从 `origin/main` 建分支（或先 `git rebase origin/main`）。不得以本地旧提交或他人未合并分支为基线。
- **可以提 PR，但不得自行合并进 `main`，也不得直接推送 `main`。** 合并由 A 决定。
- 分支命名 `codex/<任务编号小写>-简短英文说明`，例如 `codex/d01-tex-spec`。
  一条分支只处理一个可独立审查的目标，不维护长期个人分支，不强制推送共享分支。
- 提交信息用中文陈述行为变化：新功能首个提交形如 `D01: 逐题规范与合成样例（三题型 schema 与错误样包）`；
  修正类以「修正…」开头。不用 emoji，不用 `feat:` / `fix:` 前缀。
- PR 必须按 `.github/pull_request_template.md` 关联任务编号与 Issue，列明输入输出、运行方式、
  **实际执行的检查及结果**、已知限制、恢复或回退考虑与需要的非作者审查人。

## 必须由 A 确认的变更

授权、交卷持久化、主备、**计分**、密钥与**数据库迁移**属关键变更，除分工内的非作者复核外还需 A 确认。
`src/Exam.Contracts`、`src/Exam.Server` 与两处 Application 目录在 `.github/CODEOWNERS` 中归 A，
改动前先确认。公共契约、schema 语义或迁移有变化时，同步修改样例与受影响文档。

## 依赖与构建

- `Directory.Build.props` 启用包锁文件，CI 以 `--locked-mode` 还原；**新增任何 NuGet 包都必须同步提交
  `packages.lock.json`**，且版本必须精确（`Directory.Build.targets` 禁止浮动版本号）。
- 交付前运行 `CONTRIBUTING.md` 的三条本地检查，Release 构建要求 **0 警告、0 错误**：
  ```powershell
  dotnet restore Huaizhou.ExamSystem.sln --locked-mode
  dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release
  dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release
  ```
- 需要外部重型依赖（PostgreSQL、TeX 等）的测试必须加 `[Trait("Category", …)]`，
  以免污染 CI 现有两条流水线（windows 的 `Category!=Postgres` 与 ubuntu 的 `Category=Postgres`）。
  **CI 上没有 TeX 发行版**，编译相关检查必须在单独类别内。

## 数据边界（违反会造成事故）

仓库只能含源码、脱敏配置模板、依赖锁、迁移、测试、**合成样包**、构建脚本与说明。**绝不提交**：

- 真实试题、参考答案、解析及其专用资源；
- 账号、名册、答卷、成绩、数据库与备份；
- 证书、私钥、密钥、口令、`.env` 与本机日志；
- `bin`、`obj`、发布包等可重建产物。

真实试卷材料在仓库**外**（`../试题卷及参考答案/`）。**不得把上级目录中的试卷材料复制进仓库**，
也不得把真实试题或个人数据上传给外部模型。样例只用合成材料并在文档中说明来源。
发现凭据已提交时立即停止推送、轮换凭据并在 Issue 记录，未经决定不得重写共享历史。

## 如实报告

- 测试必须**实跑**，如实记录命令、环境与结果；**未执行的检查写清原因，不得写成已通过**。
- 自动检查通过只代表当前代码检查通过，不代表真实设备、网络、主备接管、断电恢复或 300 人考试已验收。
- 占位接口、硬编码账号与静态页面不算业务完成。缺公共约定时报给 A 协调，不猜接口、不绕过校验。
- 不修改他人主责文件（分支命名、命令注册、他人模块的实现），必要时在 Issue 或 PR 中提出。
