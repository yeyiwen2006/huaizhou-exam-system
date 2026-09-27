# B03 原版题目与选项 · 实现状态

- 任务：B03 原版题目与选项（[Issue #11](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/11)）
- 日期：2026-09-27（深夜）
- 基线：main HEAD `b9415ad`（PR #66 之上）
- 依据：[D03 选项定位契约](d03-option-region-contract.md)（路线 A、`anchorScheme = pageTopLeftNormalizedV1`、`option-regions.json` 包内文件名）；需求·答题一节

## 一、交付内容

### 1. 原版题目显示（Windows.Data.Pdf）

- `src/Exam.Student/Views/PdfQuestionView.xaml(.cs)`（新增）：
  - 逐题 PDF 经 WinRT `Windows.Data.Pdf` 渲染（页面尺寸 DIP×2 ≈ 192 DPI），**保留多页**、原字体/公式/图片/表格与选项排列；
  - **只允许整体等比缩放与滚动**：`LayoutTransform` 统一 ScaleTransform，默认适应宽度，+ / − / 重置 / Ctrl+滚轮，缩放范围 0.4–3.0；
  - 选项字母处叠加**圆形选择控件**：圆心默认取归一化矩形中心（契约 4.3），视觉圆 24px、命中范围 36px（圆钮直径及命中范围由 B03 自定，不改写坐标产物）；未选空心圆环、选中绿色实心（语义色 `Brush.Answer`）；
  - "不作答"按钮位于原选项之后（页面内容流末尾），与普通选项互斥，选择/取消颜色反馈一致；
  - 明文 PDF 副本渲染完成后即清零，不留驻托管内存。
- TFM：`Exam.Student`、`Exam.StudentUnitTests`、`Exam.StudentIdentityTests` 由 `net10.0-windows` 升级为 `net10.0-windows10.0.19041.0`（WinRT 投影 `Microsoft.Windows.SDK.NET`，要求 Windows 10 19041+）；`packages.lock.json` 已 `--force-evaluate` 重新生成并过 `--locked-mode`。

### 2. 选项坐标（D03 契约 4.4 学生侧）

- `src/Exam.Student/Core/OptionRegions.cs`（新增）：
  - `OptionRegionMap.TryReadFromPackage`：从解锁后的包内容读取根目录 `option-regions.json`；**文件缺失返回 null（v1 包无坐标，不猜测点击位置）**；
  - `Parse` 严格校验：UTF-8、重复 JSON 字段、`artifactVersion = 1`、`anchorScheme = pageTopLeftNormalizedV1`、考试/试卷版本与清单一致、每题每选项**恰好一次**（缺失/重复/清单外均整包失败）、数值有限、`x/y ≥ 0`、`width/height > 0`、`x+width ≤ 1`、`y+height ≤ 1`、`pageIndex ≥ 0`；
  - `ValidatePageCount`：显示时按该题 PDF 实际页数核对，越界即整包失败（错误信息含题号、选项与页数）；
  - 圆钮按**选项标识**绑定（`options[i]` 语义），不按注解遍历顺序或坐标排序绑定答案（契约 4.2）。

### 3. 左侧导航与作答状态

- `src/Exam.Student/Core/PaperNavigator.cs`（新增）：科目按清单顺序、题组按 `Order`、题号方框按**数值升序**（避免 1、10、2 字典序）；全卷题号唯一跨科不重置；已作答（普通选项或"不作答"）题号变绿、取消全部选择恢复未作答色；私人标记右上角红点；当前题主色描边。点击跳题。
- `src/Exam.Student/Core/OptionSelection.cs`（新增，纯函数）：单选选新项替换旧项、再点已选项取消；多项/不定项逐项选中取消（快照按清单顺序稳定输出）；"不作答"与普通选项互斥。

### 4. 考试页接线（`ExamInProgressPage` 重构）

- 解锁后（`runtime.Paper` 可用）自动建导航、加载坐标、渲染当前题；当前题恢复自 B04 会话（`Answers.CurrentQuestionId`），跳题写回 B04（`SetCurrentQuestionAsync`）；
- 圆钮/不作答点击 → `OptionSelection` 计算下一状态 → `AnswerSession.SetAnswerAsync` 可靠落盘；写入失败保持原状态展示；
- "标记本题/取消标记" → `ToggleMarkAsync`；提交按钮仅在 `Answers.Status == Active` 且未固定时可用（修复此前按钮恒禁用的遗留问题，经 INPC 绑定绕开 PageHeader 命名作用域）；
- B04/B05 状态保留并移入底部状态条；坐标整包失败/冻结提交/解锁横幅分级展示；
- 试卷版本切换（B06 场景）以清单对象变化检测重建，答案会话失效逻辑沿用 B04。

## 二、验证

- 全解决方案构建 Debug + Release 均 0 警告 0 错误；`restore --locked-mode` 通过；
- **学生端 174/174**（142 既有 + 30 B03 逻辑 + 2 WinRT 管线）；身份 25/25、管理 76/76、负载 22/22；
- 新增测试：
  - `OptionRegionMapTests`（14）：契约正反用例（有效绑定、缺/多选项、清单外题、重复、版本/考试不符、越界/非有限数值/负页码、重复 JSON 字段、坏 UTF-8、ZIP 入口缺失返回 null、页数核对）；
  - `PaperNavigatorTests`（3）：科目/题组/题号排序、作答/标记/当前状态、按标识查找；
  - `OptionSelectionTests`（8）：单选替换与取消、多选/不定项逐项与顺序稳定、不作答互斥、未知选项/未启用拒绝、全部可达状态过 `AnswerSheetContract`；
  - `PdfRenderPipelineTests`（2）：动态构造带精确 xref 的最小双页 PDF，验证 WinRT 加载/GetPage(1)/渲染出 PNG（管线可用性）；假 PDF 头被拒绝不崩溃；
- 启动冒烟：Release 产物以 `DOTNET_ROOT` 启动，进程存活且主窗口"怀籀传承在线考试系统 · 学生端"正常显示。

## 三、已知边界与后续

1. **v1 包无坐标时不作答**：当前 D03 尚未交付 v2 组包器/读取器，现存包均为 v1（无 `option-regions.json`），B03 显示原版 PDF 并明确提示"未提供选项坐标，不能作答"；D 的 v2 包上线后坐标作答自动启用，无需改 B03（机会式读取 + 严格校验）。注意：现有 v1 共享读取器会拒绝含 `option-regions.json` 的包，v2 读取器归 D03 侧交付。
2. **显示与点击复测**：旋转（0/90/180/270）× 裁剪、多页、缩放、滚动、不同 DPI 的点击绑定复测按契约由 D/B 联合执行，尚未开始；本 PR 的 WinRT 管线测试仅覆盖加载/渲染可用性。
3. WinRT `PdfPageDimensions` 单位实测为 DIP（1/96 英寸，pt×4/3），已在渲染基准中注明；契约中 PdfPig 侧坐标为归一化值，不受此单位差异影响。
4. 多行选项、表格选项、含图选项的显示验证依赖真实材料（D06 跟踪），合成样包尚未含此类结构。
