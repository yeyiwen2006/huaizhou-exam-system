# M0 架构与公共边界

## 依赖方向

`Exam.Contracts` 和 `Exam.Domain` 位于内层。`Exam.Application` 编排领域规则并声明持久化、通知、时钟和操作系统能力接口；`Exam.Infrastructure` 实现这些接口；API、CLI 和 GUI 只调用应用服务，不复制业务校验。

```text
Student / Admin / CLI / Server
              |
        Exam.Application
          /          \
Exam.Domain       Exam.Contracts
              |
       Exam.Infrastructure
```

基础设施在运行时注入应用层，图中的最后一条只表示基础设施实现应用层声明的接口，源码引用仍保持由外向内。WPF 两端共同引用 `Exam.Desktop.Shared`，共享主题不得复制到业务项目。

## 写入与版本

每个可重试写请求携带稳定 `requestId`。并发修改携带 `expectedVersion`；参试重入、设备裁决、答题进度、时间、试卷、计分和服务接管分别使用自己的单调版本。重复请求返回原结果，过期消息不能回退当前状态。

服务器响应封套包含稳定 `serverId`、当前 `nodeId`、`serverEpoch` 和 `correlationId`。客户端拒绝错误逻辑服务器或过期服务代次。SignalR 只负责传送，命令是否完成由服务器持久事件、客户端确认和状态查询共同判断。

面向学生端与考务端的 HTTP、SignalR 及本机服务管理响应，所有 `DateTimeOffset` 时间均以带
`+08:00` 的 ISO 8601 格式返回，包括服务器当前时间、考试起止时间及操作时间。
传输边界使用 `JsonDefaults.CreateServerTransport()`，请求中的带偏移时间按同一时刻归一为 UTC；
内部时钟、数据库及文件序列化继续使用原有 UTC/`JsonDefaults.Create()` 约定，不平移时间点，
不改变考试时长、事件排序或持久化内容。客户端不能再把收到的时间数值额外加八小时。

## 持久化边界

生产服务已通过 `PostgresRuntimeRegistration` 接入 PostgreSQL 18：激活、参试、指令、最终交卷及版本发布使用持久化实现，缺少数据库配置时不会退回内存。仓库中的内存实现用于规则参考、状态重放或测试，不能把内存锁当作跨进程一致性机制。成功响应必须发生在事务及当前保护模式要求的持久确认之后；提交结果未明时使用原标识查询或重试。

主备存储和平台适配已建立，但 `FailoverCoordinator` 的持久化管理链路、独立 Windows 服务管理宿主及真实双机闭环仍未完成；不能因存在同步复制模板而宣称双机验收通过。逐项完成状态和具体缺口见 [A 组实施状态](a-implementation-status.md)。

学生端的本地答案、标记和待提交记录要求使用 SQLite WAL 与 FULL 同步保存，具体实现仍由 B04 跟踪；B01/B02 的设备凭据和控制恢复信息已使用 Windows 当前用户保护，不能把它们算作答案持久化已完成。真实题包和密钥不进入源码仓库。

## 运行优先级

最终交卷、授权裁决和可靠事件优先于进度合并、编译、导出和监测查询。连接池、队列、文件并发和后台任务必须有界。编译、Excel 和备份任务不能占用 GUI 线程，也不能阻塞交卷持久化。
