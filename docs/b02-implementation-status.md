# B02 实施状态（等待与解锁）

本文只记录仓库当前可验证的实现，不替代任务卡和验收报告。上游依赖状态见 `docs/a-implementation-status.md` 与 `docs/b01-implementation-status.md`。

## 当前已完成

| 项 | 说明 |
| --- | --- |
| 考试包状态机 | `ExamPackageManager`：下载 → 已下载 → 已确认等待 → 解锁；结束后不可确认参试；未开始不解密 |
| 原子落盘 | 临时 `.part` 文件 + 校验长度 + 原子重命名；中断/取消/长度不符均不产生最终文件 |
| 合成加密包 | `SyntheticExamPackage`：AES-GCM-256 占位格式（D03 定稿后替换）；密钥由服务端持有，解锁引用换取 |
| 确认参试 | 对接 A03 真实契约（`ConfirmParticipationRequest` 上报已下载版本；服务器校验） |
| 等待与退出 | 等待页展示说明与下载进度；退出需服务器确认，失败可重试 |
| 开考解锁 | 订阅指令（`CommandDelivery` / `Start`），收到后换密钥解密进入考试；迟到进入（答题态确认）同一流程直接解锁 |
| 单元测试 | `Exam.StudentUnitTests`（新工程，net10.0-windows）：16 个用例覆盖 B02 验收判据 |

## 验收判据对应（执行指南 B02）

| 判据 | 现状 |
| --- | --- |
| 缺包或说明不能确认参试 | ✅ `ConfirmBlocked_WhenPackageMissing / WhenInstructionsMissing`（客户端状态机 + Mock 服务器双侧校验） |
| 结束后下载才完成不能新增参试 | ✅ `ConfirmBlocked_WhenExamEnded_AfterDownloadComplete / ConfirmFailsOnServer_WhenExamEndedDuringDownload` |
| 开考前无题目明文 | ✅ `NoPlaintext_BeforeStartCommand_EvenAfterConfirm`：磁盘仅密文；内存明文仅存在于解锁后；等待态下提前调用解锁被状态机拒绝 |
| 已开始时按同一流程进入 | ✅ `Unlock_Succeeds_Immediately_WhenAlreadyAnswering`（demo-late 演示） |

全量测试：74（单元）+ 2（集成）+ 16（学生端）= 92 通过；学生端启动冒烟无崩溃。

## 尚未完成 / 需外部条件

| 项 | 阻塞方 |
| --- | --- |
| 真实加密包格式与完整性校验（现为长度校验 + AES-GCM 占位格式） | D03 定稿样包 |
| 包传输契约（HTTP 直链 / 断点续传 / 哈希签名） | A×D 定稿 |
| 真实指令通道（SignalR、确认、补发、事件去重） | A04（B05 消费全部事件类型） |
| 解锁密钥交付与保护格式（现为解锁引用直换密钥的 Mock） | A04×D03 |
| 考试说明下发契约（现为 `InstructionsDocument` Mock） | A 补契约 |
| B04 本地恢复（下载/确认状态的崩溃恢复落 SQLite） | B04（下一任务） |

## Mock 演示情景（examId）

- `demo-exam`：正常流程 —— 下载 → 确认参试 → 约 6 秒后模拟开考指令 → 解密进入考试。
- `demo-late`：迟到进入 —— 确认时服务器告知答题态，立即解锁。
- `demo-package-timeout`：下载中断 —— 无包不可确认参试，可重试。
- `demo-closed`：已结束 —— 不可进入 / 不可新增参试。
