# D01 实施状态

本文记录 D01「逐题规范与合成样例」的交付范围与验证情况，范围为 PR #41（schema、合成样包、规范文档）
与本次的导入校验器 PR。依据 `requirements.md`「逐题导入、原版显示与加密」「考试配置与计分」、
`agent-implementation-guide.md` 第 62、145、151-157、237 行与
[`docs/import-package-spec.md`](import-package-spec.md)。

## 逐项实现

| 项目 | 当前实现 |
| --- | --- |
| 清单与独立答案 schema | 四份：`exam-manifest`、`scoring-rules`、`display-metadata`、`answer-key`；`additionalProperties:false` 贯穿，按题型用 `if/then` 强制计分分支 |
| 显示元数据 | 首次定义：`anchorScheme`（固定 `pageTopLeftNormalizedV1`）、`optionCaptions`、`pageCount`；坐标产物属 D03，不在清单内 |
| 计分表达式 | 专用解析器：文法、两个中文变量白名单、常量部分的除零与有限小数判定；**含变量的求值留 D04** |
| 精确分数 | `ExactRational`：BigInteger 分子分母、既约、有限小数转字符串（整数不带小数点） |
| 清单校验 | 26 条规则与 `samples/synthetic/errors/expected-errors.json` 逐一对应，另有 6 条防御性规则 |
| 满分求和 | 逐题 → 题组 → 科目；无法静态判定时置 `RequiresAnswerKeyArithmetic`，由 D04/D05 拿答案后核对 |
| 命名与路径 | 包内相对路径、Windows 文件名规则（**不使用 `Path.GetInvalidFileNameChars()`**）、`考试名称_题号.tex` |
| 合成样例 | 4 套合法样包 + 26 个错误样包；错误样包已成为可执行验收清单 |
| 校验结果定位 | 稳定规则标识 + RFC 6901 JSON Pointer；传输层沿用既有错误码 `IMPORT_INVALID`，未新增错误码 |

## 测试范围

全部落在 `tests/Unit/Exam.UnitTests/Importing`，不依赖 PostgreSQL 或 TeX，随现有
`Category!=Postgres` 流水线执行，共 **232** 项（含 A 在 `0fe0188` 已合并的 2 项）：

| 测试类 | 覆盖 |
| --- | --- |
| `ScoreExpressionTests` | 常量表达式精确值与 0.1+0.2=0.3、变量表达式、未知变量与位置、14 类文法错误、常量除零、有限小数、长度与嵌套上限、按变量求值 |
| `ExactRationalTests` | 有限小数判定与格式化、既约与符号规范化、除零、精确加法 |
| `NamingRulesTests` | 与 schema 正则逐例等价的路径判定、Windows 文件名规则、`考试名称_题号.tex` 解析、满分常量规则与 schema 正则一致 |
| `ManifestValidatorTests` | 错误样包未覆盖的规则：必填缺失、科目标识与题组标识重复、空选项、未知坐标规范、显示文本长度、题组满分不符、答案 schema 版本；含变量满分的正反例；JSON 解析与字段类型错误 |
| `SamplePackageTests` | 4 套合法样包必须通过且不需答案；26 个错误样包必须只报出声明的规则与位置；目录与期望清单一一对应；含变量样包缺答案时置位标志 |
| `ContractSchemaAlignmentTests` | schema 与 C# 契约对齐：DTO 属性名、题型枚举、变量白名单、显示元数据形状与 `anchorScheme` 常量；所有对象 schema 都禁止未定义字段 |
| `SchemaConsistencyTests`（A 已合并） | 单独发布的 schema 与清单内嵌定义逐项一致；样例 JSON 可解析；错误样包为 26 例 |

## 本机验证

环境：Windows 11，.NET SDK **10.0.401**，MiKTeX 26.5 / XeTeX 4.18，`--configuration Release`，
基线为合并 PR #41、#42、#43 之后的 `main`。

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode   # 成功，包锁文件未变
dotnet build  Huaizhou.ExamSystem.sln --no-restore -c Release   # 0 警告 0 错误
dotnet test   Huaizhou.ExamSystem.sln --no-build -c Release --filter "Category!=Postgres"
# 全部通过，共 570 项，失败 0：Exam.UnitTests 335、Exam.StudentUnitTests 89、Exam.AdminUnitTests 76、
# Exam.StudentIdentityTests 25、Exam.IntegrationTests 23、Exam.LoadTests.Tests 22
```

单独统计 D 组：`dotnet test --filter "FullyQualifiedName~Exam.UnitTests.Importing"` → 232 项通过。

### 对已合并版本的适配

A 在 `0fe0188` 中为 `groups[].name` 补了执行指南的样例、并把 D01 的 schema 一致性回归测试合并进
`tests/Unit/Exam.UnitTests/Importing/SchemaConsistencyTests.cs`（2 项，核对单独发布的 schema 与清单内嵌定义
逐项一致，并固定错误样包为 26 例）。本任务据此调整：

- 新增代码与测试的目录、命名空间统一为 `Importing`（`Exam.Contracts.Importing`、`Exam.Domain.Importing`、
  `Exam.UnitTests.Importing`），与已合并的测试目录一致。
- 原 `SchemaConsistencyTests` 与 A 的测试重名且重复，改为 `ContractSchemaAlignmentTests`，
  只保留 A 的测试未覆盖的 **schema 与 C# 契约对齐** 部分（DTO 属性名、题型枚举、变量白名单、
  显示元数据形状、`anchorScheme` 常量、所有对象 schema 禁止未定义字段）。
- 注意：A 的测试固定错误样包为 **26 例**，后续增删错误样包需同步该断言。

样例的编译核对（属 D02 范围，此处只作为样例质量证据）：`three-types`、`ordering`、
`figures-chemistry` 共 16 道题在本机 XeLaTeX 下全部编译通过，`figures-chemistry` 第 51 题实测 2 页、
与清单 `pageCount: 2` 一致，生成的 PDF 内确认含四个 hyperref 选项标记目标。

`Category=Postgres` 的集成测试未在本机执行：本机没有 PostgreSQL 18，仓库要求只在当前进程提供
`EXAM_TEST_POSTGRES`。该部分不涉及本任务的改动。

## 尚未完成及归属

- **D02 依赖检查与编译**：源文件是否存在、字体/宏包/图片依赖、独立目录 XeLaTeX 编译、可定位错误提取与版式核对。
  本任务只做**静态**校验，不读包内文件；样例的编译通过是本机一次性核对，未纳入自动检查，且 CI 无 TeX 发行版。
- **D03 选项定位与学生包**：normalized 选项区域产物、允许清单组包、加密与版本发布。
  本任务只固定 `anchorScheme` 名称与「圆钮第 i 个 ↔ `options[i]`」的绑定约定，**不猜坐标结构**。
- **D04 精确计分**：含变量的求值与发布前枚举每题所有合法作答情况；本任务只提供解析器与静态判定。
- **D05 规则版本与重算**：`answer-key` 的规则版本字段（拟 `ruleSetId`、`paperVersion`）与按版本去重。
- **答案完整性**：本任务**不**要求每题都有答案（导入期答案可以后续补齐）；「每个可评分题目都有有效答案」
  应由 D04 的发布门保证，未在此实现。
- **组内题号连续性**：需求未明文要求，本任务不强制，提请 A 裁定。
- **答案条目重复**：同一题目出现两条答案时，本任务未报错（按答案与规则版本去重属 D05 的范围）。
- **C05 与 GUI/CLI 接入**：导入预览、逐项错误展示与 CLI 命令尚未接入；CLI 由 E 的框架包装。
- **交叉审查**：按分工由 B 核对版式与控件绑定、A 审查执行边界、E 独立核算，**均尚未进行**。

以上是本机实现与自动化测试证据，**不等于**真实试卷导入、版式比对、300 人规模或三校样卷验收通过。
本任务未改动任何运行链路，也未接入 GUI、CLI、HTTP 或数据库。任何未列为完成的场景不得写成已通过。
