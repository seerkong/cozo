# 方案设计：社群检测正式化

## 上下文

G2 internal 预览：CodeGraphProjections.RunCommunityDetectionAsync（Louvain 经 cluster_input 投影，:replace 写 ck_community/ck_member，label/cohesion 占位）。G3/G4 后 CALLS 边质量：1.0 占比 85.1%。ED-1：占位语义不进公开契约，本 track 正式化。

## 方案概览

1. **API 正式化**（Om.CodeKnowledge，public）
   - `ComputeCommunitiesAsync(CommunityDetectionOptions?)` → `CommunityDetectionResult`（升级现 internal 方法；options：MinConfidence、MaxCommunities、MinCommunitySize）。
   - 查询：`ListCommunitiesAsync()`、`GetCommunityMembersAsync(communityId)`、`FindSymbolCommunityAsync(symbolId)`。
   - records 转 public：CodeCommunitySummary（+ label/cohesion/symbolCount/algo）。
   - 策略接缝：算法名经 options（默认 "louvain"）；实现内部 switch，Leiden 未来加 case（D4）。
2. **质量**
   - cohesion = intra_weight / (intra_weight + inter_weight)（按 cluster_input 权重；孤立社群=1.0）。
   - label：成员限定名（sym_key 去 lang/arity）最长公共点分前缀（≥1 段）优先；无公共前缀 → 最高加权度成员名；社群间 label 冲突加 #n 后缀。
   - 确定性：Louvain 输出后按（size desc, 最小成员 symbolId asc）排序分配 community_id（community:001…）；成员输出排序。注意 Cozo Louvain 本身若有随机性需验证（测试跑两次比对；若不稳定，固定输入顺序或在 .NET 侧对 Louvain 结果做规范化映射——社群 id 由成员集合内容哈希派生）。
3. **预算**
   - MinCommunitySize 默认 3：小于阈值成员计入 noise 计数（诊断/结果字段），不落库。
   - MaxCommunities 默认 max(10, min(200, symbolCount/20))：按 size 降序截断，截断计数进结果与诊断。
4. **管线集成**（llm-wiki）
   - RepositoryIndexRequest.ComputeCommunities（默认 true，add-only）；IndexAsync 尾部（写库后）调用 ComputeCommunitiesAsync；失败 catch → community_failed 诊断不炸索引。
   - Summary add-only：Communities/NoiseSymbols 计数。
5. **dogfood**：本仓（cozo-lib-dotnet/src）社群数、size 分布、label 列表记 findings，人工评注可解释性。

## 影响范围与修改点（Impact）

- Om.CodeKnowledge：CodeGraphProjections.cs（正式化+质量+预算）、CodeKnowledgeModels.cs（records public + options/result 扩展）。
- llm-wiki：Indexing（IndexAsync 尾部集成）、Core（add-only）。
- tests 两侧。

## 决策摘要

- ED（本 track）：确定性策略——社群 id 从成员集合派生/规范化排序，保证幂等输出。
- 沿用 A2 预算模式；D4 策略接缝。

## 风险 / 权衡

- Louvain 随机性 → 测试双跑断言 + 规范化映射兜底。
- 大仓性能 → 投影在 Cozo 侧执行，.NET 只做规范化；dogfood 记耗时。

## 最小公开面

新增 public：ComputeCommunitiesAsync + 3 个查询 + options/result/summary records。其余 internal。
