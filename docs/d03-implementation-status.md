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

## 剩余工作

- D/B 共同明确并由 A 确认选项坐标契约，继续实现隐藏标记、区域提取和 pageTopLeftNormalizedV1 归一化，不等待 B03 全部完成。
- 完成 B03 的 PDF 画面及坐标绑定，以及 C05、CLI 的实际接线。学生端已有包读取入口及题目清单，尚无完整答题画面。
- 完成版本入库、密钥下发、新旧版本独立下载及恢复验证。
- 完成真实试卷的版式、选项绑定和资源内容验收，以及 T01/T02 的对应场景。

上述剩余项继续由 #26 跟踪。代码及模块检查通过不代表这些场景已经验收。
