# B 组实施状态（B01）

本文只记录仓库当前可验证的实现，不替代任务卡和验收报告。风格与 [A 组实施状态](a-implementation-status.md)一致。

| 任务 | 当前已完成 | 尚未完成或需外部条件 |
| --- | --- | --- |
| B01 | 共享主题（颜色/字体/间距/尺寸/控件样式）、通用组件（页头、校验输入、状态胶囊、消息条、等待遮罩、确认弹窗、题号方框、组件样例页）、学生端六页面流程（激活、登录、选择考试、等待考试、考试进行页框、交卷成功页）、编号姓名统一显示、设备信息本地持久化、契约同构 Mock 服务层 | C 评审共享组件样例；A01 契约的服务端 HTTP 客户端接入（Mock 替换）；设备凭据 DPAPI 加密；M1 组件样例页两端并排验收 |

学生端当前全部业务数据来自 `MockStudentApi`（独立合成样例），不代表真实考试可用。以下为具体边界。

## 已实现

### 共享层 `src/Exam.Desktop.Shared`
- **主题**（`Themes/`）：颜色（含语义色：已作答绿 `#2E9E5B`、标记红点 `#E04B4B`、危险红 `#D64545`）、字体字号阶梯、间距/圆角/控件尺寸、按钮族/输入/表格/进度/滚动条样式、卡片与布局样式。所有页面统一合并 `Themes/Theme.xaml`，禁止业务页面硬编码颜色字号。
- **组件**（`Controls/`）：`PageHeader`（左上角编号徽标+姓名+班级，右侧操作插槽）、`ValidatedInput`（标签/水印/错误反馈，数字过滤含粘贴）、`StatusPill`、`Banner`（业务错误/提示条）、`BusyOverlay`（网络等待遮罩，所有网络等待可见）、`ConfirmDialog`（统一确认弹窗，危险操作红色确认键）、`QuestionBox`（题号方框：已答绿/标记红点/当前题描边）、`ComponentShowcasePage`（组件样例页，供 M1 验收与两端一致性核对）。

### 学生端 `src/Exam.Student`
- **激活页**：服务器 IP/端口输入校验 → 契约同构激活申请 + 轮询（同 RequestId 幂等提交）→ 批准后设备信息（deviceId/凭据/编号）持久化至 `%LOCALAPPDATA%\HuaizhouExam\Student\device.json`，服务器关闭不影响激活状态。
- **登录页**：姓名/班级/准考证号（后两项仅数字，含粘贴过滤）→ 登录前确认信息弹窗 → 名册不符（ROSTER_MISMATCH）显示服务器中文说明；底部小字“取消激活”。
- **选择考试页**：个人登录状态、按报名范围过滤的考试列表（状态胶囊）、进入考试三种结果（同意→等待页；ENTRY_NOT_ALLOWED→“当前不可进入该考试！”；RESOURCES_NOT_READY→“试题获取失败！”）；右上角红色“退出系统”确认后清除会话（无前一学生内容）。
- **等待考试页**：考试时间与倒计时、开考前 5 分钟下载提示、考试说明渲染（B01 为 Mock 合成文本）、右上角红色“返回”确认后退出考试。
- **考试进行页（页框）**：左侧试题切换栏（学科→题型→题号方框 WrapPanel，点击跳题，已答/标记/当前三态）、右侧题目区占位（B03 接入 PDF 与选项圆钮）、标记本题/取消标记、右上角红色“提交答卷”+服务器时间占位（B05 接入 60 秒校时）。
- **交卷成功页**：3 秒倒计时自动返回选择考试页。
- **app.manifest**：asInvoker（普通账户）+ PerMonitorV2 DPI（S17 100%/125%/150% 缩放验收基础）。

### 契约对接方式
`Core/IStudentApi`（`StudentApi.cs`）的请求/响应全部使用 `Exam.Contracts` 类型（`SubmitActivationRequest`、`LoginStudentRequest`、`PrepareExamEntryRequest`、`ApiEnvelope<T>`、错误码常量），页面按错误码映射提示文案、不解析中文文案分支——与执行指南约定一致。A 组 HTTP 客户端就绪后在 `AppServices` 替换 Mock 实现即可，页面不动。

## 验证记录

- `dotnet build Huaizhou.ExamSystem.sln`（Debug）：0 警告 0 错误（12 项目全部构建）。
- `dotnet test Huaizhou.ExamSystem.sln`（Debug）：76/76 通过（Unit 74 + Integration 2），未影响 A 组既有测试。
- 学生端启动冒烟：正常运行无启动崩溃（截图验证受环境限制未执行，待 GUI 评审时补）。
- 未执行：真实设备缩放显示核对、C 评审、S17 两端一致性并排核对（依赖 C01 接入共享组件）。

## 发现的契约缺口（需 A 确认）

1. **考试概览设备侧查询缺失**：`StudentSessionResult` 只含 `ExamIds`，学生端选择考试页需要考试名称/起止时间/科目/lifecycle 展示数据。当前由 `ExamOverview`（Mock）承担，建议 A 在契约中补充设备侧考试概览查询。
2. **考试说明内容下发契约缺失**：`ExamResourceDescriptor` 含 `EncryptedPackageUri` 与 `InstructionsVersion`，但无考试说明文本的获取途径（B01 暂以 Mock 合成说明渲染，B02 需要正式通道）。
3. **激活审批轮询**：按执行指南“重复请求返回已有结果”以幂等重提交实现轮询，请求 A 确认该用法是否符合 A02 设计意图。

## B01 完成判据对照

| 任务卡判据 | 状态 |
| --- | --- |
| 共享样例经 C 评审 | ⏳ 待 C 评审（组件样例页 + `docs/gui-spec.md`） |
| 所有页面使用同一主题 | ✅ 全部页面合并 `Theme.xaml`，无散落硬编码 |
| 编号稳定 | ✅ 编号激活后持久化，重启沿用 |
| 姓名切换正确 | ✅ 登录填充、退出清空 |
| 无前一学生内容 | ✅ 会话清除，考试列表按新会话重新加载 |
