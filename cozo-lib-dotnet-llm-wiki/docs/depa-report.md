# DEPA 报告解读

DEPA（Data/Effect/Processor/Actor 架构判据）符合度报告由三个 MCP 工具提供：`depa_conformance`（全量报告）、`health_score`（分维健康度）、`fact_grade_map`（事实源分级图）。本页讲怎么跑、怎么读、误报怎么处置。口径真源：`cozo-lib-dotnet/src/Om.Depa/rubrics/rule-map.md`。

## 怎么跑

```bash
REPO=/path/to/repo            # 已完成 depa-wiki index
depa-wiki call depa_conformance --work-dir "$REPO"                    # 全量（先扫描后聚合）
depa-wiki call depa_conformance --work-dir "$REPO" --dimension layering  # 只看一维
depa-wiki call health_score    --work-dir "$REPO"                     # 分维健康度速览
```

- `--scan-first false` 只聚合已持久化的 violations，不重跑检测器——此时无持久化命中的规则报 BLOCKED（"PASS cannot be told apart from BLOCKED without running the detectors"），所以日常用默认 `scanFirst=true`。
- 标注输入：默认读 `<workDirectory>` 根（其次 `.codument/`）下的 `depa-map.json` / `depa-effects.json`，可用 `--map-path` / `--effects-path` 显式指定。

## 报告结构（真实样例，截断）

```json
{"dimensions":[
  {"dimension":"layering","rules":[
    {"ruleId":"V-L2","verdict":"PASS","blockedReason":"","violations":[]},
    {"ruleId":"V-L4","verdict":"GAP","violations":[{
      "violationId":"depa:violation:V-L4@depa:capsule:Om.Core@401c89a602bc",
      "dimension":"layering","subjectEntityId":"depa:capsule:Om.Core",
      "message":"capsule 'Om.Core' exposes 157 entries (a capsule has one stable entry): AddInterceptorAsync, …"}]},
    …]},
  {"dimension":"data","rules":[
    {"ruleId":"V-D2","verdict":"BLOCKED",
     "blockedReason":"静态观测不足（占位规则，见 rubrics/rule-map.md）：需运行时语义：状态可重建性（事件源→重放）无法从静态符号图证明",
     "violations":[]},
    …]}],
 "structuralFindings":[],"scanned":true}
```

## 三种 verdict

| verdict | 含义 |
|---|---|
| **PASS** | 检测器跑了，没有命中——在该规则可观测的范围内合规 |
| **GAP** | 检测器命中违例，`violations[]` 携带主体、message 与 `path:line` 证据 |
| **BLOCKED** | 检测器**没有足够输入下裁决**：缺标注（如未声明 layers/capsules/recoveryPaths）、缺观测类别（占位规则），或 scanFirst=false 时无持久化数据。`blockedReason` 点名缺的是什么 |

**BLOCKED ≠ PASS。** 这是报告最重要的口径：BLOCKED 表示"没看见"，不表示"没问题"。`health_score` 中 BLOCKED 只降低 coverage，从不计入合规；报告宁可 BLOCKED 点名缺失输入，也不假装 PASS。

## 32 规则分母与占位规则

工具分母 = rule-map.md 中状态 ∈ {implemented, implemented-weakened, placeholder-BLOCKED} 的规则，**共 32 条**（24 实现 + 8 占位），按 8 维分组：data / effect / processor / layering / fact_source / actor / overdesign / vendor。人工核查表专属的 4 条（D3/D4/G4/G5 human-only）不进工具分母。

**占位规则（placeholder-BLOCKED）**恒报 BLOCKED，reason 逐条命名缺失观测类别（需语句级 AST / 需运行时语义 / 需人工语义比对），例如 V-D2/V-P1/V-P3/V-P4/V-A*/V-G2/V-G3/V-V*。静态符号图观测不足以裁决它们——这是设计上的诚实占位，不是 bug。

## implemented-weakened（弱化实现）

部分规则是**现象级近似**，不是完整语义判定，rule-map.md 逐条注明缺口。读报告时的含义：

- 命中（GAP）时 confidence 通常 ≤0.6~0.7，evidence 标 `heuristic`——它捕捉到了现象（如 threading 外呼密度、词表匹配），但"语义罪名"（如"锁糊共享状态"）未被证明，需要人工确认；
- 未命中（PASS）只代表该弱化观测面上干净，语义层面的违例可能仍存在。

例：D2（裸锁糊共享状态）由 V-A1 以"threading 外呼密度 ≥3"近似；A3（写后回读）被归并进 V-E1 的"core 直接 IO"现象。

## confidence 与 evidence

- 每条 violation 按 confidence 排序，携带 `path:line` 证据可直接跳转核对；
- confidence 如实传递观测边的置信度：如 name-only 歧义解析的 CALLS 边是 0.5，不冒充 1.0；
- evidence 标 `heuristic` 的命中优先人工复核。

## 误报处置：走标注侧，不改业务代码

确认为误报/豁免的命中，处置路径是**编辑 `depa-map.json` 标注**（add-only 键），让下次扫描正确裁决，而不是为了让报告变绿去改业务代码：

- 声明 `layers[]`（`{name, pathGlobs[]}`，低→高）让 V-P2 分层判定有据；
- 声明 `recoveryPaths[]`（恢复路径 glob）让 V-S2a/V-S2b 豁免合法的恢复读取；
- 补 capsule / fact_source 分级标注消除"未声明恒 BLOCKED"。

`fact_grade_map` 可导出当前标注的事实源分级图（grade 1-7 + fact_written_by / projection_derived_from 邻接）核对标注是否生效；无标注仓库返回空 `{"factSources":[],"edges":[]}`。

## health_score 怎么读

```json
{"dimensions":[{"dimension":"layering","gapCount":1,"blockedCount":9,"rulesCovered":1,"rulesTotal":10,"score":0}, …],
 "note":"Display layer only: each DEPA dimension is judged independently (八组分别裁决) and never merged into a single overall verdict. …"}
```

- `score = PASS 规则数 / 该维规则总数`，display-only；
- 各维独立裁决，**没有**全仓单一总分——不要把八个 score 平均成一个数去汇报；
- `rulesTotal` 覆盖完整 rubrics 目录：不可检测行以显式 BLOCKED 占位入分母，human-only 行不入。
