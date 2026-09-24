# C05 清单静态预检交付记录（2026-09-24）

基线为已核对的 GitHub `main` 提交 `61c7c383ce35bc2e65f818c7965c99b3c88a7eee`（PR #44 已合并）。
新分支从该提交建立，仅移植旧 C 分支未入主线的 C05–C07 页面及测试，再新增本任务的 C05 清单预检。
D02 PR #45 当时仍开放，未把其未合并能力作为正式依赖。

## 可用能力与安全边界

- 考务端 C05 可选择本地 `manifest.json`，调用 `Exam.Domain.Importing.ExamManifestValidator.ValidateJson`。
  错误列表显示 D01 的稳定 `Rule`、RFC 6901 `Location` 和 `Message`；不复制 D01 校验规则。
- 选择文件限 10 MB、严格 UTF-8、文件名为 `manifest.json`；不选择、读取或显示独立答案内容。
  文件读取失败只给概括性提示，不显示本机路径或文件内容。
- 清单考试 ID 必须与 C05 当前考试 ID 一致。更换考试或服务器连接会清除旧预检结果，迟到的文件读取结果不能恢复旧考试状态；重复预检不累加旧错误。
- 即使清单静态校验通过，也始终提示独立答案关联与逐题满分核对未完成，更不表示 D02 依赖检查/XeLaTeX 编译、D03 选项定位、D04 全部作答情形计分校验或正式发布通过。
- 原有“逐题导入”“编译预览”“发布修订”按钮继续禁用。C06 正式报告与导出、C07 主备接管等缺接口操作继续禁用；C 端不计算正式成绩、不实现主备状态机。

## 验证

合成样例覆盖合法最小包、错误题型、非法计分表达式、无效 JSON、缺失字段、切换考试、重复操作、拒绝答案文件，以及实际 WPF 窗口的按钮绑定。C01–C04 原有回归保留。

实际执行：

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release
dotnet test tests/Unit/Exam.AdminUnitTests/Exam.AdminUnitTests.csproj --no-build --configuration Release
dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release --filter "Category!=Postgres"
```

锁定还原成功；Release 构建 0 错误、14 条 `NU1900` 警告；考务端 88/88，通过；
全套非 PostgreSQL 测试在有 Windows 用户配置文件的授权上下文中 884/884 通过。
受限沙箱首次运行曾有 31 项学生端/身份测试因 DPAPI `CryptographicException` 失败；
同一提交在授权用户上下文复测全部通过，未修改 B 模块或关闭测试。
NuGet 漏洞数据源不可达，**依赖安全审计未完成**；没有关闭审计或增加 NuGet 包。

本机没有 `EXAM_TEST_POSTGRES`，未发现 PostgreSQL 服务、`psql` 或 `docker`；
PostgreSQL 专项和 Exam.Server + Exam.Admin 的持久化业务冒烟均为 **BLOCKED**。
WPF 自动测试验证窗口启动与绑定，不等于人工 GUI、真实材料或 300 台设备验收。

## 正式依赖

- D、A：正式逐题导入任务、依赖检查/编译与预览、内容修订、发布门和回执、D05 规则重算。
  需要任务 ID、阶段/进度、逐题错误位置、预览产物、`expectedVersion`、
  `paperVersion/gradingVersion`、发布回执和逐客户端安装/复核状态；失败不得发布半份题包。
- D02 PR #45 尚未合并，其检查器及未来接口只能用于兼容性分析，不接入正式运行链路。
- A、D、E：C06 报告读取与同版本导出、服务离线管理接口；A、E：C07 受保护的 HA 状态和操作接口。
  详细建议契约见 `docs/c-afternoon-integration-readiness.md`，其中路径均是建议而非已存在 API。
