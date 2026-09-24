# D03 实施状态

本文记录 D03「定位与学生包」的**第一批交付**：不依赖坐标约定的部分（允许清单组包、参考答案隔离、
完整包加密与版本产物）。依据 `requirements.md`「逐题导入、原版显示与加密」、
`agent-implementation-guide.md` 第 165–168 行（选项标记、允许清单、AES-GCM 与解锁）与 239 行（D03 任务卡）。

**选项定位与学生包的另一半尚未开始**：hyperref 隐藏选项标记、PDF 区域提取与归一化坐标依赖
**B03 的坐标约定**，B03 尚未落地，因此本任务不猜其结构（见文末）。

## 逐项实现

| 项目 | 当前实现 |
| --- | --- |
| 允许清单组包 | `StudentPackageBuilder`：只复制清单允许的路径（**默认拒绝**，绝不「先全量复制再删除」），进包的是无答案元数据、逐题 PDF、清单 `resourceRoots` 声明的资源 |
| 参考答案隔离 | `StudentPackageAllowlist`：`server-only/` 一律不进包；路径关键词（答案/解析/answer/solution）与**内容字段检查**双重判定 |
| 不能仅按扩展名筛选 | 内容检查按**字段名**判定：可解析的 JSON 走属性名（含嵌套），其余文本只认带引号的字段名——避免把样式文件里名为 `\answers`、`solution` 的宏误判成答案数据 |
| 无答案元数据 | 学生端包内的 `manifest.json` 用 D01 的严格读取校验，并再按内容规则核对一次；测试逐条断言包内任何条目都不含 `correctOptionIds` |
| 完整包加密 | 直接复用既有 `PackageEncryptionService`（HEXAMPKG v2，每版本独立随机密钥、12 字节 nonce、16 字节 tag），不另造格式；服务端与学生端已按同一格式编解码 |
| 版本产物 | 加密产物按 `packageId` + `paperVersion` 命名并落在独立目录；`EncryptedPackageEnvelope` 交回调用方，由 A06/A08 的既有链路入库与下发 |
| 半成品保护 | 加密先写 `.partial` 再原子改名，避免半成品替换当前产物；明文只在内存里，不落盘 |
| 材料目录零改动 | 产物目录与加密产物都必须在导入包之外（越界直接抛错）；包根前后文件清单一致由测试断言 |
| 包内明文布局 | 一个 ZIP，条目名即包内相对路径：`manifest.json`、`questions/<考试名称_题号>.pdf`、清单声明的资源。**该布局由 D03 定义，需与 B03 一起确认后定稿** |

## 测试范围

`tests/Unit/Exam.UnitTests` 下共 **46** 项，不需要 PostgreSQL 或 TeX，随现有流水线执行：

| 测试类 | 数量 | 覆盖 |
| --- | --- | --- |
| `StudentPackageAllowlistTests` | 36 | 服务器专用目录识别（含大小写、反斜杠、`./` 前缀）、答案/解析关键词、JSON 字段与嵌套字段、非 JSON 文本只认带引号字段、样式宏不误伤 |
| `StudentPackageBuilderTests` | 10 | 组包分离服务器专用内容；**用学生端解码器读回加密产物**并逐条断言无答案字段；答案类资源被排除但其余保留；缺 PDF / 缺资源目录 / 缺清单被挡下；未定义字段的清单被严格读取拒绝；产物目录越界与非空目录被拒绝；**材料目录零改动** |

## 本机验证

Windows 11，.NET SDK 10.0.401，`--configuration Release`，基线为已合并 `main`：

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --no-restore -c Release   # 0 警告 0 错误
dotnet test Huaizhou.ExamSystem.sln --no-build -c Release --filter "Category!=Postgres"
# 918 项通过：Exam.UnitTests 683、学生端 89、管理端 76、学生身份 25、集成 23、负载基础 22
```

加密往返不是「自己加密自己解密」了事：测试用**学生端使用的 `PackageCiphertext.DecryptAsync`
与 `PackageUnlockMaterial`** 读回产物，再解开 ZIP 逐条核对条目与内容。

## 尚未完成及归属

- **选项定位**：hyperref 隐藏外观的选项标记、PdfPig 提取区域、按 `pageTopLeftNormalizedV1`
  归一化坐标。依赖 **B03 的坐标约定**；本任务只固定了该规范名与「圆钮第 i 个 ↔ `options[i]`」的绑定约定，
  **不猜坐标结构**。PdfPig 尚未引入（新增依赖需同步 `packages.lock.json`）。
- **版本产物入库**：`PostgresVersionedResourceStore` 的写入与查询属 A06/A08 既有链路，
  本任务只产出加密产物与信封；实库往返尚未在本机验证。
- **包内明文布局需 B03 确认**：布局由 D03 定义（见上表），但消费方是 B03，未经确认前不当作已定契约。
- **解锁链路**：密钥下发与解锁由 A04 负责，本任务不涉及。
- **GUI/CLI 入口**：依赖检查、编译与组包的 GUI/CLI 由 C05 与 E 的框架提供。
- **交叉审查**：按分工由 B 核对版式、A 审查执行边界、E 独立核算；本任务尚未完成交叉审查。

以上是本机实现与自动化测试证据，**不等于**真实试卷组包、300 台设备下载、版式与选项绑定验收通过。
任何未列为完成的场景不得写成已通过。
