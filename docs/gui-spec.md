# 共享 GUI 主题与组件规范

本规范用于两端界面一致性检查与维护。实现位于 `src/Exam.Desktop.Shared`，学生端与考务端全部页面（含考务端服务管理页）引用 `Themes/Theme.xaml` 一套资源，**禁止在业务页面硬编码颜色、字号与重复样式**；主题变更只在共享项目维护。

## 1. 使用方式

各端 `App.xaml` 合并一个入口字典即可获得全部主题：

```xml
<ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml" />
</ResourceDictionary.MergedDictionaries>
```

`Theme.xaml` 依次合并 `Colors.xaml`（颜色）→ `Typography.xaml`（字体字号）→ `Metrics.xaml`（间距/圆角/尺寸）→ `Icons.xaml`（线性图标）→ `Controls.xaml`（控件样式）→ `Components.xaml`（布局级样式）→ `Reporting.xaml`（成绩报表专用组件）。

## 2. 颜色语义（浅色主题）

| 资源键 | 值 | 用途 |
|---|---|---|
| `Brush.Primary` | `#1E5AA8` | 品牌主色、主按钮、当前题描边 |
| `Brush.Background` / `Brush.Surface` / `Brush.Surface.Alt` | `#F3F5F9` / `#FFFFFF` / `#F4F6FA` | 页面背景 / 卡片表面 / 次级表面（表格隔行、禁用、未作答题号） |
| `Brush.Border` / `Brush.Border.Strong` | `#E1E6EE` / `#C3CCD9` | 卡片与表格分隔 / 输入框描边 |
| `Brush.Text.Primary` / `.Secondary` | `#1B2433` / `#5F6B7E` | 主文本 / 辅助文本 |
| `Brush.Nav.*` | 深蓝 `#132A4C` 系 | 仅用于考务端侧栏导航外壳（背景、悬停、选中、强调条、文字） |
| `Brush.Brand.Gradient` | `#2A6CC4` → `#173F78` | 登录/等待页卡片顶部品牌带与侧栏标识，只作装饰，不承载语义 |
| `Brush.Scrim` | 白色 95% | 撤权等整页遮罩 |
| **`Brush.Answer`** | `#2E9E5B` | **已作答语义（题号方框绿色填充）** |
| **`Brush.Mark`** | `#E04B4B` | **私人标记语义（题号右上角红点）** |
| **`Brush.Danger`** | `#D64545` | **危险操作语义（提交答卷 / 退出系统 / 返回按钮）** |
| `Brush.Success` / `Brush.Warning` / `Brush.Info` | 绿/橙/蓝 | 状态反馈 |
| `Brush.*.Soft` | 各对应浅色 | 状态胶囊、消息条底色 |

语义色为全系统统一约定：**已答=绿、标记=红点、提交/退出=红**，不允许页面另配颜色。

## 3. 字体与字号

- 界面字体：`Font.Family` = Microsoft YaHei UI（回退 Microsoft YaHei / SimHei / SimSun）。
- 字号阶梯：`Font.Size.Caption` 12 / `Body` 14 / `Body.Large` 16 / `Subtitle` 18 / `Title` 22 / `Display` 32。
- **试题 PDF、题内字体图片及相对排版按原 LaTeX 由题目包自身决定，主题不作用于试题内容**；考试说明文本按需求要求（说明黑体、数字 Times New Roman 类）由考务端说明文本自身携带。

## 4. 尺寸与间距

- 间距阶梯：4 / 8 / 12 / 16 / 24 / 32（`Spacing.XS`…`Spacing.XXL`）。
- 圆角：`Radius.SM` 4（输入/小按钮）、`Radius.MD` 6（按钮）、`Radius.LG` 10（卡片）、`Radius.Pill` 11（胶囊，取控件高度一半；WPF 中过大的半径会画成椭圆）。
- 卡片内边距 `Padding.Card` 20；页面外边距 `Padding.Page` 28,24,28,28；考务端侧栏宽 `Nav.Width` 232。
- 控件高度：`Control.Height.Small` 28 / `Control.Height` 36 / `Control.Height.Large` 44。

## 5. 控件样式（Controls.xaml）

| 样式键 | 说明 |
|---|---|
| `Btn.Primary` / `Btn.Secondary` | 主/次按钮，含 Hover、Pressed、Focus 环、禁用态 |
| `Btn.Danger` | 红色危险按钮（右上角提交答卷 / 退出系统 / 返回） |
| `Btn.Ghost` | 小字链接型按钮（辅助操作） |
| TextBox 隐式样式 | 36 高、圆角、聚焦主色描边、校验错误红框 |
| DataGrid 隐式样式 + `TableFooterRow` | 表格及固定平均分行（供成绩报表使用） |
| ProgressBar / ScrollBar | 统一细样式 |
| ComboBox / ComboBoxItem 隐式样式 | 圆角下拉框、箭头随展开翻转、弹出层阴影；页面不得另写下拉框模板 |
| PasswordBox / CheckBox / ToolTip 隐式样式 | 口令框与文本框同外观；复选框为圆角方框 + 勾选；提示为深色气泡 |
| `Nav.TabControl` / `Nav.TabItem` | 考务端左侧深色导航外壳：`TabItem.Header` 为页面名、`TabItem.Tag` 为图标几何；`NavShell.TopBar` 附加属性放跨页常驻内容（服务器连接条）。导航项的字重与颜色只作用于标题，不会被页面内容继承 |

## 6. 组件（Controls/）

| 组件 | 职责 |
|---|---|
| `PageHeader` | **全端统一左上角“编号徽标 + 姓名 + 班级”区域**，右侧 `RightContent` 插槽放页面右上角操作 |
| `ValidatedInput` | 标签 + 水印 + 错误文本；`IsNumericOnly` 供班级/准考证号仅数字（含粘贴过滤） |
| `StatusPill` | 圆点胶囊状态标识（Info/Success/Warning/Danger/Neutral） |
| `Banner` | 业务消息条（“当前不可进入该考试！”“试题获取失败！”等） |
| `BusyOverlay` | **网络等待遮罩：所有网络等待必须可见**，转圈动画 + 文案 + 阻断输入 |
| `ConfirmDialog` | 统一确认弹窗（登录确认、提前交卷、退出系统等），`isDanger` 切红色确认键 |
| `QuestionBox` | 题号方框：已答绿色填充 / 标记右上角红点 / 当前题主色描边，点击跳题 |
| 布局样式（`Components.xaml`） | `PageTitle`/`PageSubtitle` 页头，`FieldLabel` 表单标签，`Card`/`Card.Flat` 卡片，`IconTile`/`IconTile.Glyph` 图标块，`Hint` 提示条，`TopBar` 顶部条，`BrandBand` 品牌带，`BoolToVis` 布尔可见性转换器 |
| 图标（`Icons.xaml`） | `Icon.*` 24×24 线性几何，配合 `Icon.Stroke` 或 `IconTile.Glyph`；图标只作识别辅助，含义始终由相邻文字表达 |

## 7. 变更流程

主题与组件修改在学生端、考务端和服务器管理页面同步验证100%、125%、150%缩放，对应验收场景S17。任何页面新增的通用控件先下沉到 `Exam.Desktop.Shared`，再由页面引用，不复制样式。


## 8. 界面截图与资源检查

- `XamlResourceReferenceTests`（学生端单元测试，普通 CI 执行）扫描三端 XAML，要求每个 `StaticResource`/`DynamicResource` 键都在共享主题或同一文件内定义；缺失的 `StaticResource` 只在模板实例化时才抛异常，构建发现不了。
- 两端截图测试只在设置 `EXAM_UI_SCREENSHOT_DIRECTORY` 时生效，未设置时直接通过。用真实页面和合成数据渲染 100% / 125% / 150% 截图，考务端另截窗口最小尺寸；学生端答题页用 XeLaTeX 现场编译合成中文题，因此同时标记 `Wpf` 与 `Latex`。WPF 每个进程只能创建一次 Application，考务端截图须单独运行：

```powershell
$env:EXAM_UI_SCREENSHOT_DIRECTORY = "<仓库外的目录>"
dotnet test tests/Unit/Exam.AdminUnitTests --configuration Release --filter "FullyQualifiedName~UiScreenshotTests"
dotnet test tests/Unit/Exam.StudentUnitTests --configuration Release --filter "FullyQualifiedName~UiScreenshotTests"
```

截图只是渲染复核，不能代替真实设备、物理 DPI 或现场交互验收；截图与绑定告警记录写到仓库外，不提交。

## 9. 服务器管理页面

服务器管理正常复用考务登录；登录页另有“服务器管理 / 故障接管”入口，业务不可用时使用原账号独立登录目标备机。页面显示主备节点，点击节点行填入“准备变为主服务器的节点”，备注选填。接管操作继续核对旧主隔离及备用副本条件，取消三个常驻勾选框和原接管说明小字。退出、换账号或会话失效时清除管理权限。

“当前管理服务器”同时列出已配置的主机、备机与地址。“服务器状态”分别显示主机和备机的运行情况，不展示代次，详细检测结果说明异常原因。“自动备份与磁盘”独立成框，位于“服务器状态”下方。主备保护、自动备份与磁盘告警只在服务器管理页显示。节点行在首次进入和刷新失败时也要保留，缺少实时数据时标明未核实及最近观测时间，不能刷新成空表。页面右上角统一“刷新”节点、状态、备份及日志。

页面不显示请求编号输入框、“查询请求处理状态”按钮或顶部请求处理状态。原请求记录和自动核对保留，人工查询、服务启停、原请求重放及恢复续行由正式管理 API 和 CLI 提供。页面不放置“启动业务”“停止业务”“按原内容重试”和“续办原恢复”按钮。

取消“服务器配置”“备份与恢复”框、“切换管理服务器”按钮及其展开界面和用户操作功能；部署和恢复改由管理 API、CLI 及部署配置文件处理。取消“重建目标服务器旧节点”“恢复同步保护”和“刷新状态与日志”按钮。

“主备运行与故障接管”“操作与事件日志”标题与“服务器状态”采用同一字体和字号，保持展开，不提供收起按钮。日志包含请求编号或事件编号、类别、阶段或事件级别、结果，便于查询对应处理状态。

恢复 API 和 CLI 沿用原有完整性检查：历史备份缺少后续已确认数据时不能开放业务，失败、条件未满足或业务尚未就绪不能显示恢复完成。移除界面入口不改变恢复数据保护要求。
