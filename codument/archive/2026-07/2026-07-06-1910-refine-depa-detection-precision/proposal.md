# 变更：检测/消解精度改进（CallResolver ambiguous + V-F2 纯委托豁免）

## 背景和动机 (Context And Why)

backlog 两条同主题证据充分的精度项（来源：expand-depa-detection-rules 复扫裁定与 depa-expert 盘点 B-3）：

1. **CallResolver 跨文件 ambiguous 缺陷**（V-P2 ×9 误报根因，证据齐全）：tree-sitter name-only 兜底把 BCL `JsonElement.GetString()`（0 参实例调用）绑定到他包 **private static 2 参**方法（evidence=ambiguous:2、conf=0.5）。跨文件候选不应含他文件 private 成员，且应校验实参 arity（调用点 ParsedCallSite 有 arity，候选符号 sym_key 含 arity——数据都在，兜底 tier 没用）。
2. **B-3 V-F2 纯委托豁免**（检测器理论完备化）：方法体仅单条 CALLS 且首参为 runtime 载体类型的纯委托 facade（OmMutationContext 形态）应降级 INFO/不报 GAP——目前靠 runtimeCarrierTypes 封闭世界压制误报，豁免规则让无标注仓库也不误伤该形态。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- CallResolver 兜底 tier（仓内唯一/歧义档）改进：候选集剔除"他文件 private/protected 成员"（同文件调用不受限）；name 命中后校验 arity（不匹配则剔除；无 arity 信息的符号保留但降序）。预期效果：本仓复扫 ambiguous CALLS 边减少、V-P2 残余误报清零或显著下降（数字为准，不硬压）。
- V-F2 纯委托豁免：检测时对候选方法做"纯委托形态"判定（方法符号出边仅 1 条 CALLS 且无 ACCESSES-write；受 ck_* 粒度限制的近似——记 rule-map 弱化说明）→ 命中形态降 confidence≤0.3 或以 INFO 类 evidence 标注不产 GAP（取实现简单诚实者，记决策）。
- 复扫对比：改进前后本仓 CALLS 置信分布、V-P2/V-F2 命中变化记 findings；一致性不回归（增量一致性门禁全绿）。

**非目标:**
- 不动 Roslyn 路径（其消解本就语义级）；不做 V-P2 主体锚定弱化问题（rule-map 已备注，另行）。
- 不追求 unresolved 率下降（剔除候选可能增加 unresolved——如实计数，正确性优先）。

## 变更内容（What Changes）

- Indexing/CallResolver.cs（兜底 tier 过滤）；Om.Depa/DepaViolationDetectors.cs（V-F2 豁免）；rule-map.md V-F2 行更新；tests 两侧。

## 影响范围（Impact）

- 受影响能力：dotnet-llm-wiki-semantic-parsing（call-resolution 精度）、cozo-dotnet-codeknowledge（V-F2 豁免）。
- 受影响代码：packages/Cozo.DotNet.LlmWiki.Indexing/、cozo-lib-dotnet/src/Om.Depa/、两侧 tests。
