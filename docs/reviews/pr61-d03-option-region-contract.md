# PR #61 合并前审查记录

2026-09-27，A 在已确认路线 A 和试卷拆分完成状态的基础上，授权审查、修正并合并 PR #61。
审查以当时最新 main 为基线，保留 D 的两个原始提交及 A 的契约决定提交。
本轮由 A 单独审查，没有将自己的修改记为他人批准。

## 审查结论与修正

本 PR 可按契约与合成样包交付，D03 抽取器、坐标文件读写、学生清单 v2 和 B03 接入继续由 #26/#11 跟踪。
路线 A、三参数标签宏、题内零起页索引、同包加密及版本匹配约定保持一致。

- 核对 requirements.md 的原版显示与字母处圆钮要求，16 个题文件的 64 处标记均保留原题文和选项标签。
  三套样式的宏定义一致，注释补充只包标签及标签单行约束。
- 核对 `StudentPackageBuilder`、`StudentPaperPackage`、`StudentPackageLayout` 及现有显示/学生清单 schema。
  当前 v1 只接受原有文件集合，坐标 JSON 仍由未来 v2 专用入口读取，文档明确同步升级读写双方，未放宽现有校验。
- 核对原始 `/Rect`、PdfPig 已变换矩形、CropBox 与 Rotate 的区别。契约只对原始矩形进行一次变换，
  同名跨页及分离标记拒绝合并，按题目标识和选项标识匹配，保证记录顺序不会改变答案绑定。
- 修正原实验描述中的事实错误。两页样例由显式 `\newpage` 换页，不能据此证明插图导致自然分页。
  将 PdfPig 的有限反射检索和 CJK 探针现象限定在实际证据范围，不推广为整个库的能力结论。
- 核对拆题完成状态与 #29、总计划和执行指南一致。材料样式适配、依赖、对照、CLI 与验收继续跟踪，未关闭 D06。

没有新增运行时 C#、schema、数据库或授权变更。本 PR 没有改动计分、交卷、幂等及主备恢复行为。
旧学生包和本地答卷保持原义；未来 v2 必须在完整验证后切换，新包失败保留可恢复旧版。

## 本地构建和模块检查

环境为 Windows、.NET SDK 10.0.401、TeX Live 2025 / XeTeX 0.999997。
执行以下命令，结果与完整控制台日志核对。

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release
dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release -m:1 --filter "Category!=Postgres&Category!=Wpf"
dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release -m:1 --filter "Category=Wpf"
```

锁定还原成功，Release 构建 0 警告、0 错误。核心模块测试包含真实 TeX 类别，共 1269 项通过；
WPF 类别另有 2 项通过。合计 **1271 项通过，0 失败，0 跳过**。
本地没有运行 PostgreSQL 类别，交由最终提交的独立 CI 验证，结果链接写入 PR。

测试输出另使用 TRX logger 和系统临时 results 目录。各项目共用文件名时 TRX 会顺序覆盖，
本记录以保留的各项目完整控制台汇总计数，没有把最后一份 TRX 当作全部结果。

## 独立合成 PDF 验证

所有输入均来自仓库中的合成样包。main 的旧样式和 PR 样式分别编译相同四道题，使用以下参数，
产物全部写到系统临时目录，样包及真实材料目录没有写入生成文件。

```text
xelatex -no-shell-escape -interaction=nonstopmode -halt-on-error -output-directory=<临时输出目录> questions/<合成题名>.tex
```

| 合成题 | 覆盖内容 | 结果 |
| --- | --- | --- |
| ordering / q-d-001 | 普通字母标签、排序样式 | 1 页、4 个选项目标 |
| three-types / q-a-005 | 第三套样式、五选项题 | 1 页、5 个选项目标 |
| figures-chemistry / q-c-051 | 图片、显式分页 | 2 页、4 个选项目标均在 pageIndex 1 |
| figures-chemistry / q-c-053 | 中文括号标签、化学式 | 1 页、4 个选项目标 |

17 个目标均为内部 GoTo Link，边框为 `[0, 0, 0]`，目标存在且与注解位于同一页。
main 与 PR 共 5 页在相同 1.5 倍渲染尺度下逐像素一致，并逐页检查了中文、公式、图片和选项位置。
该结果证明这四个样例的可见版式未因链接宏改变，不推广为全部真实材料均已验证。

另从普通标签 PDF 生成 Rotate 0/90/180/270 与无裁剪/带偏移 CropBox 的 8 个变体，
在临时 .NET 工程中用 PdfPig 0.1.16 读取原始注解并执行契约公式。
对原始 4 个 PDF 和 8 个变体共 49 个选项区域，与 MuPDF 独立解析的显示坐标比较，
最大归一化差异约为 `4.14e-8`，均小于本次对照容差 `1e-6`。
对照容差只用于两种 PDF 库的数值比较，没有放宽契约的有效区域校验。

临时探针初次编译因 `DictionaryToken.Get` 调用缺少 scanner 参数失败，改用适用于该直接字典的 `TryGet` 后编译并运行通过。
该错误发生在仓库外的验证工具，项目 Release 构建未因此失败。临时探针并非已交付的 D03 抽取器，未新增项目依赖。

## 合并与未覆盖范围

最终提交应通过 Windows 与 PostgreSQL CI 后合并，不关闭 #26、#11 或 #29。
PR 说明和评论记录最终提交与 CI 链接，保留作者原实验与本轮独立检查的来源区别。

本轮未覆盖生产抽取器的失败路径、组包 v2 的真实接入、Windows.Data.Pdf 与 WPF 的选项叠加、
多行/表格/含图选项的交互、滚动及 DPI 点击，也未核验真实试卷材料或执行 T01/T02。
这些属于后续实现及验收范围，本 PR 的 WPF 基础测试和临时 PDF 探针不能替代。
