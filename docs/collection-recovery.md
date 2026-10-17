# 收卷 自动收齐与可恢复通知

## 数据来源和触发

生产环境只使用 PostgreSQL `exam.participations` 中不为 `not_participating` 的实际参试记录。等待退出成功后排除；答题、提交、断线及客户端崩溃不会自动移出。确认参试和退出改变实际集合时，数据库触发器在同一事务中递增 `actual_roster_versions` 并写 `collection_pending_checks`。最终答卷插入也在原提交事务中写入待检查记录；事务回滚不会留下伪检查或通知。

`CollectionCompletionWorker` 在活动节点每 250 毫秒执行有界批次，启动后直接恢复数据库中的待检查记录。每场检查使用短事务独占该场队列记录，以同一 SQL 快照核对名单版本、实际参试者及其最终答卷。零人不产生通知。满足条件后，完成事实和可重放通知是 `collection_completion_events` 中的同一条记录；该记录与待检查删除同事务提交。主键 `(exam_id, roster_version)` 保证每个版本最多一个业务事件。并发变更等待队列记录后重新标记，不会在后台删除队列时丢失唤醒。

收齐通知不代表成绩可以开放。提前收齐仍需等待考试结束；新增实际参试者产生新名单版本，原通知只证明原名单版本曾收齐，不能作为新版本的开放依据。统计模块必须重新核对当前名单和考试结束条件。

## 考务 API

管理员使用现有 Bearer 认证调用：

`GET /api/v1/submissions/collection/events?examId=<考试>&afterRosterVersion=0`

返回标准 API 信封，`result` 是 `CollectionCompletedNotification[]`。按名单版本升序，每页最多 100 条。事件包含 `eventId`、`examId`、`rosterVersion`、`participantCount`、`completedAt`。匿名请求和只有学生设备凭据的请求均拒绝。

消费端应每场保存已处理的名单版本，以 `eventId` 去重；成功处理后推进游标，再补取下一页。请求失败或消费前崩溃时沿用原游标，服务器返回相同事件标识。建议在线每 500 毫秒补取；该建议不能代替 GUI/CLI 实测。服务器不以“已发送”删除事件，各考务端互不抢占消息。

补取在返回事件前写 `delivery_fence_at` 并等待事务提交。此真实 WAL 写入形成当前业务连接持久策略下的提交屏障，防止将“可查询但本机持久提交尚未确认”的事件直接宣布成功。生产业务连接采用本机持久提交 synchronous_commit=local，并在返回前完成本机确认记录；备机缺席不阻断通知补取。双节点健康时继续核对备机独立确认，后台追平复制。本机单库测试不证明真实双机接管完成。

旧的 `POST /api/v1/submissions/collection/evaluate` 保留兼容请求格式，但生产完全忽略调用方 `rosterVersion` 和 `actualParticipationIds`，只查询权威数据；后台自动触发不依赖此接口。

## 升级和验证边界

新增迁移 `202609221100_A05_collection_recovery.sql`。已有手动事件保留，但不作为新的权威通知补取；升级后的版本高于已有事件最高版本。现有参试数据自动进入待检查队列。无需清空数据库或覆盖旧迁移。

真实 PostgreSQL 回归覆盖：调用方伪造精简名单、最后两份并发提交、进程内存丢失后恢复、同版本去重及原事件补取、等待退出、零人、新参试者形成新版本、通知记录失败回滚后重试、后台 Worker 无手动 evaluate 恢复、队列清理与最终提交并发不丢唤醒，以及真实 HTTP 认证、游标和提交屏障写入。

这些测试验证服务端持久通知出口。监测/回收提示 的弹窗、CLI 事件展示、真实现场网络下 2 秒时限，以及双份持久确认、故障接管仍需对应联调验收。
