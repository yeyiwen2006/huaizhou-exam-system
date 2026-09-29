# 当前任务编号、负责人及Issue映射

2026-09-27新增G后按当前主责使用A/B/C/D/E/F/G/T前缀，GitHub的#编号、评论、PR关联和已完成记录保留。旧任务码仅用于历史追溯；正在开发的分支不因改编号强制重建。七人账号为A（@yeyiwen2006）、B（@takeanothernap-maker）、C（@whaler-1）、D（@huangjiaqi-squirrel）、E（@WorldChangerczx）、F（@Nostalgia-cy）、G（@kgwdonkey）。所有PR代码审查与合并归A。T01/T02由A统筹、全员测试，T03由A负责最终交付；七人成员为A至G。

开发期模块测试持续执行，整体单机测试及修复归T01，多机联调联试及修复归T02，测试后的最终版本固定与交付归T03。测试基础设施归E01，产品运维工具归F05。旧阶段安排不再作为当前任务前置；历史M0记录不纳入新的推进流程。

## 编号变更

| 原任务 | 当前任务 | 保留Issue | 说明 |
| --- | --- | --- | --- |
| E02 | G01 排名、统计与报表结果 | [#31](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/31) | 2026-09-27交接给G，保留历史记录 |
| E03 | G02 Excel导出 | [#32](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/32) | 2026-09-27交接给G，读取G01统一结果 |
| C06 | G03 考务端报表与导出入口 | [#22](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/22) | 2026-09-27交接给G，保留C已有页面成果；原服务管理部分仍归F04 |
| E06 / 原E04候选包 | 并入E01工具与T01/T02/T03阶段工作 | [#35](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/35) | #35关闭为范围移交；E01负责工具，E负责T阶段候选包/重建，A负责T03最终交付 |
| 当前E04（#35）的测试后交付部分 | T03 最终版本固定与交付 | [#48](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/48) | 新建拆分任务，A主责，T02通过后执行 |
| A07 | F01 服务管理与备份恢复 | [#7](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/7) | F |
| A08 | F02 主备同步与故障接管 | [#8](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/8) | F |
| B08 | F03 学生端主备重连 | [#16](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/16) | F |
| C07 | F04 考务端服务与主备管理入口 | [#23](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/23) | F；另接收#22的服务管理页面部分 |
| E07 | F05 主备部署与运行维护工具 | [#36](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/36) | F；测试设施归E01，正式演练归T02 |
| E04 | T01 全项目单机测试与缺陷修复 | [#33](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/33) | A（统筹），A至G 全员执行 |
| E05 | T02 多机联调联试与缺陷修复 | [#34](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/34) | A（统筹），A至G 全员执行 |

## 全部任务

下表列任务目录，不据此改变完成状态。A01—A06、B01/B02、D01/D02的既有关闭状态保留；C01—C04原卡因合并跟踪而关闭，剩余实现与验收唯一入口为#46。

| 当前任务 | 主责 | 当前跟踪Issue |
| --- | --- | --- |
| A01 架构、契约与开发组织 | A | [#1](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/1) |
| A02 激活与编号 | A | [#2](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/2) |
| A03 参试与设备授权 | A | [#3](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/3) |
| A04 时间和可靠指令 | A | [#4](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/4) |
| A05 进度与最终交卷 | A | [#5](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/5) |
| A06 时间与资源版本发布 | A | [#6](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/6) |
| B01 共享GUI、页框与身份 | B | [#9](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/9) |
| B02 等待与解锁 | B | [#10](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/10) |
| B03 原版题目与选项 | B | [#11](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/11) |
| B04 本地保存与恢复 | B | [#12](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/12) |
| B05 同步和交卷 | B | [#13](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/13) |
| B06 现场异常与在线更新 | B | [#14](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/14) |
| B07 学生端发布 | B | [#15](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/15) |
| C01 考试配置与说明 | C | [#46](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/46) |
| C02 名册维护 | C | [#46](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/46) |
| C03 激活与试场管理 | C | [#46](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/46) |
| C04 监测与人工裁决 | C | [#46](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/46) |
| C05 导入和考试中修改 | C | [#21](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/21) |
| D01 逐题规范与合成样例 | D | [#24](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/24) |
| D02 依赖与编译 | D | [#25](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/25) |
| D03 定位与学生包 | D | [#26](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/26) |
| D04 精确计分 | D | [#27](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/27) |
| D05 规则版本与重算 | D | [#28](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/28) |
| D06 真实材料准备、对照与CLI | D | [#29](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/29) |
| E01 CLI、自动构建与测试基础设施 | E | [#30](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/30) |
| 原E04 候选运行包准备（已并入） | E在T01/T02执行；工具归E01 | [#35](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/35)，非活动开发卡 |
| F01 服务管理与备份恢复 | F | [#7](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/7) |
| F02 主备同步与故障接管 | F | [#8](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/8) |
| F03 学生端主备重连 | F | [#16](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/16) |
| F04 考务端服务与主备管理入口 | F | [#23](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/23) |
| F05 主备部署与运行维护工具 | F | [#36](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/36) |
| G01 排名、统计与报表结果 | G | [#31](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/31) |
| G02 Excel导出 | G | [#32](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/32) |
| G03 考务端报表与导出入口 | G | [#22](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/22) |
| T01 全项目单机测试与缺陷修复 | A（统筹），A至G 全员执行 | [#33](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/33) |
| T02 多机联调联试与缺陷修复 | A（统筹），A至G 全员执行 | [#34](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/34) |
| T03 最终版本固定与交付 | A | [#48](https://github.com/yeyiwen2006/huaizhou-exam-system/issues/48) |

## 交接边界

G01/G02/G03由G负责，D提供精确计分、重算及持久化结果，G读取实际名单和成绩完成统计、报表整体发布、导出和页面。G01先确认契约，再交付G02、G03，GUI/CLI/Excel共用同一结果和开放条件。E保留E01并支持T01/T02候选包与重建，C保留考务核心及C05；已有实现和分支交接保留，详见[G模块交接说明](g-reporting-handoff.md)。

F01/F02提供业务和管理API/CLI处理器，F04把它们接入现有考务端，不另写一套规则或程序。F03复用B的本地状态、会话和交卷逻辑，只负责主备切换适配。原C06报表及导出交接G03，原服务管理部分仍以F04为准；C/G/F在同一考务端开发，公共导航由A协调。

F05接续现有主备部署、归档及监测脚本；E01搭建测试环境和合成数据/故障注入/负载/核对/报告/可复用发布工具；T02执行真实主备及场地验证。E01保留可复用构建/发布自动化与产物清单工具；E在T01/T02内负责同版本候选包汇总及修复后重建，A在T03负责最终版本固定与移交。原E04 #35作为阶段工作并入上述任务后关闭，不再单列开发卡。 非T开发任务在本卡范围的实现、必要接线、适用模块测试及A审查合并完成后关闭；不等待T01/T02、最终多机实测或整项目打包。仍缺本卡所属功能或生产入口则保留打开，并逐条列出开发缺口。最终整体测试及交付独立归T01/T02/T03；开发卡关闭不表示这些验收通过。 详见[开发卡关闭与阶段验收](development-closure-policy.md)。

D03继续由D开发，不转给F；F可从F01独立管理宿主、F02持久协调和F05脚本补齐开始。所有任务开发期间必须进行相应模块测试。
