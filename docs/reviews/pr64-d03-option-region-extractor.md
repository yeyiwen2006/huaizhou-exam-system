# PR #64 合并前审查记录

2026-09-27，在仓库所有者授权下审查、修正 PR #64。基线为 main `d959ebf`，作者提交为
`73a269d`、`1680d8f`。保留原提交，在同一 PR 分支追加修正，不重写共享历史。
本轮没有把自己的修正记为其他开发者复核或批准。

## 结论与交付边界

本 PR 交付 D03 的抽取侧：公共结果 DTO、原始标签矩形读取、目标页核对、页框继承、
旋转/裁剪归一化和失败判定；同时保留并完善 D02 的输出日志折行修复。
可作为部分交付在修订提交的 Windows 与 PostgreSQL CI 通过后合并。

核对 `XeLaTeXCompiler` 的隔离编译产物入口，以及 `StudentPackageBuilder`、
`StudentPaperPackage` 的现有调用边界。本次没有把坐标 JSON 加入旧学生包，
没有放宽 v1 的文件允许清单、版本匹配或加密校验。公共 DTO 的含义仍按
[选项定位契约](../d03-option-region-contract.md)；PdfPig 保持 0.1.16，锁定还原通过。

## 审查发现与修正

| 问题 | 修正及回归证据 |
| --- | --- |
| **分离标记被错误合并**：逐步扩大的外接框填平 L 形空洞，把未接触任何原始碎片的第三处标记纳入 | 在原始碎片相接图上做连通遍历，完全连通后才取并集；验证三种遍历顺序及实际 PDF 注解 |
| **继承 CropBox 被忽略**：只取 PdfPig 0.1.16 的 `Page.CropBox`，合成页树四种旋转均按错误基准归一化 | 从原始页字典沿 Parent 查找最近页框，取 CropBox 与 MediaBox 交集；覆盖继承、页级覆盖、超大裁剪框、空交集和旋转 |
| **标记读取与动作核对不一致**：未读 HexToken、间接目标与间接 Rect；清单外十六进制标记会被静默漏过；同时有 A/Dest 时，两套解析优先级交叉绑定 | 有界解引用并接受字符串、十六进制字符串及名称；拒绝冲突目标来源、Hidden/NoView、非本地 GoTo；保留精确标识绑定与清单顺序 |
| **非法区域被掩盖**：零面积碎片与合法区域合并后通过；容差接受真实越界，分别钳位 x/width 仍可能越界 | 每个原始碎片独立校验；变换前严格核对原始边界；仅在原始范围通过后处理浮点表示误差，并由端点求宽高 |
| **坏 PDF/请求抛异常中断导入**：仅保护 Open，不保护延迟页面/注解读取；空集合、空题目或非法路径可抛异常 | PDF 打开与延迟读取均返回稳定失败，不携带第三方异常原文；请求校验要求绝对路径并处理无效输入，已知选项的错误保留 OptionId 和页码 |
| **输出日志仍可能误判**：固定 512 字符窗口截断长路径；Fold 会删除续行文件名里的括号文本 | 从原始日志还原折行，不使用破坏文件名的 Fold；线性正则处理空格、引号和长路径，非法页数不抛异常或报告成功 |

参考核对了 PdfPig v0.1.16 的 `AnnotationProvider`、`DestinationProvider`、`ActionProvider`、
`Structure` 和 `Page` 源码。继承页框缺陷通过本仓库实际解析最小 PDF 复现，而不是仅根据文档推测。

## 修复前后回归

新增 `OptionRegionReviewTests`，普通 CI 无需 TeX 即可执行。所有 PDF 都由测试中的最小合成对象生成，
写入独立临时目录并清理，不使用真实题目、答案、学生信息或外部下载样本。

首批 39 项在作者原代码上运行：**30 失败、9 通过**，含继承页框、分离重复、异常路径、
目标表示和日志折行问题。修复后该批全部通过；补充跨页、冒号标识、名称冲突、普通链接、
页框覆盖与非法页数后，新增测试共 **56 项**。连同原有几何及日志测试，定向运行 **100 项全部通过**。
定向运行是全量运行的子集，不重复计入下表。

## 本地构建与完整模块检查

环境：Windows、.NET SDK 10.0.401、TeX Live 2025 / XeTeX 0.999997、PdfPig 0.1.16。

```powershell
dotnet restore Huaizhou.ExamSystem.sln --locked-mode
dotnet build Huaizhou.ExamSystem.sln --configuration Release --no-restore
dotnet test Huaizhou.ExamSystem.sln --configuration Release --no-build -m:1 --filter "Category!=Postgres&Category!=Latex&Category!=Wpf"
dotnet test Huaizhou.ExamSystem.sln --configuration Release --no-build -m:1 --filter "Category=Latex"
dotnet test Huaizhou.ExamSystem.sln --configuration Release --no-build -m:1 --filter "Category=Wpf"
```

锁定还原通过；Release 构建 **0 警告、0 错误**。

| 分组 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: |
| 普通测试（单元 1061、集成 24、身份 24、学生端 134、考务端 75、负载工具 22） | 1340 | 0 | 0 |
| 真实 TeX 类别（单元 1、集成 22） | 23 | 0 | 0 |
| 独立 WPF 类别 | 2 | 0 | 0 |
| **合计** | **1365** | **0** | **0** |

TeX 类别包含编译器到抽取器的真实合成题检查，以及既有编译、组包、加密、学生读取回归。
这不是坐标文件进入 v2 加密包的验证。没有相应类别的项目显示“无匹配测试”，不算跳过或额外通过。

实际命令还设置独立 `--logger trx`、分组结果目录及 hang timeout。
每个项目使用自动生成的唯一 TRX 文件名，完整结果保存在本地忽略目录 `artifacts/pr64-review/`，
按每组 6 份 TRX 的 Counters 汇总并与控制台结果交叉核对，没有重复计算或覆盖计数。
本地未运行 PostgreSQL 类别，由修订提交对应的独立 CI 验证；最终 SHA、CI 链接和合并结果写入 PR。

## 尚未覆盖及后续任务

Issue #26 保持打开。坐标 JSON 序列化及公共 schema、学生清单 v2、同一加密 ZIP 的读写、
B03 PDF 叠加控件及真实点击、C05/CLI 接线、版本下载恢复仍需后续交付。
本次 WPF 基础测试不等同于选项绑定、滚动、缩放、DPI 的真实点击验收。
没有读取或修改真实材料，没有执行 T01/T02，也没有宣称 D03 全部完成。
