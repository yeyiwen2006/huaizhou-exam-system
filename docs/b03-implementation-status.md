# B03 原版题目与选项：实现与审查边界

## 当前开发状态（2026-09-29复核）

B03 #11按模块开发完成关闭：WinRT原版PDF、v2选项区域读取/点击、导航、缩放/旋转、私人标记与B04保存接线已有实现；本轮再次通过真实WPF模块测试。旧记录中的“只支持v1/没有选项坐标”不再成立。C05真实导入发布归#21，B06未完成异常闭环归#14，物理设备/DPI与真实题目验收归T01/T02。

非T开发任务在本卡范围的实现、必要接线、适用模块测试及A审查合并完成后关闭；不等待T01/T02、最终多机实测或整项目打包。仍缺本卡所属功能或生产入口则保留打开，并逐条列出开发缺口。最终整体测试及交付独立归T01/T02/T03；开发卡关闭不表示这些验收通过。 规则见[开发卡关闭与阶段验收](development-closure-policy.md)。

## 历史交付记录

> 以下保留当时的交付和测试记录；涉及旧版本、旧依赖或旧关闭要求的描述不作为当前状态。当前缺口及归属以上述复核和GitHub任务卡为准。

关联 #11。2026-09-27 A 审查；以已合并的 B05 修复和最新主线联测，PR #69 仅为原版显示及坐标交互基础的部分交付。不得据此宣布整个 B03、D03、B06 或 T01/T02 完成。

## 实现与修正

- 原逐题 PDF 由 Windows.Data.Pdf 渲染，多页保持原顺序；不从文字或 OCR 重绘试题、不替换字体或重排表格。视图统一等比缩放并滚动，选项使用 D03 pageTopLeftNormalizedV1 坐标及清单选项标识。
- 审查修正：渲染基准改为 WinRT 已应用 CropBox/Rotate 的显示尺寸，Image 和 Canvas 同用实际位图像素宽高；不再用 MediaBox 比例错误叠加。双边渲染尺寸受 2200 像素限制，原图和圆钮统一缩放。
- 圆钮中心取区域中心；不作答置于原选项后，与普通选项互斥。普通点击经 OptionSelection 和 B04 AnswerSession 可靠保存；导航按科目、题组、数字题号显示，已答题变绿、私人标记为红点。
- 坐标严格校验考试/版本、方案、每题每选项恰好一次、显式必填字段、页码、有限数值及归一化边界；拒绝缺失字段偷偷默认零、重复 JSON/ZIP 入口、缺失/多余选项。验证后的坐标不可通过只读接口反向修改；渲染立即复制传入映射，隔离调用方后续改动。
- 页码或 PDF 无效时整份显示失败，不创建部分可点击控件。渲染中收到禁用、清空或取消后，迟到结果不能重新启用按钮或复活题目。没有坐标时连不作答也不能启用，不猜测点击位置。
- 页面按试卷、渲染和会话身份隔离异步结果；试卷失效即时清空导航、坐标和图像。答案/标记保存期间同步禁用交互，完成后只更新同一题和会话；跳题渲染未结束前不接受下一次编辑。恢复保存后从可靠快照刷新圆钮和标记。
- 语义解决与 #66 的页面冲突，保留已合并的真实回执契约、END 固定、可靠回执保存、原请求恢复、独立周期校时、Loaded 启动/卸载清理、防重复确认以及原答卷补传。INPC 提交按钮绑定替代命名作用域 Loaded 方案，不丢失原保护。
- 调用方 PDF 副本和临时 PNG 托管字节使用后清零；显示位图在展示期间保留，清空时释放视图引用。不声称清除了全部 WinRT/WPF/操作系统内部缓存。

## 审查验证

先在原 PR 的未改生产代码上新增 15 项回归，14 项失败、1 项清空隔离通过。复现错误比例/旋转/裁剪叠加、渲染中禁用失效、页码错误仍创建部分按钮、必填零值字段缺失及可变坐标。

修正后扩展为 26 项审查回归：9 项坐标边界测试，以及 17 项真实 WPF/WinRT 控件测试。四种旋转 × 有无裁剪八组均核对实际位图比例、圆心命中原 PDF 合成黑块及放大后的 WPF 命中目标；三组多页缩放/滚动验证第二页选项身份和选中状态。另验证中途禁用、清空、取消、调用方改动及无坐标不作答保护。

这些窗口在屏幕外独立 STA 调度器运行，均关闭；不创建全局 Application，不读取用户试卷或切换用户窗口。最初无 PresentationSource 的点击测试不足，已改为真实 WPF 窗口后执行 InputHitTest。测试产物全部为动态生成且具有正确 xref 的合成 PDF，不是伪造 PDF 文件头。

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release
dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release -m:1 --filter "Category!=Postgres&Category!=Latex&Category!=Wpf" --logger "trx;LogFileName=pr69-final.trx" --blame-hang-timeout 90s --blame-hang-dump-type none
dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release -m:1 --filter "Category=Wpf" --logger "trx;LogFileName=pr69-final-wpf.trx" --blame-hang-timeout 90s --blame-hang-dump-type none
```

最终完整基线计数、头提交及 Windows/PostgreSQL CI 记录在 PR 审查评论；未全部通过不合并。TRX 留在本地 TestResults，不提交仓库。

## 未完成门槛

当前生产组包/读取仍为 v1，不能携带 option-regions.json；因此已有合法 v1 包仍只能原版显示，不能坐标作答。本 PR 不放宽 v1 安全白名单，也不声称部署后可正式答题。D03 v2 交付后仍需真实组包→传输→授权解锁→坐标读取→点击→持久化→交卷的全链路联调，不能仅凭机会式读取代码声称会自动通过。

不同实体 DPI/显示缩放、真实复杂公式表格与图片、完整页面版本切换与通知、XeLaTeX、真实两端考试、300 客户端及 T01/T02 均未在本次完成。当前合成控件测试不能替代这些验收。Windows.Data.Pdf 接口要求学生端及其测试目标为 net10.0-windows10.0.19041.0，锁文件不引入浮动版本。

## 一次未复现的既有组包测试失败

首次完整非 GUI 回归中，未被 #69 修改的 `Build_ResourceDirectoryIsNotAFileAllowlist` 一次返回 `EncryptionFailed`。该用例实际不启用加密；这是组包文件操作/参数/密码操作的共同失败码，不能仅凭错误码断言密码算法故障。原失败保留为 `tests/Unit/Exam.UnitTests/TestResults/pr69-initial-failed.trx`。

临时独立诊断测试使用原有夹具和用例，监听相关 first-chance 异常，执行 100 次串行及 10 批 × 10 次并行，共 200 次，全部通过，未复现该失败。日志为 `pr69-packaging-probe.trx`；临时诊断源码已移除，未提交，也未改变组包生产实现或放宽断言。根因尚未确定，不将其声称为已修复。随后重新构建并要求两轮完整非 GUI、完整 WPF 和最新头提交 CI 全部通过，结果记录在 PR 评论。
