# 变更：社群检测正式化（Cozo Louvain）

## 背景和动机 (Context And Why)

mission `deepen-llm-wiki-code-graph` G5 第一弹。G2 track 已验证 Cozo 内置 CommunityDetectionLouvain 固定规则可消费 cluster_input 投影，并留下 internal 预览 API（`RunCommunityDetectionAsync`，占位 label=最高加权度符号名、cohesion=0.0；ED-1 决定由 G5 正式化）。现在调用图质量已就位（G3/G4：CALLS 1.0 边占比 85%），社群划分具备真实输入。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 正式化公开 API（升级 internal 预览）：社群计算 + 查询（列社群、查符号所属社群、社群成员）。
- 质量补齐：cohesion 真实计算（社群内边权 / 社群关联总边权）；label 启发式改进（公共命名空间/目录前缀优先，退化用最高加权度符号名）；确定性输出（固定排序）。
- 有界产出：maxCommunities 预算（默认 max(10, min(200, symbolCount/20))，超出合并入 "misc"或截断记诊断——采用截断+诊断）；最小社群 size 阈值（默认 3，小于阈值归 noise 不落 ck_community，成员不落 ck_member）。
- 索引管线集成：RepositoryIndexRequest.ComputeCommunities（默认 true），索引完成后重算（:replace 全量重建派生层）。
- dogfood：本仓社群数量、规模分布、label 可解释性记录 findings。

**非目标:**
- 不做 Leiden（策略契约留接缝，D4）。
- 不新增 agent 面 MCP 工具（P1 mission 范围；overview_graph 消费社群留到工具深化时做）。
- 不做跨仓社群。

## 变更内容（What Changes）

- Om.CodeKnowledge：CodeGraphProjections 的社群 API 正式化为 public（含质量与预算逻辑）；CodeCommunitySummary 等 records 转 public。
- llm-wiki Indexing：索引后管线集成 + Core add-only 开关。
- tests：质量/预算/确定性用例 + dogfood。

## 影响范围（Impact）

- 受影响能力：cozo-dotnet-codeknowledge（新增 community-detection 需求）。
- 受影响代码：cozo-lib-dotnet/src/Om.CodeKnowledge/、llm-wiki packages/{Indexing,Core}、两侧 tests。
