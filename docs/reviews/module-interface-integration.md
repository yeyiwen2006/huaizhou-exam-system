# 模块衔接修正记录

本次由 A 独立完成，接续 PR #50、#51。用户确认同时修复 A05 与 D04 的答卷约定、D01 与 D04 的规则组装、D03 与学生端的包读取，并要求在 AGENTS.md 固定每次修改及合并前的接口检查要求。

原问题是学生会话只记录编辑过的题目，收卷入口接受稀疏快照，而 D04 要求每题都有记录。D03 的清单也没有明确绑定 PDF 和试卷版本，学生运行时未读取包内题目。计分发布仅依赖调用方标志，没有实际执行导入与计分校验。

修正后，新答卷按验证后的清单初始化完整 Empty，收卷对照已发布结构验证，计分读取固定答卷和原回执。学生包使用专用清单，保留题组顺序并剥离源路径及计分规则。发布入口通过 ImportedGradingRules 执行 D01 与 D04 检查，核对考试和试卷结构。详细格式、旧数据边界及恢复约定见 [模块接口文档](../module-interface-integration.md)。

代码审查覆盖生产注册、学生运行时真实调用、重复交卷、授权停止、SQLite 恢复、版本不可变约定及 PostgreSQL 事件重放。审查补充了附加资源路径大小写、ZIP 总容量和题组顺序处理。拒绝路径测试还发现 InvalidDataException 未纳入学生解析失败处理，已修复并验证损坏题包不会生成空答卷。没有覆盖原工作区，没有修改参考答案或已有固定答卷。

## 本地实跑

环境为 Windows、.NET SDK 10.0.401、TeX Live 2025，材料全部合成。

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release
dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release --filter 'Category!=Postgres'
```

还原成功，Release 构建 0 警告、0 错误。1090 项通过，其中领域及基础设施 841、学生端 92、管理端 76、学生身份 25、集成 34、负载工具 22。包含真实 TeX 编译后经学生读取器打开两题 PDF。

随后补充容量限制、题组顺序、schema 和合成格式样例，重新构建仍为 0 警告、0 错误；与变化有关的 Packaging、SchemaConsistency、StudentExamRuntimeTests 共 49 项通过。未重复无关测试。最终提交的完整 Windows 和 PostgreSQL 检查由 PR CI 提供。

新增数据库用例验证带导入源数据的发布在服务器重启和后续命令重放后仍保留版本与 AllowAbstain，快照不带参考答案。本地未连接 PostgreSQL，以数据库 CI 的实际结果为准。无 GUI 画面变更，未进行 GUI、真实考生、断电、多机或 300 人现场验收。Issue #26、#27 继续保持打开，未把本次模块接线当作 T01/T02 完成。
