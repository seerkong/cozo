# Decisions

## Usage
- G1-T2 映射设计已定大部分；执行期决策追加至此

### 1. 【P1】新建 Om.Depa capsule
- 解释层与观测层分包；依赖单向。状态：decided（design 补充节）

### 2. 【P1】ck_external_call 并入 v2 schema（add-only 不升版本）
- 新表不构成破坏性变更；reindex 语义不变。状态：decided

### 3. 【P2】SyncEffectApisAsync 收敛为 internal（AttractorCheck GAP 修复）
- design 公开面清单只含 InitDepaOntologyAsync + DepaScanAsync；SyncEffectApisAsync 越界，改 internal。
- DepaScanAsync（DepaScanPipeline.ScanAsync 步骤 ① 前）已自动 sync effect apis（走 DepaScanOptions.EffectsPath），公开工作流不缺能力；tests 经既有 InternalsVisibleTo 直接调用。状态：decided（执行期）

### 4. 【P2】内置 effect 词表归观测层 Om.CodeKnowledge（AttractorCheck GAP 修复）
- design §4.1 原文：内置表"编译进 Om.CodeKnowledge"。实现曾放在 Om.Depa/DepaEffectCatalog，导致 Om.CodeKnowledge 写入路径反向引用 Om.Depa，违反 decision #1 的单向依赖。
- 修复：内置 pattern/category/direction 静态表 + glob 匹配器迁至 src/Om.CodeKnowledge/EffectApiBuiltins.cs（internal EffectApiRule/EffectApiBuiltins）；ck_external_call 写入路径只用它。Om.Depa 的 DepaEffectCatalog 以 EffectApiBuiltins.Rules 为 BuiltIn 基座、GlobMatch 委托之，并保留用户 depa-effects.json 合并逻辑，方向恢复 Om.Depa → Om.CodeKnowledge。状态：decided（执行期）
