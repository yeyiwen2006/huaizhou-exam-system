# 考试目录：多场考试、名称唯一与学生端显示

对应 requirements.md“考试设置与计分”一节。实现由 A 完成（2026-10-01）。

## 规则

- 同一服务器可管理多场考试。考试以名称区分，不另设考试类别：去除首尾空格后不区分大小写比较，不能与已有考试（含只有学生端元数据的旧考试）同名；名称须能用作 Windows 文件名，不超过 128 个字符。
- 内部考试标识由服务器按新建请求标识生成（`exam-` + 24 位十六进制），同一请求重试得到同一场考试；界面和 CLI 都按名称指定考试，不显示内部标识。
- 名称和科目题组只能在开考前修改；起止时间仍按原计划规则修改（版本化时间计划 计划接口），考务端“保存考试设置”在时间变化时一并提交。
- 保存设置时在同一事务内写入学生端可见的名称、科目，以及由结构化规则生成的考试说明（`ExamSettingsRules.BuildInstructions`，考务端预览使用同一实现）。
- 学生端显示：新建考试默认不显示，可随时打开。已确认参试（等待、答题、已交卷）的考生中只要有人尚无最终答卷，就不能取消显示；全部回收后才能取消。学生端考试列表在“不显示”时仍会列出仍有未回收考生的考试，避免已在考试中的考生看不到。

## 接口（考务身份）

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/v1/exams` | 列出考试：名称、时间、状态、是否显示、是否锁定、实际参试与已回收人数 |
| POST | `/api/v1/exams` | 新建：`requestId`、`name`、`schedule`、`settings` |
| GET | `/api/v1/exams/{examId}` | 完整设置、计划版本、当前考试说明 |
| PUT | `/api/v1/exams/{examId}/settings` | 修改名称与科目题组：`requestId`、`expectedVersion`、`name`、`settings` |
| PUT | `/api/v1/exams/{examId}/visibility` | 设置学生端显示：`requestId`、`visibleToStudents` |

错误码：`EXAM_NAME_TAKEN`（重名）、`EXAM_SETTINGS_LOCKED`（已开考）、`EXAM_VISIBILITY_LOCKED`（不能取消显示）、`VERSION_CONFLICT`、`INVALID_REQUEST`。

## CLI

```powershell
exam-cli exams list
exam-cli exams show 名称
exam-cli exams create --input exam.json        # { "name", "schedule": { "startsAt", "endsAt" }, "settings" }
exam-cli exams update 名称 --input settings.json # { "name", "settings", "expectedVersion"? }
exam-cli exams visibility 名称 on|off
```

## 数据库

迁移 `202610011200_A01_exam_catalog.sql` 新增 `exam.exam_catalog`（名称唯一键、设置 JSON、显示开关、版本、操作者）与 `exam.exam_catalog_idempotency`。只新增表；没有目录行的旧考试保持原显示行为，可在列表中看到但需通过原接口维护。回退时撤回代码即可，新表可保留。

## 未包含

- 名单页在进入考试后可用，名册由服务器持久保存，GUI 与 CLI 共用逐行及整批校验。
- 导入试题包时核对已保存科目与题组，编号和规则与导入清单保持一致。
