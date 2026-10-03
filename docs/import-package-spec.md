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
  是否存在由 D02 的依赖检查定位报错；D01 检查路径形式、每段 Windows 名称与规范化后的重复路径。
  `styles`、`./styles/`、`STYLES` 视为重复；路径不得包含设备名、数据流或结尾空格/句点等非法目录段。
  符号链接与联接点是否越界必须由 D02 在实际解析依赖时另行检查，静态路径检查不能代替该检查。
- 包来源可以是目录或 ZIP；两者的校验结论必须一致。路径一律相对包根目录，保留子目录结构。
  ZIP 中越出包根的条目（`..`、绝对路径、盘符）一律拒绝。
- 导入按**数值题号**与清单顺序进行，不使用字典序（避免 1、10、2 的次序）。
- 包内不得含真实试卷、参考答案之外的材料；`server-only/` 下的内容不得进入学生端包。

## 2. 文件命名

- 每题为**一个文件**，命名为 `<考试名称>_<题号>.tex`；扩展名按 Windows 习惯不区分大小写，schema 与解析器一致。
- **以最后一个下划线之后的十进制数字识别题号**；该数字必须与清单中该题的 `questionNo` 一致。
  例：`考试_名称_10.tex` 的题号为 10。
- 文件名前缀必须等于清单的 `examName`。
- 创建考试时必须检查 `examName` 能否用作 Windows 文件名：禁止 `< > : " / \ | ? *` 与控制字符，
  禁止保留设备名（`CON`、`PRN`、`AUX`、`NUL`、`COM1`…`COM9`、`LPT1`…`LPT9`），禁止结尾空格或句点。
  同样拒绝 Windows 保留的 `COM¹`…`COM³`、`LPT¹`…`LPT³`；`.名称` 本身不是设备名，不应误拒。
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

### 独立答案与输入边界

`answer-key.examId` 必须与清单一致；同一 `questionId` 只能有一条答案，`correctOptionIds` 也不得重复。
无论重复条目的内容相同还是冲突，都拒绝导入，不按首条或末条静默覆盖。未通过考试、题号、选项、题型
校验的答案不参与满分计算，避免污染结果或产生误导的汇总差异。

JSON 入口拒绝缺失必填字段、显式 `null`、null 数组成员、重复属性、错误类型与超出 schema 上下界的数据。
不把带引号的数字隐式转为整数；数值为整数的 `1.0`、`1e0` 按 JSON Schema 语义接受，
不经过浮点或 decimal 舍入。此规则仅用于整数字段，计分表达式的文法不变。
GUI 直接构造 DTO 时也经过同一结构检查；DTO 中可选字段的 `null`
代表“未声明”，序列化时省略，而必填字段缺失仍报错。显示元数据不能是空对象，页数、选项与显示文本
的数量、长度和唯一性都按 schema 检查。

导入期允许不传答案或只提供部分答案，此时 `RequiresAnswerKeyArithmetic` 表示尚未完成满分核对。
`ValidateJson` 的答案参数只有 `null` 表示未提供，传入空字符串或纯空白不是合法答案文件。
调用方必须先检查 `IsValid`，不能把“未完成核对”当作可发布证明。

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
- 已持有合法参考答案时，D01 将正确选项数代入满分分支，逐题拒绝除零与非有限小数；
  例如三题各为 `1/3`，不能因合计为 `1` 就放行。中间分数约分后整体有限（如 `(1/3)*3`）仍允许。
- 其他依赖作答的情形由 D04 在**发布前枚举每题所有合法作答情形**校验最终逐题分为有限小数。
  D01 的满分核对不代替这一发布门。
- 科目与题组满分不是表达式，而是无变量的有限十进制常量。

## 5. 显示元数据与选项标记

`displayMetadata` 的字段级约束见 `schemas/display-metadata.schema.json`。产地划分如下：

| 字段 | 产地 | 说明 |
| --- | --- | --- |
| `anchorScheme` = `pageTopLeftNormalizedV1` | **D01 定名** | 坐标规范：定位区域原点在页面左上角，按页面实际尺寸归一化到 `[0,1]`，显示时按实际尺寸缩放。D03 产出的区域与 B03 的控件绑定共用此名称，任何一方不得自造；省略时按该默认值处理 |
| `optionCaptions` | **D01** | 与 `options` 等长同序的显示文本（如 `A.`、`（A）`、`甲乙丙丁`）；省略时按 `options` 原值显示 |
| `pageCount` | D02 写回 | 清单作者可声明期望值，D02 编译后以实测值核对并写回 |
| 选项定位区域 | **D03** | 每题每选项的页索引与归一化矩形，随 `paperVersion` 作为版本产物发布；**不写入清单** |

定位仍由隐藏链接绑定选项；可读文字标签的最终矩形取链接范围内的字母/数字可见字形，
不含句点、括号和链接留白。图片、路径或不可读标签无可用字形时保留链接区域，须预览核对。
坐标产物 v2 同时保存显示字母及完整标签遮盖区域，学生端以实心蓝底白字按钮遮住原标签，选中后加深；
“不作答”共用该配色。读取器兼容 v1；新包发布前须先升级学生端。
具体坐标转换及旧包重新发布要求见 [选项定位契约](d03-option-region-contract.md)第 4.3、4.4 节。

选项标记采用 A 于 2026-09-27 确定的路线 A，模板和合成样例已使用三参数宏，见 [D03 选项定位契约](d03-option-region-contract.md)。

```latex
\optmark{题目标识}{选项标识}{选项标签文本}   % 只包 A. 或 （A）等标签，不包选项正文，不改变外观与占位
\DeclareRobustCommand{\optmark}[3]{\hypertarget{opt:#1:#2}{}\hyperlink{opt:#1:#2}{#3}}
```

上述形式对应已选定的**路线 A**，作者已记录合成样包实测；三条路线的取舍及后续验证边界见
[D03 选项定位契约](d03-option-region-contract.md)。原形式只用 `\hypertarget` 建立零尺寸命名目标，
**该形式不产生注解对象，PdfPig 无法读出区域**，是实现抽取时才发现的问题。

每个导入包自带共享样式，样式负责提供该宏，并在加载 hyperref 后设置 `\hypersetup{hidelinks}`，保证链接外观隐藏。
**试卷题目拆分已由 A 于 2026-09-27 确认为完成**。D06（#29）继续在现有逐题材料上适配三参数标记样式，
完成依赖、编译和版式/选项绑定对照，拆分完成不代替这些检查。

学生端控件绑定约定为**圆钮第 i 个对应 `options[i]`**，坐标按题目及选项标识精确匹配。
坐标产物使用题内从 0 开始的页索引，固定文件名 `option-regions.json`，随该版本 PDF 和学生清单放在同一加密 ZIP。
学生清单 v2/v3 必须附带该文件，v1 不允许夹带或将其当作普通显示资源。
当前组包器在提供坐标产物时发布学生清单 v3；无坐标时仍发布 v1。
v3 另外包含科目 `maxScoreText` 与题组 `perQuestionScoreText`，供导航显示满分和每题分值。
这些文字取自导入配置，只保留满分表达式，不携带参考答案、错选/漏选规则或逐题正确选项数。
字段语义、合成样例及升级顺序见 [学生导航显示契约](b03-navigation-display.md)。

## 6. 校验分工

| 类别 | 落点 |
| --- | --- |
| 字段存在性、类型、长度与数值边界、`additionalProperties`、数组数量与唯一性 | JSON Schema 声明，`ManifestShapeScanner` 执行 |
| 引用完整性、题型与分支、数量一致、标识唯一、答案关联、表达式文法与变量、满分求和、文件名与路径规则 | 校验器（`Exam.Domain.Importing.ExamManifestValidator`） |
| 源文件是否存在、字体/宏包/图片依赖、编译 | D02 |

仓库不引入 JSON Schema 校验库：schema 作为对外契约、校验器为实现权威。两份定义由零依赖的测试守住：
`tests/Unit/Exam.UnitTests/Importing/SchemaConsistencyTests.cs` 核对单独发布的 schema 与清单内嵌定义逐项一致，
`ContractSchemaAlignmentTests.cs` 核对 schema 与 C# 契约（字段名、枚举、变量白名单）不漂移；
`ImportReviewRegressionTests.cs` 读取公开 schema，自动生成缺字段、null、错误类型、上下界与唯一性变异，
并覆盖 DTO 入口、跨文件关联、路径与逐题满分缺陷。
因此**每个样例都必须通过校验器**，样例集合本身就是一次对 schema 语义的检验。

### 规则标识与定位

校验结果用**稳定规则标识 + RFC 6901 JSON Pointer** 定位，传输层仍只使用既有错误码 `IMPORT_INVALID`，
不新增错误码。清单内问题指向清单指针（如 `/groups/0/scoring/oneCorrect/correct`），
答案问题相对答案 JSON 根定位（如 `/answers/0/questionId`，版本错误为 `/schemaVersion`，不带 `/answerKey` 前缀）。
JSON 根指针为空字符串，字段名中的 `~`、`/` 分别转义为 `~0`、`~1`；不是用 `/` 代表根。
未来包内文件问题使用 `file:<包内相对路径>`。规则标识见 `ManifestRuleIds` 与错误样包。

`samples/synthetic/errors/expected-errors.json` 中 26 个错误样包的既有规则与位置保持兼容。
此外的防御性规则覆盖错误样包未单独涵盖的约束，例如：

| 规则标识 | 说明 |
| --- | --- |
| `subjects.duplicateId` | 科目标识重复会让满分求和重复计入 |
| `groups.duplicateId` | 题组标识重复会让引用不明确 |
| `groups.maxScore.constantRequired` | 题组满分与科目满分一样必须是无变量常量 |
| `questions.options.invalid` | 选项标识不能为空或只由空白字符组成 |
| `questions.displayMetadata.anchorScheme` | 坐标规范只能是 `pageTopLeftNormalizedV1` |
| `answerKey.schema.version` | 独立答案的 schemaVersion 必须为 1 |
| `manifest.schema.constraint` | schema 声明的字符串、数组、整数边界及非空对象约束 |
| `manifest.duplicateField` | 同一 JSON 对象中重复属性，包含不同转义写法的同名字段 |
| `manifest.resourceRoots.invalid` / `manifest.resourceRoots.duplicate` | 非法或规范化后重复的资源目录 |
| `rule.answerKey.examIdMismatch` | 独立答案不属于本清单的考试 |
| `rule.answerKey.duplicateQuestion` | 参考答案重复声明同一题目 |
| `rule.answerKey.optionsDuplicate` | 参考答案的正确选项集合包含重复元素 |

另有两类在清单整体不可用时才触发的检查，不单独配样例：必填字段缺失（`manifest.required.missing`）、
无法解析为 JSON 或字段类型不符（`manifest.invalidJson`）。

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
- 错误样包已成为**可执行验收清单**：`SamplePackageTests` 遍历目录并与 `expected-errors.json` 逐条比对
  （期望规则、定位，以及不得出现未声明的额外错误）。新增用例只需改样例与期望清单，不改测试。
- 合计校验通过是**静态**结论：它不表示 TeX 可编译、版式正确或考试可用。
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
5. **答案完整性不在导入期强制**：导入时允许答案后补，因此本任务不要求每题都有答案；
   「每个可评分题目都有有效答案」应由 D04 的发布门保证。
6. **答案条目重复已在 D01 拒绝**：这是参考答案文件自身的有效性约束；D05 的答卷/规则版本去重是另一件事。
7. **校验是静态的**：源文件是否存在、依赖是否齐全、能否编译，全部属 D02。缺答案时满分核对
   仍未完成；即使持有答案且满分核对通过，D04 的全部合法作答情形与完整性发布门也不能省略。

## 9. 源材料归档与逐题内容摘要（C05）

发布时随加密学生包一起提交一份**源材料归档**（上传表单的第二个密文部分 `materials`），
服务器按 `(考试, 试卷版本)` 保存，用于两件事：后续修题时取回材料；核对开考后修订声明的变更题号。
每个已发布版本的归档都保留，不做自动删除。

归档是一个确定性 ZIP（条目按路径 ordinal 排序、时间戳固定 1980-01-01，同样材料得到同样字节）：

- 条目路径**相对材料包根**、`/` 分隔，与清单里的 `source`、`resourceRoots` 同一坐标系；
- 内含 `materials.json`，逐个登记路径、长度与 SHA-256；清单与条目必须双向一致；
- 上限：归档 64 MiB、条目 2048、文本类文件（`.tex/.sty/.cls/.def/.cfg`）8 MiB、路径 512；
- 拒绝越界路径（`..`、盘符、绝对路径）、Windows 保留名/数据流、忽略大小写的重名。

**逐题内容摘要**（`QuestionContentDigest`，客户端与服务器共用同一实现）：

- 摘要面 = 需求允许开考后修改的面：题干与选项文本（题目 `.tex`）、图片、选项结构，
  以及题号/科目/题组/题型/弃权/选项顺序、源文件相对路径与显示元数据；
- 依赖闭包：`\input`/`\include` 递归、包内 `.sty/.cls/.def/.cfg`（发行版宏包不在归档内，不计）、
  按 `\graphicspath` 顺序解析到的图片；闭包内发现的**图片宏**会回头重扫调用点
  （题目里 `\qimg{图.png}` 的宏定义在共享样式里的情况）；
- **不计入**：独立答案 JSON（只改答案属规则修订，学生手里那份卷子没变）、未被任何题目依赖的文件、
  TeX 发行版依赖；
- **刻意保守**：共享样式或共享图片变化会让**所有依赖它的题**都算变更——它们的渲染确实变了；
- 查找大小写不敏感，参与哈希的路径取小写规范名（大小写重命名不算内容变化）；
- 已知残余限制：宏在运行时拼出的图片文件名无法静态解析，这类依赖只能靠共享样式自身的变化间接兜住。

开考后修订的核对由**服务器**负责：`ChangedQuestionIds` 必须与实际变更集**完全一致**，
漏报（`CHANGED_QUESTION_IDS_MISSING`）与把未变的题报成变更（`CHANGED_QUESTION_IDS_UNEXPECTED`）都拒绝；
没有任何内容变化时拒绝走试卷修订（`NO_CONTENT_CHANGE`，应使用规则修订入口）；
上一已发布版本没有归档时失败关闭（`MATERIALS_BASELINE_MISSING`），不当作首次发布。
首次发布允许留空或整卷声明，不允许只声明一部分。

**考务端流程**（CLI 与界面同一实现）：

- 修订前先取回当前版本的源材料：`import materials <考试名称> --output <新目录>`（界面为「取回当前版本源材料」），
  解出的目录就是可编辑的材料包（含独立答案，只在本机保存）。
- 检查与预览：`import preview <目录或ZIP> --exam <考试名称> --output <新预览目录> [--reason 说明] [--changed 题号…]`。
  修订模式（该考试已发布过试卷）必须给出 `--reason`；`--changed` 省略时采用**按上一版本归档算出的差异**，
  界面同样把差异显示出来。声明在**预览阶段定稿**并随预览保存——发布只重放同一份已保存的请求，
  因此跨进程重放不会产生第二份请求。
- 发布：`import publish <预览目录> --confirm`（界面为「确认并正式发布」，确认框列出本次为修订、
  声明题号与修改原因）。发布成功后服务器仍会按内容核对声明，不一致就拒绝。
- 归档里记录的是**绑定过考试与目录设置的清单**：客户端在检查时把考试标识、科目与题组标识绑定到所选考试，
  逐题摘要按绑定后的清单计算，两版才能直接比对。

## D02 编译输入与输出约定

导入材料中的 TeX、样式、文档类与配置文件会在编译前检查。引用应使用包内相对路径或已安装的宏包名，
不得通过绝对路径、父目录、符号链接或目录联接读取包外材料。源文件不得直接使用 openin/openout 文件流。
图片搜索路径使用实际目录，例如 `\graphicspath{{figures/}}`；不支持 `**` 通配目录。
图片可以省略常见扩展名，带子目录的引用仍按 graphicspath 尝试查找。

`TeXCompileRequest.OutputDirectory` 是产物父目录，必须在导入包之外。每次编译创建独立子目录，
调用方通过 `TeXCompileResult.OutputPdfPath` 获取成功产物，不能自行按题目文件名拼接路径。
失败结果不提供 PDF 路径，失败目录不能用于预览或发布。缺依赖、字体替换、缺字、超时和资源超限均阻止成功。
源路径与输出路径的参数或权限异常也应作为导入失败展示。

`TeXIssueCode.ResourceLimit` 表示输入、内存或产物超过限制。具体阈值、真实编译验证与适用边界见
[D02 验收记录](d02-implementation-status.md)。调用方负责失败目录清理、并发调度和版本发布。
