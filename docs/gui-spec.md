# 共享 GUI 主题与组件规范（B01）

本文档供交叉评审（C）与全队接入使用。实现位于 `src/Exam.Desktop.Shared`，学生端与考务端全部页面（含考务端服务管理页）引用 `Themes/Theme.xaml` 一套资源，**禁止在业务页面硬编码颜色、字号与重复样式**；主题变更只在共享项目维护。

## 1. 使用方式

各端 `App.xaml` 合并一个入口字典即可获得全部主题：

```xml
<ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml" />
</ResourceDictionary.MergedDictionaries>
```

`Theme.xaml` 依次合并 `Colors.xaml`（颜色）→ `Typography.xaml`（字体字号）→ `Metrics.xaml`（间距/圆角/尺寸）→ `Controls.xaml`（控件样式）→ `Components.xaml`（布局级样式）。

## 2. 颜色语义（浅色主题）

| 资源键 | 值 | 用途 |
|---|---|---|
| `Brush.Primary` | `#1E5AA8` | 品牌主色、主按钮、当前题描边 |
| `Brush.Background` / `Brush.Surface` | `#F4F6FA` / `#FFFFFF` | 页面背景 / 卡片表面 |
| `Brush.Text.Primary` / `.Secondary` | `#1F2937` / `#667085` | 主文本 / 辅助文本 |
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
- 圆角：`Radius.SM` 4（输入/小按钮）、`Radius.MD` 6（按钮）、`Radius.LG` 10（卡片）、`Radius.Pill`（胶囊）。
- 控件高度：`Control.Height.Small` 28 / `Control.Height` 36 / `Control.Height.Large` 44。

## 5. 控件样式（Controls.xaml）

| 样式键 | 说明 |
|---|---|
| `Btn.Primary` / `Btn.Secondary` | 主/次按钮，含 Hover、Pressed、Focus 环、禁用态 |
| `Btn.Danger` | 红色危险按钮（右上角提交答卷 / 退出系统 / 返回） |
| `Btn.Ghost` | 小字链接型按钮（登录页“取消激活”等） |
| TextBox 隐式样式 | 36 高、圆角、聚焦主色描边、校验错误红框 |
| DataGrid 隐式样式 + `TableFooterRow` | 表格及固定平均分行（供成绩报表使用） |
| ProgressBar / ScrollBar | 统一细样式 |

## 6. 组件（Controls/）

| 组件 | 职责 |
|---|---|
| `PageHeader` | **全端统一左上角“编号徽标 + 姓名 + 班级”区域**，右侧 `RightContent` 插槽放页面右上角操作 |
| `ValidatedInput` | 标签 + 水印 + 错误文本；`IsNumericOnly` 供班级/准考证号仅数字（含粘贴过滤） |
| `StatusPill` | 圆点胶囊状态标识（Info/Success/Warning/Danger/Neutral） |
| `Banner` | 业务消息条（“当前不可进入该考试！”“试题获取失败！”等） |
| `BusyOverlay` | **网络等待遮罩：所有网络等待必须可见**，转圈动画 + 文案 + 阻断输入 |
| `ConfirmDialog` | 统一确认弹窗（登录确认、提前交卷、退出系统、取消激活等），`isDanger` 切红色确认键 |
| `QuestionBox` | 题号方框：已答绿色填充 / 标记右上角红点 / 当前题主色描边，点击跳题 |
| `ComponentShowcasePage` | **组件样例页（M1 验收用）**，覆盖上述全部组件与语义色，两端一致性核对入口 |

## 7. 变更流程

主题与组件变更走任务分支 + PR（`codex/b01-gui-…`），B 牵头修改共享项目、C 评审，两端页面同步验证 100% / 125% / 150% 缩放（对应验收用例 S17）。任何页面新增的通用控件先下沉到 `Exam.Desktop.Shared`，再由页面引用，不复制样式。

实现状态与验证记录见 [B 组实施状态](b01-implementation-status.md)。
