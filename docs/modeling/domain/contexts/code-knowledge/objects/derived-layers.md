---
knowledge_plane: domain
doc_role: canonical
status: active
context: code-knowledge
last_verified: 2026-07-06
---

# 派生层：社群、执行流、入口点

派生层是对观测层事实的**确定性物化**：从符号图重算即可复原，不引入新事实，只引入结构视角。三个派生对象共享两条不变量：**确定性**（同一图双跑输出完全一致）与**有界性**（预算截断可审计，不静默丢失）。

## ck_community / ck_member（社群）

对加权 CALLS+IMPORTS 投影跑 Louvain 得到的边界单元。语义要点：

- `cohesion` = 社群内边权 / (社群内边权 + 跨界边权)，孤立社群为 1.0——它度量"这是不是一个自然模块"。
- `label` 取成员限定名的最长公共点分前缀，退化时用最高加权度成员名——标签是**发现的**，不是声明的模块名。
- 确定性编号 `community:001…`（size 降序、最小成员 id 升序）；小于 `MinCommunitySize`（默认 3）的簇计入噪声不落库，超过 `MaxCommunities` 的按 size 截断并计数。
- 消费方：wiki 生成的 context 发现、trace 的 cross_community 标记。社群是**观测层聚合**，与 DEPA 的 `depa_capsule`（人工声明的模块边界）不同源，不可互替。

## ck_entry_point（入口点）

"执行从哪里进来"的判定，`kind` 词表：`main` / `public_api` / `http_route` / `mcp_tool`。双层检测合并：

- 图级（Om.CodeKnowledge）：`main`（Main 方法或 top-level statements 的 `<Main>$`）与 `public_api`（exported public 方法/构造器）。
- 语法级（索引管线）：`http_route`（MapGet/MapPost 等注册点所在方法）与 `mcp_tool`（ToolRunner 的 CallAsync）——路由注册是出仓调用、图上不可见，必须由拿得到调用点的索引侧检测。
- 合并规则：重物化时图级 kind（main/public_api）替换旧行，语法级 kind 的既有行**存活**于 `:replace` 重建。

## ck_process / ck_process_step（执行流）

从每个入口点沿 CALLS 边（confidence ≥ MinConfidence）做环安全 BFS 得到的步骤链：

- step 0 是入口符号自身，步骤按 BFS 首达序编号——它回答"从这个入口进来大致会流经谁"，不是精确控制流。
- `process_type` = 入口 kind，跨 ≥2 个社群时追加 `,cross_community` 标记。
- 预算：短于 MinSteps 的链被丢弃（计数）、单流按 MaxStepsPerProcess 截断（计数）、入口超 MaxProcesses 截断。

## 关系

派生层的消费链：社群 → wiki context 发现与 skills 生成；执行流 → wiki workflow 叶与 trace；入口点 → DEPA 的 `depa_entry`（capsule_exposes 计数即 V-L4 多入口信号）。物化流程见 [workflows/indexing.md](../workflows/indexing.md)。
