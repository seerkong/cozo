# Decisions

## Usage
- 用于记录需要用户确认的决策问题、选项、最终结论与理由
- 问题标题不用字母前缀；字母只用于选项
- 后续执行过程中出现的新决策，也继续追加到本文件，不新建分散的决策记录

### 1. 【P0】C# OM 放置位置
- 背景：C# OM 可以放在现有 dotnet package 内，也可以拆独立项目。
- 需要决定：第一版放置在哪里。
- 选项：
  - A) 放入现有 `cozo-lib-dotnet` 包内
  - B) 新建独立 C# OM 包
- 当前建议：A
- 用户答复：选择 A
- 最终决策：A —— 放入 `cozo-lib-dotnet/src/Om/`，随现有 NuGet 包交付
- 决策理由：降低发布和依赖复杂度；当前 `.csproj` 已包含 `src/**/*.cs`
- 状态：confirmed

### 2. 【P0】内部架构形态
- 背景：可以逐行移植 Node OM，也可以按 DEPA 重建 C# capsule。
- 需要决定：C# OM 内部结构。
- 选项：
  - A) DEPA capsule：contract / runtime / input / logic / support 分层
  - B) 单个 `CozoOm` service 类集中实现
  - C) 逐行翻译 `cozo-om.js`
- 当前建议：A
- 用户答复：同意 DEPA 文件目录结构
- 最终决策：A —— 采用 `guidance/depa-capsule-layout.md` 中的结构
- 决策理由：C# 版应保留行为 parity，同时用显式 runtime/effect contract 降低长期维护成本
- 状态：confirmed

### 3. 【P0】存在规则 v1 范围
- 背景：Node OM 已确认存在规则 v1 的 head 形态。
- 需要决定：C# 版是否扩大范围。
- 选项：
  - A) 保持 Node OM v1：`exists { rel, direction?, toType }`
  - B) 同时新增属性存在性 head
- 当前建议：A
- 用户答复：已同意此前建议
- 最终决策：A —— C# 版保持同等语义，不扩大 head 形态
- 决策理由：属性存在性已由 required attribute + validation 覆盖，重复建设会造成语义漂移
- 状态：confirmed
