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

## 持久化边界

当前仓库中的线程安全内存服务是规则参考实现和单元测试夹具。进入 M1 时需要把接口接到 PostgreSQL 18 事务、唯一约束和同步提交，不能把内存锁当作跨进程一致性机制。成功响应必须发生在事务及要求的同步副本确认之后；提交结果未明时使用原标识查询或重试。

学生端的本地答案、标记和待提交记录由 SQLite WAL 与 FULL 同步保存，设备凭据和恢复信息使用 Windows 当前用户保护。真实题包和密钥不进入源码仓库。

## 运行优先级

最终交卷、授权裁决和可靠事件优先于进度合并、编译、导出和监测查询。连接池、队列、文件并发和后台任务必须有界。编译、Excel 和备份任务不能占用 GUI 线程，也不能阻塞交卷持久化。
