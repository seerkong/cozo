# Decisions

### 1. 【P0】Alias 定义时的校验深度
- 背景：Bun 已能解析 alias chain 并在 cycle 时报错，但没有 public authoring API。
- 需要决定：新增 API 是否应在写入时验证 canonical object 存在及 alias graph 无环。
- 选项：
  - A) 在写入时强校验对象存在和无环。
  - B) 只校验名称并 upsert metadata，沿用 resolve-time cycle detection。
- 当前建议：B。
- 用户答复：用户要求实现 C# 已有、Bun 缺失的能力。
- 最终决策：B。
- 决策理由：C# public API 是名称校验后直接 upsert；保持同一底层治理语义并避免破坏 migration 编排。
- 状态：resolved。

### 2. 【P0】删除的时间语义
- 背景：Bun properties and edges carry valid-time history.
- 需要决定：`deleteEntity` 是否只撤回当前投影，还是删除全部历史事实。
- 选项：
  - A) 当前时点 retraction。
  - B) 物理删除所有历史 property/edge row。
- 当前建议：B。
- 用户答复：用户要求实现 C# `DeleteEntityAsync` 对等能力。
- 最终决策：B。
- 决策理由：与 C# 级联物理删除一致，避免历史层留下孤儿实体引用。
- 状态：resolved。

### 3. 【P1】执行工作流默认值
- 背景：用户已批准 track 并要求立即实现。
- 需要决定：提交与终态校验如何执行。
- 选项：
  - A) 自动提交并等待人工确认。
  - B) 手动提交、每 phase coding 审查、终态 Gap Loop。
- 当前建议：B。
- 用户答复：用户要求立即开始实现。
- 最终决策：B。
- 决策理由：不擅自提交用户的工作树，同时保留独立终态验证。
- 状态：resolved。
