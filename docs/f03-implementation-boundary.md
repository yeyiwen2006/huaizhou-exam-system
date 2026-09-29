# F03 学生端主备重连：接线边界

日期：2026-09-29。关联 [F03 #16](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/16)。F 负责主备切换适配；B 已有本地答案、固定提交、永久回执和授权恢复逻辑继续复用。此文档只使用合成地址和身份。

## 当前调用链

`DeviceStore` 以地址和端口索引受当前 Windows 用户保护的设备身份；`DeviceRecord` 只保存一个地址。`HttpStudentApi.ConfigureConnection` 设置 HTTPS 业务端点，响应验证逻辑 `serverId` 与服务代次。`SignalRExamCommandFeed` 从 `DeviceRecord.Host/Port` 建立 WSS，连接中断时只重连同一地址。`AppServices.ConfigureDevice` 同时通知学生会话，但没有可信主备地址集或切主协调器。

本轮先修正同一设备切换地址时的身份回退：保留已验证的 `serverId` 与最高服务代次；不同逻辑服务器或缺少绑定身份的新地址被拒；连接代次变更后旧地址的在途响应仍被丢弃；已绑定身份后的业务错误响应也校验服务器身份和代次。这是可信身份边界修正，不表示已实现跨机自动重连。

## 尚需接线

1. 激活批准后持久保存经批准的主备地址和信任材料；地址必须来自已认证的权威接口，不能把任意输入的备用地址当成可信。现有激活结果没有完整主备地址字段，新增公共契约或改变语义须由 A 确认。
2. 以同一个逻辑 `serverId`、设备编号、凭据及授权版本探测可信候选节点；必须区分真实活动节点、旧代次和备库只读/恢复态。F02 尚未提供生产可用的活动节点查询及接管结果接口，最终接线依赖它。
3. 在确认新主后将 HTTPS、WSS、服务器时间、资源下载和待发请求切到同一已验证节点。重新建立会话和事件订阅，沿用 B05 的原进度 `requestId`、固定答卷 `submissionId`、原回执及本地终态；不能重新激活或生成新答卷。
4. 覆盖假节点、旧代次、两个节点同时声称活动、切换中旧响应返回、证书不符、旧主恢复、固定交卷结果未明等失败路径。未能确认活动节点时保持本地记录并显示重试，不把连接成功当成业务恢复。

## 本轮验证

macOS Arm64 / .NET 10.0.401：`dotnet restore tests/Unit/Exam.StudentIdentityTests/Exam.StudentIdentityTests.csproj --locked-mode -p:EnableWindowsTargeting=true` 成功；相同项目的 Release `dotnet build --no-restore -p:EnableWindowsTargeting=true` 成功，0 警告、0 错误。定向 `HttpStudentApiTests` 的 `dotnet test --no-build` 已尝试，但 macOS 没有 `Microsoft.WindowsDesktop.App 10.0`，testhost 无法启动，未执行任何测试。需在 Windows 环境实际运行并核对完整学生端回归；构建通过不等于测试通过。

尚未修改 F02、B 的本地答案/交卷状态机或公共契约；真实双机恢复和 10 秒指标由 T02 记录。
