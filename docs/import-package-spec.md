# 逐题导入包规范（D01）

本文件定义逐题导入包的目录模板、字段、命名与校验规则，对应 `agent-implementation-guide.md` 的 D01 任务卡
「输出清单、独立答案、显示元数据与规则 schema，文件目录模板、三题型样例、带图化学式多页样例、错误样包」。
业务依据为 `requirements.md`「逐题导入、原版显示与加密」「考试配置与计分」。

**C 按本规范做考试配置表单，B 按本规范绑定显示与选项控件；两者不得另造字段。**
字段级契约以 `schemas/` 下的四个 JSON Schema 为准，本文件说明语义、产地与校验分工。

## 1. 目录模板

```
<包根目录>/
├─ manifest.json                     清单：考试、科目、题组、题目、资源根
├─ questions/<考试名称>_<题号>.tex    逐题 TeX 源文件，每题一个文件
├─ styles/…                          共享样式（可选，按需）
├─ figures/…                         题目图片（可选，按需）
└─ server-only/answer-key.json       独立答案，仅服务器使用，不进入学生端包
```

- `resourceRoots` 列出需要收集的共享资源目录（如 `figures`、`styles`）。**列出的目录必须存在于包内**；
  是否存在由 D02 的依赖检查定位报错，D01 只校验路径形式。
- 包来源可以是目录或 ZIP；两者的校验结论必须一致。路径一律相对包根目录，保留子目录结构。
  ZIP 中越出包根的条目（`..`、绝对路径、盘符）一律拒绝。
- 导入按**数值题号**与清单顺序进行，不使用字典序（避免 1、10、2 的次序）。
- 包内不得含真实试卷、参考答案之外的材料；`server-only/` 下的内容不得进入学生端包。

## 2. 文件命名

- 每题为**一个文件**，命名为 `<考试名称>_<题号>.tex`。
- **以最后一个下划线之后的十进制数字识别题号**；该数字必须与清单中该题的 `questionNo` 一致。
  例：`考试_名称_10.tex` 的题号为 10。
- 文件名前缀必须等于清单的 `examName`。
- 创建考试时必须检查 `examName` 能否用作 Windows 文件名：禁止 `< > : " / \ | ? *` 与控制字符，
  禁止保留设备名（`CON`、`PRN`、`AUX`、`NUL`、`COM1`…`COM9`、`LPT1`…`LPT9`），禁止结尾空格或句点。
  该检查**不使用 `Path.GetInvalidFileNameChars()`**（各平台语义不同），由项目自行实现。

## 3. 清单字段

完整约束见 `schemas/exam-manifest.schema.json`。要点：

| 字段 | 说明 |
| --- | --- |
| `schemaVersion` | 固定为 1 |
| `examId`、`examName` | 考试标识与名称；名称同时用于逐题文件名前缀 |
| `subjects[]` | `subjectId`、`name`、`maxScore`（**无变量的有限十进制常量**） |
| `groups[]` | `groupId`、`subjectId`、`name`（题组显示名称，如「一、不定项选择题」）、`type`、`order`、`questionCount`、`allowAbstain`、`maxScore`（可选常量）、`scoring` |
| `questions[]` | `questionId`、`questionNo`、`subjectId`、`groupId`、`source`、`options[]`、`displayMetadata`（可选） |
| `resourceRoots[]` | 需要收集的共享资源目录 |

- 全卷题号唯一，跨科不重置；`order` 为题组在本科目内的显示顺序。
- `options` 只登记**选项标识**，顺序即原卷显示顺序；选项文本与版式保留在原版 PDF 中，客户端不重新排版。
- 题组 `name` 与 `maxScore` 是 D01 相对 A01 初版新增的字段：`requirements.md` 要求题组有「独立标识、
  **名称**、顺序、题量及计分规则」，且「题组与科目满分逐题求和」。

## 4. 计分规则

分支与语义见 `requirements.md`：

| 正确答案情况 | 分支 |
| --- | --- |
| 一个正确选项 | `oneCorrect`：`correct`、`blank`、`wrong`；多项选择题组不使用 |
| 多个正确选项 | `multipleCorrect`：`allCorrect`、`partialCorrect`、`blank`、`wrong`；单项选择题组不使用 |

- 单项选择题组只使用 `oneCorrect`，多项选择题组只使用 `multipleCorrect`，
  **不定项选择题组必须同时提供两项分支**（该约束同时写在 schema 的 `if/then` 与校验器里）。
- 「不选」为空选或「不作答」，两者同走 `blank` 分支；`allowAbstain` 只决定学生端是否呈现「不作答」。
- 每题满分取 `correct` 或 `allCorrect` 的得分；**逐题满分依赖参考答案**
  （判断该题是「一个正确选项」还是「多个」），因此科目与题组满分的求和校验必须在同时持有清单与答案时进行。

### 表达式文法

```
expression = term { ("+" | "-") term } ;
term       = factor { ("*" | "/") factor } ;
factor     = [ "-" ] primary ;
primary    = decimal | variable | "(" expression ")" ;
decimal    = digit { digit } [ "." digit { digit } ] ;
variable   = "{" ( "正确选项总数" | "选择的正确选项数" ) "}" ;
```

- 运算符左结合，`* /` 优先于 `+ -`；`digit` 仅 ASCII。
- **变量只有两个中文原名**，与需求原文一致，不提供 ASCII 别名：`{正确选项总数}` 为正确答案集合的大小，
  `{选择的正确选项数}` 为考生所选选项中属于正确答案的个数。ASCII 写法一律视为非法变量。
- 静态可判的拒绝：非法字符、未知变量、括号不配、**常量分母为零**、**整条表达式为常量且约分后分母含
  2 与 5 以外质因子**（例如 `1/3`）。
- 依赖作答的表达式（含变量的和）不在导入期判定，改由 D04 在**发布前枚举每题所有合法作答情形**校验
  最终逐题分为有限小数（`requirements.md`：「发布前校验所有合法作答情况，最终逐题分必须为有限小数」）。
- 科目与题组满分不是表达式，而是无变量的有限十进制常量。

## 5. 显示元数据与选项标记

`displayMetadata` 的字段级约束见 `schemas/display-metadata.schema.json`。产地划分如下：

| 字段 | 产地 | 说明 |
| --- | --- | --- |
| `anchorScheme` = `pageTopLeftNormalizedV1` | **D01 定名** | 坐标规范：定位区域原点在页面左上角，按页面实际尺寸归一化到 `[0,1]`，显示时按实际尺寸缩放。D03 产出的区域与 B03 的控件绑定共用此名称，任何一方不得自造；省略时按该默认值处理 |
| `optionCaptions` | **D01** | 与 `options` 等长同序的显示文本（如 `A.`、`（A）`、`甲乙丙丁`）；省略时按 `options` 原值显示 |
| `pageCount` | D02 写回 | 清单作者可声明期望值，D02 编译后以实测值核对并写回 |
| 选项定位区域 | **D03** | 每题每选项的页索引与归一化矩形，随 `paperVersion` 作为版本产物发布；**不写入清单** |

选项标记约定（模板的一部分，样例样式已实现）：

```latex
\optmark{题目标识}{选项标识}     % 在选项字母处建立零尺寸命名目标，不改变外观与占位
\DeclareRobustCommand{\optmark}[2]{\hypertarget{opt:#1:#2}{}}
```

每个导入包自带共享样式，样式负责提供该宏。学生端控件绑定约定：**圆钮第 i 个对应 `options[i]`**。

## 6. 校验分工

| 类别 | 落点 |
| --- | --- |
| 字段存在性、类型、枚举、`additionalProperties`、路径正则、选项数量与唯一性、三题型分支、满分为常量 | JSON Schema |
| 引用完整性、数量一致、题号与题目标识唯一、题目科目与题组一致、表达式文法与变量、满分求和、文件名规则 | 校验器实现（PR#2） |
| 源文件是否存在、字体/宏包/图片依赖、编译 | D02 |

仓库不引入 JSON Schema 校验库：schema 作为对外契约与文档，校验器为实现权威，另有零依赖的一致性测试
（`SchemaConsistencyTests`，PR#2）断言两份定义不漂移。因此**每个样例都必须通过校验器**，
样例集合本身就是一次对 schema 语义的检验。

### 规则标识与定位

校验结果用**稳定规则标识 + RFC 6901 JSON Pointer** 定位，传输层仍只使用既有错误码 `IMPORT_INVALID`，
不新增错误码。清单内问题指向清单指针（如 `/groups/0/scoring/oneCorrect/correct`），
包内文件问题指向 `file:<包内相对路径>`。规则标识见错误样包与 `expected-errors.json`。

## 7. 样例清单

```
samples/synthetic/
├─ minimal/            最小合法包：单科目、单题组、单题，无共享资源
├─ three-types/        三题型：single / multiple / indeterminate 各一组，含 {正确选项总数} 变量组
├─ figures-chemistry/  带图 + 化学式 + 多页：4 题，其中第 51 题声明 2 页并经编译核对
├─ ordering/           清单按 10、2、1 排列，导入必须按数值题号排为 1、2、10
└─ errors/             26 个最小错误包 + expected-errors.json（可执行的验收清单）
```

- 全部为**合成材料**，不含任何真实试卷、答案或解析。三校真实材料在仓库外，不得入库。
- 错误包只含清单与答案：这些夹具针对 D01 的静态校验，**不含 TeX 源文件**；文件是否存在属于 D02。
- 合成图片为 48×32 灰度 PNG（边框加对角线的示意图），由脚本一次性生成，无版权材料。
- 样例的编译核对使用本机 MiKTeX；**CI 上没有 TeX 发行版**，因此编译相关检查属于 D02，
  并需要独立的测试类别（D02 引入时应与 E 确认 CI 过滤条件）。

## 8. 已知缺口与待确认项

1. **`answer-key.schema.json` 暂无规则版本字段。** 需求要求「按答卷及规则版本去重」，
   拟命名 `ruleSetId`、`paperVersion`（对齐 A 的 `GradingPublicationInfo.RuleSetId`、
   `PackageUnlockMaterial.PaperVersion`），由 D05 定义后再加，属向后兼容的可选字段。
2. **选项定位区域坐标**由 D03 固化为版本产物；D01 只固定 `anchorScheme` 名称与「圆钮第 i 个 ↔ `options[i]`」
   的绑定约定，**不猜坐标结构**。
3. **组内题号是否必须连续**：需求与三校材料均满足连续，但需求未明文要求。D01 不强制，提请 A 裁定。
4. **`groups[].name`、`groups[].maxScore`、`displayMetadata`** 是 D01 相对 A01 初版新增的公共字段，
   涉及 C01 表单设计，需 A 确认并同步 C。
