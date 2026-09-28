# B05 同步、固定交卷和回执恢复：实现与审查边界

## 当前开发状态（2026-09-29复核）

B05 #13按模块开发完成关闭：实际HTTP同步/交卷、固定提交重试、永久回执核验、结束/撤销/重启恢复和客户端交互已接线并通过本轮回归。B06失焦留痕等其他功能仍归#14，真实主备切换归F03 #16；不等待最终300会话或现场时限才能关闭本卡。

非T开发任务在本卡范围的实现、必要接线、适用模块测试及A审查合并完成后关闭；不等待T01/T02、最终多机实测或整项目打包。仍缺本卡所属功能或生产入口则保留打开，并逐条列出开发缺口。最终整体测试及交付独立归T01/T02/T03；开发卡关闭不表示这些验收通过。 规则见[开发卡关闭与阶段验收](development-closure-policy.md)。

## 历史交付记录

> 以下保留当时的交付和测试记录；涉及旧版本、旧依赖或旧关闭要求的描述不作为当前状态。当前缺口及归属以上述复核和GitHub任务卡为准。

关联 #13。2026-09-27 在 PR #66 原实现上完成审查修正。代码合并不代表 B03 正式题面交互、B06 全部通知、T01 或 T02 验收完成，不自动关闭任务卡。

## 实际接线

- `HttpStudentApi` 对齐 A05 正式路由：进度返回 `PersistedProgress`，交卷和原回执查询均返回扁平 `SubmissionReceipt`，不再错误地反序列化为完整 `FinalSubmission`。
- 只上传 B04 SQLite 同事务持久化的 `PendingSnapshot`，身份、版本、完整答案、导航和请求 ID 来自同一可靠记录。私人标记不上传。修改保存成功立即唤醒，同一快照每 60 秒重报；网络重试和真实运行时重启保留原请求 ID。
- 页面启动独立的 60 秒服务器校时任务，慢校时不能占用交卷写入通道。显示继续使用 B01 单调时钟；校时、系统时间和计划时间均不能代替服务器 START/END。
- 提前交卷按钮按真实可编辑状态启用并防止重复确认。经验证的 END 与提前提交共用 B04 固定入口，保留冻结前最后一次修改。待保存编辑与固定提交按原事务重试。
- 固定答卷沿用原请求、提交 ID、答案和授权元组。丢响应时重放或查询原回执；永久冲突、撤权真正停止自动写入，撤权同时封锁本地编辑。临时查询失败不得伪称发现另一份答卷。
- 回执核验考试、考生、参试、提交、题包版本、非空回执 ID、有效计分版本和接收时间。SQLite 只给原不可变答卷追加原回执，禁止替换回执或覆盖答案。本机回执保存失败只重试保存，不重传答卷。
- 重启先读原固定答卷，等待页转入补传和核对，不重新登记参试或解锁。END 已落盘但固定事务前中断时，只固定原可靠快照，绝不恢复编辑；服务端仍核对原授权。
- 只有匹配且本机已保留的服务器回执触发现有成功页、3 秒反馈和本次登录的再次进入拦截。旧答卷及回执不自动删除。退出时等待校时、循环和已接纳的回合全部退出再释放资源。

## 实测与复现

原 PR 的 1,348 项非 GUI 测试通过，但实际接线缺陷未被覆盖。先新增四项独立回归，在未修改生产代码的原分支上全部失败：真实 A05 路由及 HTTP 客户端回执、SQLite 原请求 ID、Blocked 终态、END 固定及上传。修正后这四项均通过。

扩展回归覆盖真实运行时重建、原进度和固定答卷重放、END 与固定事务间中断、回执身份和版本、临时查询失败、撤权封锁、单调周期、保存事件唤醒、SQLite 触发器模拟写失败与恢复、慢校时隔离、排队回合退出竞争。所有身份、题包和答卷均是独立合成输入；PDF 头部样例只证明包接口，不证明真实排版和渲染。

Windows / .NET SDK 10.0.401：锁定还原成功，Release 构建通过；非 GUI **1371** 项、WPF **2** 项通过，零失败。

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release
dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release -m:1 --filter "Category!=Postgres&Category!=Latex&Category!=Wpf" --logger "trx;LogFileName=pr66-final.trx" --blame-hang-timeout 90s --blame-hang-dump-type none
dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release -m:1 --filter "Category=Wpf" --logger "trx;LogFileName=pr66-final-wpf.trx" --blame-hang-timeout 90s --blame-hang-dump-type none
```

修正后重新核对最新头提交的 GitHub Windows 与 PostgreSQL CI，通过后才合并，不使用管理员跳过检查。具体头提交、结果和合并记录见 PR 审查评论。

## 未执行与未完成项

HTTP 回归使用进程内真实路由、JSON 边界和应用服务，最终答卷存储是合成内存实现，本地存储是真实 SQLite；不冒充真实 PostgreSQL 或 TLS 网络验收。PostgreSQL 由对应头提交 CI 独立执行。本机未执行真实 XeLaTeX、正式题面全流程、300 客户端实机考试、生产主备切换及 T01/T02。

B03 正式题目展示与交互、相关 B06 新通知类型及完整的页面联调仍按原任务推进。页面保留能力边界提示，不得据此声称可直接用于正式考试。
