# D02 实施状态

本文记录 D02「依赖与编译」的交付范围与验证情况，范围为 PR #45（第一轮：静态检查）与本次
（第二轮：实际编译与文件系统检查）。依据 `requirements.md`「逐题导入、原版显示与加密」、
`agent-implementation-guide.md` 第 164 行（依赖检查与独立目录编译）、238 行（D02 任务卡）、
262 行（编译属资源与长任务）与 [`docs/import-package-spec.md`](import-package-spec.md)。

## 逐项实现

| 项目 | 当前实现 |
| --- | --- |
| 依赖检查（源侧） | `TeXSourceInspector`：文档类、宏包、字体、`\graphicspath`、图片引用；展开宏体含 `\includegraphics` 的自定义图片宏；忽略注释与宏定义占位符 |
| 依赖检查（文件侧） | `PackageContentChecker`：按 `\graphicspath` 顺序解析图片、逐题源文件、资源目录；路径越界与**重解析点**（符号链接/目录联接）一律不接受 |
| 依赖检查（编译侧） | `XeLaTeXLogReader`：从日志定位缺宏包、缺字体、缺图片，带文件名与行号；连带失败标为 Cascade 不计入阻断性问题 |
| 独立目录编译 | `XeLaTeXCompiler`：以**包根目录为工作目录**（相对路径与 `\graphicspath` 才按包内结构解析），产物写独立目录，**材料目录零改动** |
| 禁外部命令 | 参数层 `-no-shell-escape`，MiKTeX 另加 `-disable-write18`；源文件静态检查拒绝 `\write18`、`\ShellEscape`、`\input\|` 与需要外部命令的宏包（minted 等） |
| 超时与资源上限 | 单次编译时限（默认 120 秒，可配，上限 6 小时），超时杀进程树；日志读取上限 8 MiB；进程输出捕获上限沿用既有 runner |
| 路径校验 | 复用 `PathBoundary`：越出包根、经过重解析点的路径一律拒绝；产物目录不得位于包根之内 |
| 可定位错误 | 每条问题给出稳定类别 + 文件名 + 行号 + 中文说明；缺图片给出尝试过的候选路径 |
| 成功判定 | **不看退出码**：必须「未超时 + 退出码 0 + 日志无阻断性问题 + 确实写出 PDF」 |
| 发行版差异 | MiKTeX 需显式关闭包安装器（否则会联网取依赖）；TeX Live 不需要也不接受该参数 |

## 测试范围

| 测试类 | 数量 | 是否需要 TeX |
| --- | --- | --- |
| `XeLaTeXInvocationTests`（参数与成功判定） | 12 | 否 |
| `XeLaTeXCompilerTests`（用替身进程运行器） | 9 | 否 |
| `PackageContentCheckerTests`（临时目录；符号链接用例在无权限时跳过） | 10 | 否 |
| `XeLaTeXLogTests`、`TeXSourceInspectorTests`（第一轮） | 32 | 否 |
| `XeLaTeXRealToolchainTests`（**`Category=Latex`**） | 5 | 是 |

不需要 TeX 的 63 项随现有流水线执行；需要 TeX 的 5 项以 `Category=Latex` 标记并在 CI 中排除
（运行机没有 TeX 发行版），由本机单独执行。本机没有 XeLaTeX 时这 5 项**明确失败而不是跳过**。

## 本机验证

Windows 11，.NET SDK 10.0.401，MiKTeX 26.5 / XeTeX 4.18，`--configuration Release`：

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore -c Release      # 0 警告 0 错误
dotnet test Huaizhou.ExamSystem.sln --no-build -c Release --filter "Category!=Postgres&Category!=Latex"
# 935 项通过：Exam.UnitTests 700、学生端 89、管理端 76、身份 25、集成 23、负载基础 22
dotnet test tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj --no-build -c Release --filter "Category=Latex"
# 5 项通过（真实 XeLaTeX）
```

`Category=Latex` 的 5 项覆盖：自造最小包（含自定义图片宏与 `\graphicspath` 子目录）**编译成功且材料目录零改动**、
**整包换目录后仍能找到图片**、缺图片**定位到文件名与行号**、缺字体**定位且不静默替换**、
源文件里的 `\write18` **确实没有执行**（命令本应写的标记文件不存在）且仍判为不通过。

真实材料上的核对（材料在仓库外、只读；编译产物写在系统临时目录）：
16 份真实卷子的编译成功日志零误报；真实「缺图片」日志精确定位 6 张图片及行号（160/172/211/222/226/251）。

## 尚未完成及归属

- **预览产物查询与 GUI/CLI 入口**：导入预览、逐题错误展示、编译命令的 GUI 与 CLI 由 C05 与 E 的框架提供；
  本任务只交付可被它们调用的服务。requirements.md 要求「依赖检查、编译、预览产物查询和发布均提供 GUI 及 CLI」，
  该条**尚未满足**。
- **TeX Live 未经实测**：参数分支按发行版差异实现并有单元测试，但本机只验过 MiKTeX；换发行版需重新核对日志措辞。
- **样式文件的静态解析未做**：`styles/*.sty` 里再引用别的宏包时，静态检查看不到，靠编译日志兜底。
- **未做签名或来源校验**：导入包不做完整性校验，也不限制 ZIP 之外的来源形式。
- **D03/D04/D05**：选项定位与学生包、表达式求值与发布前枚举、规则版本与重算仍按各自任务跟踪。
- **交叉审查**：按分工由 B 核对版式、A 审查执行边界、E 独立核算；本任务尚未完成交叉审查。

以上是本机实现与自动化测试证据，**不等于**三校真实试卷的版式比对、300 人规模或现场环境通过。
`Category=Latex` 的结论只在本机 MiKTeX 下成立；任何未列为完成的场景不得写成已通过。
