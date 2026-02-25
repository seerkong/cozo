# CodeKnowledge v2 图 Schema 设计定稿（G1-T3）

输入：现状 schema（cozo-lib-dotnet/src/Om.CodeKnowledge/CodeKnowledgeSchema.cs，9 relations）、GitNexus 节点/边全集（~24/~18，2026-07-04 gap 分析）、两个 spike 结论（spike-treesitter-pinvoke.md / spike-roslyn-callgraph.md）、D1–D5 决策。

## 1. 设计原则

1. **单边表 + kind 枚举**（不按边类型分表）：Cozo 固定规则（louvain/SCC/最短路）吃 [from, to] 投影，用 Datalog 规则按 kind 过滤即可；分表会让跨边遍历查询碎片化。
2. **调用点粒度保留**：同一 (from,to) 的多个调用点各存一行（key 含 file_id+line），聚合走查询。
3. **双后端归并**：resolver 字段标注来源（treesitter|roslyn|regex），同一调用点两后端都产出时 Roslyn 覆盖（confidence 更高者胜）。
4. **重建式迁移**：索引数据可重算；v2 直接 drop+recreate，不做数据搬迁。ck_meta 记 schema_version，工具启动时校验。

## 2. v2 Relations

```
:create ck_meta {key => value}                       # schema_version=2, indexed_at, resolver_versions
:create ck_repo {repo_id => root_path, name, commit}                       # 不变
:create ck_file {file_id => repo_id, path, language, hash, updated_at}     # 不变
:create ck_symbol {symbol_id =>
    file_id, name, kind, start_line, end_line, signature,
    parent_id,        # 嵌套归属（类→方法），CONTAINS 冗余加速
    lang, visibility, exported,
    sym_key,          # 归一键 = lang + 全限定名 + arity（spike-roslyn 建议）
    doc_id,           # Roslyn DocumentationCommentId（M:Ns.Type.M(...)），ts 后端可空
    resolver}
:create ck_edge {from_id, to_id, kind, file_id, line =>
    confidence,       # 1.0 语义命中 / 0.6 候选 / ts 启发式 0.9/0.7/0.5 / regex 0.3
    resolver, evidence}
:create ck_entry_point {symbol_id, kind => metadata}  # kind: public_api|main|cli_command|http_route|mcp_tool (D5)
:create ck_community {community_id => label, cohesion, symbol_count, algo}
:create ck_member {symbol_id => community_id}         # 一符号一社群（Louvain 硬划分）
:create ck_process {process_id => name, entry_symbol_id, entry_kind, process_type, step_count}
:create ck_process_step {process_id, step => symbol_id, via_kind}
# 保持不变：ck_doc_block, ck_concept, ck_diagnostic, ck_owner, ck_wiki_page
# embedding relation 归 VectorSearch capsule 所有，不动
```

## 3. 节点 kind 枚举（GitNexus 全集裁剪，首批 C#/TS）

`file, folder, class, interface, struct, enum, record, delegate, method, constructor, property, field, function, variable, type_alias, namespace, module`
（GitNexus 的 trait/impl/union/macro 等留给后续语言；kind 是开放字符串，枚举写进常量类不写 schema 约束。）

## 4. 边 kind 枚举

| kind | 语义 | 产出方 |
|---|---|---|
| CONTAINS | 文件/父符号 → 子符号 | ingestion（结构） |
| IMPORTS | 文件 → 文件/符号 | ingestion |
| EXTENDS / IMPLEMENTS | 类型继承/接口实现 | ingestion |
| HAS_METHOD / HAS_PROPERTY | 类型 → 成员 | ingestion |
| CALLS | 调用点（含 new → 构造） | 调用消解（G4） |
| ACCESSES | 字段/属性读写（evidence 记 read/write） | 调用消解（G4，预算开关） |
| METHOD_OVERRIDES / METHOD_IMPLEMENTS | 方法重写/接口方法实现 | 调用消解（G4） |
| DOC_LINKS | doc_block → 符号（吸收 v1 doc 关联，沿用 local/global 预算） | ingestion |
| MENTIONS | concept → 符号/文件（沿用 v1） | 既有 |

MEMBER_OF、STEP_IN_PROCESS 不进 ck_edge，用专表（ck_member/ck_process_step）——它们是派生层产物，重算周期与源边不同，分表便于整体重建。

## 5. 固定规则适配（A6）

Datalog 投影规则（进 Om.CodeKnowledge 查询层常量）：
```
call_graph[from, to] := *ck_edge{from_id: from, to_id: to, kind: "CALLS", confidence}, confidence >= $min_conf
cluster_input[from, to, w] := # CALLS+IMPORTS 加权投影，喂 Louvain
```
- Louvain：`?[sym, community] <~ CommunityDetectionLouvain(cluster_input[])`
- SCC（P1 check 循环）：`<~ StronglyConnectedComponents(import_graph[])`
- 最短路（P1 trace）：`<~ ShortestPathDijkstra(call_graph[], ...)`

## 6. 双后端归并规则（spike-roslyn §符号 ID 建议）

1. tree-sitter 基线先落全部符号+边（resolver=treesitter，启发式 confidence）。
2. Roslyn 增强遍历 C#：按 sym_key + 位置重叠匹配既有符号；命中则升级该符号 doc_id/signature 并覆盖其出边中同调用点的 CALLS（resolver=roslyn，confidence 1.0/0.6）；未命中的 Roslyn 符号补插。
3. regex 提取器仅在无 grammar 语言上保留（resolver=regex，confidence 0.3）。

## 7. 迁移与门禁

- InitCodeKnowledgeAsync 检查 ck_meta.schema_version：无 → 建 v2；=1（旧表存在无 ck_meta）→ 提示需 reindex（工具报错带指引），提供 `--reindex` 走 drop+recreate。
- 12 个既有 MCP 工具适配点：symbol_context/impact_of_change/explain_relation 读 ck_edge（替代 ck_relation）；overview_graph 优先 ck_community；其余不动。
- 硬门禁：cozo-lib-dotnet tests + llm-wiki tests + smoke 全量通过；对本仓抽样 30 条 CALLS 边人工校验方向与目标正确率 ≥90%（confidence≥0.8 子集）。

## 8. 分批归属

- G2 track（redesign-codeknowledge-schema-v2）：§2 relations + §7 迁移 + 工具适配（此时 ck_edge 仅由现有 regex 索引器写入，保证工具链先活）。
- G3 track：tree-sitter ingestion 写 ck_symbol/结构边。
- G4 tracks：CALLS/ACCESSES/OVERRIDES + Roslyn 归并。
- G5 tracks：ck_community/ck_member、ck_entry_point/ck_process*。
