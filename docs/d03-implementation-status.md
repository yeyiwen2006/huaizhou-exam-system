# D03 实施状态

本次交付学生包文件选择、服务器专用资源隔离、ZIP 组包和既有 HEXAMPKG v2 加密格式的接入。PR #50 经审查修正后作为 D03 的部分交付，Issue #26 保持打开。

## 已实现行为

- 元数据先经过 D01 的完整结构及语义校验，再从受控 DTO 重新生成。必填字段、空集合、题号、引用及路径规则不再只依赖反序列化。
- 后续接口修正将输出清单明确为 StudentPaperManifest，包含试卷版本、题目及 PDF 映射、题型、选项和 AllowAbstain，不携带导入源路径及计分规则。学生端运行时已接入读取，约定见 [模块接口衔接](module-interface-integration.md)。
- 默认进包内容为 manifest.json 和逐题 PDF。PDF 已嵌入编译时使用的图片、字体及版式，不分发 TeX 源码和编译样式。
- resourceRoots 只限定附加资源的可选目录。调用方经导入预览确认后，通过 DisplayResourcePaths 提供逐文件允许清单，目前接受 PDF、PNG、JPEG 并核对文件头。未选择的文件记入排除列表；必需文件缺失、重复或被拒绝时整个组包失败。
- 路径检查复用 PathBoundary，拒绝越界路径、符号链接和目录联接。输入文件读入受限内存快照；完整内容中的答案字段检查作为补充，取消原来的 64 KiB 取样。
- CompiledQuestions 按 questionId 接收本次 D02 返回的成功编译结果，直接读取各 compile-* 目录里的 OutputPdfPath。省略该参数时可使用调用方准备的平铺目录，调用方必须确认该目录只含本次已验收的产物。
- 指定 EncryptedPackagePath 时直接从内存生成 ZIP 并加密，StudentPackageDirectory 不产生明文。未指定加密路径时，显式生成服务器侧预览目录。
- 每次加密使用独立临时文件，完成后刷新并移动到最终路径；取消及失败时清理本次临时文件。已有密文不覆盖，新旧产物可以分别保留，后续下载应复用既有密文及信封。
- 预览目录通过独立暂存目录发布。产物与材料、编译输入目录分离，不修改输入材料。

## 调用约定与限制

StudentPackageRequest 的两个可选属性属于组包输入，不定义 B03 的坐标格式。DisplayResourcePaths 默认空列表。CompiledQuestions 的键必须与清单 questionId 一一对应，失败编译结果不能退回读取旧 PDF。

清单最大 8 MiB，待打包文件合计最大 256 MiB，单个资源目录遍历最多 10000 个文件或目录。超过限制时停止组包；最大实际题包的容量验收仍归后续接线测试。

文件头和字段检查不能判断图片或 PDF 正文的语义，也不能证明压缩附件中绝无答案。材料必须来自已验收的题目编译结果，附加资源仍需在导入预览逐项确认。调用方应在组包期间保持材料和编译目录不被其他进程修改。

## 本次验证

环境为 Windows、.NET SDK 10.0.401、TeX Live 2025。输入均为合成材料。

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore -c Release
dotnet test Huaizhou.ExamSystem.sln --no-build -c Release --filter "Category!=Postgres"
```

依赖还原成功，Release 构建 0 警告、0 错误。1029 项测试通过，分别为领域及基础设施单元测试 784、学生端 89、管理端 76、学生身份 25、集成 33、负载工具 22。

新增回归覆盖越界资源根、无效清单、未经选择的资源混入、64 KiB 之后的答案字段、目录联接、取消、加密失败重试、已有密文保护及加密模式不生成明文目录。真实 TeX 用例编译两道合成题，确认它们位于不同的编译目录，再用学生端 PackageCiphertext 解码并读取两个 PDF。该用例标记 Category=Latex，普通 Windows CI 不运行，本机已实跑。

PostgreSQL 检查由 PR 最新提交的独立 CI 执行，记录见 [PR 审查记录](reviews/pr50-d03-student-package.md)。本地未连接数据库。

## 已确定的坐标契约与材料状态

2026-09-27，A 在 PR #61 确定隐藏外观链接与 PdfPig 的路线 A，保留三参数合成样包。
标签区域、题内零起页索引、原始矩形变换及加密包内 `option-regions.json` 的约定见
[选项定位契约](d03-option-region-contract.md)。D 按契约继续实现，B 接入并配合显示和点击复测。
当前运行时代码仍使用学生清单 v1，坐标文件及清单 v2 的同步支持尚未交付，不可直接向旧包添加坐标 JSON。

同日 A 确认试卷题目拆分已完成。D06（#29）保留该完成子项，继续处理样式标记适配、依赖清单、材料对照和 CLI。
本次状态更新没有重新核验真实材料，不代表 D06 或 T01/T02 已验收。

## 本轮交付：选项定位抽取器（2026-09-27）

按 [选项定位契约](d03-option-region-contract.md) 第 4 节实现抽取侧，尚未接入组包。

- 公共契约 `Exam.Contracts.Importing.OptionRegionArtifact` 及问题类别；产物固定名 `option-regions.json`，
  `artifactVersion = 1`、`anchorScheme = pageTopLeftNormalizedV1`、题内零起页索引。
- `Exam.Domain.Importing.OptionRegionGeometry`：纯几何换算（裁剪基准、`/Rotate` 0/90/180/270 换算表、
  边界与宽高校验）与同名碎片连通合并判定；不与 PDF 读取耦合，便于穷举验证。
- `Exam.Infrastructure.Compilation.OptionRegionExtractor`：筛选 Link 注解，取**原始字典的 `/Rect`**
  （不取已应用初始矩阵的 `Annotation.Rectangle`），从 `/A /D` 或直接 `/Dest` 读目标名
  （不使用 `/NM`），要求解析为同一 PDF 内 GoTo 且目标页与注解页一致；期望目标名由清单生成并精确比较，
  两个组合同名时拒绝发布；缺失、清单外、分离或跨页重复一律整次失败，不产出部分文件。
- 新增依赖 PdfPig 0.1.16 并同步各 `packages.lock.json`。
- **同时修复 D02 的日志缺陷**：`XeLaTeXLogReader` 只按折叠结果识别 `Output written on ...`，
  而 79 列硬折行的折点可能落在文件名的扩展名点上（`..._1.` 换行 `pdf (3 pages).`），
  此时 `Fold` 的句末规则把续行拆成两条记录，**真的写出了 PDF 也被判成没有产物**，
  合法导入被当成编译失败拒绝。现对该标记之后的片段单独还原折行，不改变其它日志行的折叠行为。

作者原提交验证（Windows、.NET SDK 10.0.401、MiKTeX 26.5 / XeTeX 4.18、PdfPig 0.1.16）：
Release 构建 0 警告 0 错误；`Category!=Postgres&Category!=Wpf` 共 **1307 项通过、0 失败、0 跳过**
（单测 1006、集成 46、学生身份 24、学生端 134、考务端 75、负载工具 22）。
其中几何换算 25 项穷举旋转与裁剪；真实 XeLaTeX 集成 12 项覆盖普通题、显式分页、旋转 0/90/180/270
与契约换算表的精确关系、裁剪基准、以及缺失/清单外/悬空/分离重复四类失败路径。
日志缺陷的回归用例在修复前失败、修复后通过。

**未做**：坐标 JSON 的序列化与公共 schema、包内同一加密 ZIP 的集成、学生清单 v2 与学生读取器、
B03 的控件接入与真实点击绑定、真实材料验收。这些按契约第 4.4 节与 #26 在后续 PR 交付。

### PR #64 合并前审查修正

审查修正了原始碎片连通性、继承 CropBox、十六进制与间接目标读取、冲突动作及隐藏标志、
单个碎片有效性、严格边界、延迟 PDF 解析异常、请求校验与输出日志路径处理。
新增 56 项无需 TeX 的回归进入普通 CI；其中首批 39 项在修复前有 30 项失败，修复后全部通过。

Windows / .NET 10.0.401 / TeX Live 2025 的独立复测：锁定还原通过，Release 构建 0 警告、0 错误；
普通测试 1340 项、真实 TeX 类别 23 项、独立 WPF 2 项，合计 **1365 项通过，0 失败、0 跳过**。
[完整审查记录](reviews/pr64-d03-option-region-extractor.md) 区分了作者原验证、本次复现与未覆盖范围。
PostgreSQL 本地未运行，修订提交的远端 CI 结果在 PR 中记录。#26 继续保持打开。

## 本轮交付：学生清单 v2 与坐标文件进包（2026-09-28）

按契约第 4.4 节把坐标产物接入学生包，读写双方同步升级。

- `StudentPaperRequest` 新增 `OptionRegions`：调用方传入抽取好的坐标产物，组包器据本次
  已验收的编译产物打包；传入即产出 **schemaVersion 2**（必须附带根目录 `option-regions.json`），
  不传维持 v1。组包器仍是纯打包器，不承担 PDF 解析与抽取。
- 组包前核对产物与本包的考试、试卷版本、产物版本、坐标规范以及**清单的题目与选项逐一对应**：
  多一个、少一个、清单外选项一律整包失败，不发布。
- 坐标文件只作为**专用元数据**接受，不进入 `displayResources`：契约 4.4 明确不允许把坐标文件
  塞进旧包或当成普通显示资源。实测确认这一点：先前的实现会让坐标文件被回填进显示资源列表，
  随即被显示路径校验拒绝。
- `StudentPaperPackage` 接受 v1 与 v2：声明 v2 必须附带坐标文件，声明 v1 不得夹带，
  两者不一致即整包失败，避免同一份清单出现两种解释；坐标文件另按大小与 JSON 形态校验，
  逐题逐选项的语义核对由学生端 `OptionRegionMap` 执行。
- `schemas/student-paper.schema.json` 的 `schemaVersion` 由 `const 1` 改为 `enum [1, 2]` 并写明语义。

验证（Windows、.NET SDK 10.0.401）：Release 构建 0 警告 0 错误；
`Category!=Postgres&Category!=Wpf` 共 **1507 项通过、0 失败、0 跳过**。其中本轮新增 8 项：

- 组包器 3 项：传入坐标产物产出 v2 与坐标文件；不传维持 v1 且无坐标文件；
  产物与清单不一致的七种情形（考试、试卷版本、坐标规范、产物版本、缺题、缺选项、清单外选项）
  全部拒绝且不写出任何文件。
- 端到端 1 项：D03 组包（v2、加密）→ 按学生端路径解密 → `StudentPaperPackage.ReadManifest`
  接受 v2 → 学生端 `OptionRegionMap.TryReadFromPackage` 读出并逐题逐选项核对通过。
  这一项同时证明 D03 的序列化格式与 B03 的读取实现彼此一致。
- 学生包边界 4 项：v2 缺坐标文件、v1 夹带坐标文件、v2 坐标文件非法 JSON 均被拒绝；
  v2 且坐标合法时被接受，并核对两侧各自定义的坐标文件名一致。

**仍未交付**：生产调用方（C05 导入流程、CLI）尚未传入坐标产物，实际发布仍走 v1；
B03 的 PDF 叠加控件与真实点击、版本下载恢复仍待完成。

## 剩余工作

- 抽取与 pageTopLeftNormalizedV1 归一化已由本轮交付；D/B 继续坐标 JSON 读写及公共 schema，同步扩展组包器、学生清单 v2 和学生读取器，不等待 B03 全部完成。
- 完成 B03 的 PDF 画面及坐标绑定，以及 C05、CLI 的实际接线。学生端已有包读取入口及题目清单，尚无完整答题画面。
- 完成版本入库、密钥下发、新旧版本独立下载及恢复验证。
- 完成真实试卷的版式、选项绑定和资源内容验收，以及 T01/T02 的对应场景。

上述剩余项继续由 #26 跟踪。代码及模块检查通过不代表这些场景已经验收。
