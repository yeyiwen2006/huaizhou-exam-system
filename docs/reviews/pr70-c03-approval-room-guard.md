# PR #70：C03 审批试场输入边界审查

2026-09-28，按 A 的审查、修改、测试与合并任务复核。原提交 `64b8212d14fa1cd6b902b987ad2b85e2c7ad4e43`，基线 `dce4d97`。

## 范围与接口

核对 `requirements.md` 的激活、编号与试场分组要求，并检查 `ActivationRoomViewModel` → `AdminApiClient.ApproveActivationAsync` → `ActivationService.ApproveAsync` 的实际调用链。当前没有权威试场组 CRUD/查询/成员校验接口，本 PR 的临时限制仅阻止考务 GUI 将手填或本地草稿组 ID 送入审批；不宣称完成服务端分组校验，也不改变现有 HTTP/CLI 契约。

空白组统一发送 `null`，首次审批由服务器归入未分组。重复审批必须显示服务器返回的原设备、编号和原组，不能将请求中的 `null` 当作清空原组的命令。服务端拒绝时不得新增设备或显示成功。

## 补充修改

新增 `ActivationApprovalBoundaryTests` 的 8 个测试实例：空值、空串、空白组的正式 HTTP 请求；已配置连接下拒绝两种非空输入；拒绝真实创建的本地组草稿 ID；重复审批保留权威原组且不重复插入列表；服务器拒绝不产生虚假成功。原有 ViewModel 拦截和 XAML 禁用输入保持不变。

## 本次实际测试

环境：Windows，.NET SDK 10.0.401，全部使用合成数据。

- `dotnet restore Huaizhou.ExamSystem.sln --locked-mode`：退出 0。
- `dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release`：退出 0，0 警告、0 错误，未关闭 NuGet 审计。
- 考务测试项目 `FullyQualifiedName~Activation`：14/14 通过。
- 解决方案 Release、`-m:1`、`Category!=Postgres&Category!=Latex&Category!=Wpf`：1470/1470 通过（1076 + 24 + 24 + 206 + 118 + 22）。
- 解决方案独立 `Category=Wpf`：19/19 通过（1 + 17 + 1）。

本地完整文本日志位于被 Git 忽略的 `artifacts/pr-review-20260928/pr70-*.log`。首次全量测试显式使用同名 TRX，导致不同测试项目的 TRX 被覆盖；上述逐项目数量来自保留的完整文本日志，不把最后一个 TRX 当作全量证据。后续回归使用自动命名 TRX。

## 限制与合并条件

本机未配置 `EXAM_TEST_POSTGRES`，Docker Linux 引擎未运行，没有运行本地 PostgreSQL 测试；合并前须重新核对补充提交对应的 Windows 和 PostgreSQL CI。WPF 自动测试不等同于人工 GUI、真实设备或多机验收，本轮未进行这些验收。#46 保持打开，服务端权威试场管理仍需后续交付。

回退可通过 revert 本 PR 完成，无数据库迁移；回退会重新暴露 GUI 接收未校验组 ID 的旧行为。不存在为了通过测试而放宽安全校验或删除既有断言的修改。
