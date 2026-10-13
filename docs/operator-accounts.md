# 考务账号

考务端和CLI使用考务账号登录。考务GUI只输入一次用户名和口令，服务器管理使用当前业务令牌安全换取管理会话，退出或切换账号时清除旧管理权限。CLI保留独立的管理登录。原共享令牌配置 `ExamSecurity:OperatorToken` 和 `ExamSecurity:OperatorId` 已删除，配置后不再生效。

## 规则

- 服务器上第一个注册的账号直接启用并成为管理员；之后注册的账号为“等待批准”，须由管理员批准后才能登录。
- 管理员可停用或重新启用账号。停用立即撤销该账号的全部登录会话；不能停用自己，也不能停用最后一个启用的管理员。已批准的账号不能改回等待批准。
- 用户名 2～32 个字符，只含字母、数字和 `_ . -`，不区分大小写（注册时的写法用于显示和操作记录）；口令 8～128 个字符；姓名 1～64 个字符。
- 口令只以 PBKDF2-SHA256（随机盐、21 万次迭代）哈希保存；会话令牌只保存 SHA-256 摘要。登录失败不区分“用户名不存在”和“口令错误”。
- 登录会话不自动过期，退出登录或账号被停用时失效。
- 请求体中的 `actorId`、`operatorId`、`operator` 必须与登录账号的用户名一致，操作记录写入登录账号。

## 存储

数据库迁移 `202610011300_A01_operator_accounts.sql` 新增 `exam.operator_accounts` 与 `exam.operator_sessions`。

管理CLI的账号登录以数据库为准，在Windows上将已启用账号的口令哈希与管理员标记写入加密缓存 `Management:OperatorCachePath`，默认路径为 `Management:OperationDirectory\operators.cache`。数据库暂不可达时可使用已核验缓存进行CLI运维认证，缓存只在数据库可用时刷新。Linux不保存离线缓存，认证需要数据库可达。GUI的业务令牌交换直接核对业务会话，不能使用缓存代替这项认证。管理会话保存在管理进程内存中，退出、账号切换或进程重启后不能沿用旧权限。

## 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/api/v1/operators/register` | 注册（公开） |
| POST | `/api/v1/operators/login` | 登录，返回会话令牌（公开） |
| POST | `/api/v1/operators/logout` | 退出登录 |
| GET | `/api/v1/operators/me` | 当前账号 |
| GET | `/api/v1/operators` | 全部账号（管理员） |
| PUT | `/api/v1/operators/status` | 批准、停用或重新启用（管理员） |
| POST | `/management/v1/login`、`/management/v1/logout` | 管理CLI账号登录及管理会话退出；GUI使用业务会话交换 |

其他考务接口在 `Authorization: Bearer <会话令牌>` 中携带登录会话。

## 考务端与 CLI

- 考务端顶部输入服务器地址、用户名和口令登录，或点“注册账号”填写姓名并再次输入口令后注册；登录后显示当前账号和“退出登录”。管理员可在“考务账号”页批准、停用或重新启用账号。
- CLI：`register`、`login`、`logout`、`whoami`、`operators list|approve|disable|enable 用户名`；`login` 的会话按当前 Windows 用户加密保存在 `%LOCALAPPDATA%\HuaizhouExam\Cli`（可用 `EXAM_CLI_SESSION_DIRECTORY` 改变），后续命令自动使用，`logout` 删除。服务管理另用 `management login|logout`。`EXAM_OPERATOR_TOKEN` 若设置则优先作为会话令牌使用。

## 升级

升级后数据库中尚无账号：由负责人先在考务端或 CLI 注册第一个账号（成为管理员），其余人员注册后由管理员批准。部署配置中的 `ExamSecurity__*` 项可删除。
