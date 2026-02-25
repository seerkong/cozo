# 变更：Om 层 DEPA 符合度整改（REC-1..5）

## 背景和动机 (Context And Why)

承接已归档 mission `add-llm-wiki-depa-fractal-wiki`（P2）与 `harden-llm-wiki-engineering`（P3）的交接：depa_conformance 工具在本仓发现 5 条 V-F2；2026-07-06 用 depa-expert skill 做模块级人工盘点（分析工作区随本 track 归档：analysis/depa-scan-workspace/），裁定：**V-F2 五条全为 heuristic 误报**（OmMutationContext 是作用域 facade 非 runtime 载体，五方法均为零业务单表达式委托——正是 DOP 形态；根因是本仓未提供 depa-map.json，heuristic 按名猜 carrier），但人工核查表发现 **4 条真 GAP**（REC-1/2/3/5）+ 1 条标注整改（REC-4）。

## "要做"和"不做" (Goals / Non-Goals)

**目标（按依赖顺序 REC-1→3→2→4→5）:**
- **REC-1（P0，Data/唯一写入者）**：Om.Core 新增公开 `DeleteEntityAsync`（单事务物理删实体+全部属性+出入边；与时态语义的关系在 API 文档注明——violation 类可重建投影场景用物理删）；Om.Depa 过期清理段（DepaViolationDetectors.cs:726-732 的原生 `:rm om_entity`）改调新 API；孤儿 om_property 残留回归测试（修复 P2 时代已知妥协）。
- **REC-3（P2，Runtime 显式化）**：CozoOmCodeKnowledgeExtensions.cs:169 的 `DateTimeOffset.UtcNow` 改走 `Runtime.Options.TimeProvider`（全库唯一直取点，indexed_at 变可测）。
- **REC-2（P1，Effect/分层）**：depa-map.json/depa-effects.json 的文件读取从 DepaScanPipeline 核心内上提到 CozoOmDepaExtensions.DepaScanAsync 入口；pipeline internal 签名改吃已解析配置对象（core 不自发现依赖）；路径版公开 API 留薄壳兼容。
- **REC-4（P1，标注）**：仓根新增 depa-map.json（本仓 DEPA 标注真源起步版）：runtimeCarrierTypes 声明 CozoOmRuntime/CozoOmOptions（config 通道 1.0 压制 heuristic 误报），并声明 Om.Core capsule；复扫断言 OmMutationContext 不再误报。
- **REC-5（P2，分层）**：Om.Depa 消费 Om.CodeKnowledge internal（EffectApiBuiltins）——同 assembly 合法但跨 capsule 边界；裁定为 **known-exception 决策记录**（观测层词表的唯一合法消费者，public 化会扩大 NuGet API 面违反最小公开面纪律），零代码改动。
- 验证：depa_conformance 复扫（误报清零、无新 GAP）+ 两侧测试全绿。

**非目标:**
- backlog 5 条（B-1 interceptor 漂移、B-2 前缀撞名、B-3 工具纯委托豁免、B-4 scan 批量写、B-5 Support/ 混装）——登记至 codument/backlog，不在本 track 展开。
- 不做时态 retract 删除语义（物理删够用，retract 留需求出现时）。

## 变更内容（What Changes）

- Om.Core：EntityLogic.DeleteEntityAsync + CozoOm facade（新增公开 API，非破坏）。
- Om.Depa：清理段改调 + DepaScanPipeline 配置注入化。
- Om.CodeKnowledge：一行 TimeProvider。
- 仓根 depa-map.json；tests 两侧。

## 影响范围（Impact）

- 受影响能力：cozo-dotnet-om（delete-entity）、cozo-dotnet-codeknowledge（scan 配置显式化 + 标注压制）。
- 受影响代码：cozo-lib-dotnet/src/{Om.Core,Om.CodeKnowledge,Om.Depa}、仓根 depa-map.json、两侧 tests。
