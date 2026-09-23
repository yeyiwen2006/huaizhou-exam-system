# PR #44：D01 逐题包校验器审查与修正

审查日期：2026-09-23。原 PR 提交：`e39e0d7246e9653a0bf1e9b57be355620a5bbb2f`。
原始基线：`59eb1093ff77cc396922155a5a9cc30a33bc2282`。
依据用户“仔细审核、修改后合并”的请求，在独立工作副本中审查 PR 原始 18 个文件，并对照
`requirements.md`、D01 规范、四份 schema、合成夹具与 CI。未在用户原工作区切换分支或覆盖文件。

## 审查结论与修复

| 级别 | 问题与影响 | 修复位置与行为 |
| --- | --- | --- |
| P1 | 必填集合缺失、null 元素、null 标识可引发 NullReferenceException / ArgumentNullException，而非返回校验报告 | `ManifestShapeScanner` 在反序列化前检查对象、必填字段、null、类型、数组和数值边界；`Validate` DTO 入口复用同一检查 |
| P1 | 未核对答案 examId；重复答案静默取首条；重复正确选项能冒充多选；非法答案先参与满分计算 | `ValidateAnswerKey` 先校验考试、题目、题号、选项与题型，拒绝重复题目/选项，只把无歧义合法答案交给满分计算 |
| P1 | 已知参考答案代入满分表达式除零时仅标记待核对；逐题 1/3 可由三题合计 1 掩盖 | `TryQuestionFullScore` 明确报告除零/非有限小数并定位具体分支和题号；保持精确分数，中间分数最终约分为有限小数仍允许 |
| P2 | 未验证 resourceRoots；只校验题目文件的最后一段，遗漏非法目录段和 Windows 数据流/设备名 | 检查所有路径段，拒绝绝对路径和父目录；资源根按规范化后的 Windows 路径判重；实际符号链接/联接点仍由 D02 检查 |
| P2 | JSON 重复属性可覆盖前值；Web 默认选项允许数字字符串；schema 长度、数量、页数等约束未执行 | 拒绝重复属性与隐式数字转换，使用 schema 驱动的变异测试覆盖边界；按 Unicode 字符计数；精确接受 1.0/1e0 等整数值而不经过浮点舍入 |
| P2 | 原部分 JSON Pointer 指向不存在的根层级；一元负号文法与实现不一致；Windows 名称/扩展名契约有差异 | 根指针改为空字符串；答案指针相对答案根；保留 ~0/~1 转义；重复一元负号需括号；修正点开头文件名、上标设备名与大小写扩展名 |
| P2 | 补充坏输入测试时，Unicode 字符串/属性名解码仍有异常出口；DTO 编码可能替换非法代理项，改变标识 | Unicode 解码异常转为 InvalidJson；DTO 非法 UTF-16 明确拒绝，不静默替换为 U+FFFD |

没有新增传输层错误码，仍使用既有 `IMPORT_INVALID`。新增诊断规则只放入现有校验报告。
原 4 套合法样包和 26 个错误样包保持通过/预期规则定位兼容，未通过修改错误夹具掩盖缺陷。

## 验证过程

1. 原分支锁定还原、Release 构建、非 PostgreSQL 测试：570/570，通过，0 警告 0 错误。
2. 首轮新增 255 项回归，在原实现上 121 项失败、134 项通过，复现异常、非法输入放行和满分缺陷。
3. 修正后继续补齐 multipleCorrect 分支、精确整数、Unicode、扩展名等边界。新增总计 302 项：
   `ImportReviewRegressionTests` 300 项，`NamingRulesTests` 2 项。Unicode 的 3 项原始探测先失败后修复。
4. 最终重新执行全套锁定还原、Release 构建和测试：872/872 非 PostgreSQL 测试通过，0 警告 0 错误。
   单独筛选导入校验测试：534/534 通过。`git diff --check` 通过。依赖与包锁文件未变。

本机环境：Windows 11、.NET SDK 10.0.401。使用命令：

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore --configuration Release
dotnet test Huaizhou.ExamSystem.sln --no-build --configuration Release --filter "Category!=Postgres"
dotnet test tests/Unit/Exam.UnitTests/Exam.UnitTests.csproj --no-build -c Release --filter "FullyQualifiedName~Exam.UnitTests.Importing"
git diff --check
```

CI 和最终合并记录留在 PR #44。必须检查修复提交本身的 `build-test` 与 `postgres-integration`，
不能引用原 PR 提交的成功状态代替；本文不预先声称尚未发生的远端结果。

## 保持的边界

导入期允许不提供答案或提供合法的部分答案，此时满分未完成核对。提供空字符串不是“未提供答案”。
参考答案文件自身的重复项与 D05 的答卷/规则版本去重不是同一问题，前者已在 D01 阻止。

D01 校验成功不代表考试可发布：D02 的真实文件/依赖/编译及重解析点边界、D03 的版式和控件坐标、
D04 的所有合法作答情形枚举与答案完整性、D05 的版本和重算，以及 C05/GUI/CLI 接入仍须各自验收。
本次未在本机跑 PostgreSQL 服务、XeLaTeX 或真实试卷测试，也不冒充 B/E 的人工交叉签核。

## 规范参考

- RFC 6901 第 3、5 节：JSON Pointer 转义及空字符串根指针。
  https://www.rfc-editor.org/rfc/rfc6901
- Microsoft：Windows 文件与目录命名（设备名、目录段和大小写约定）。
  https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file
- JSON Schema：integer 按精确数值而非小数点的存在判定，带引号数字不是整数。
  https://json-schema.org/understanding-json-schema/reference/numeric
