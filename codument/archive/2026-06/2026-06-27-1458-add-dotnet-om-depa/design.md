# 方案设计：基于 DEPA 的 C# OM

## 上下文

`cozo-lib-dotnet` 当前只提供底层数据库操作。Node/Bun 版 `cozo-om.js` 已承载完整本体层能力，但它是一个大型 JS 文件，不适合在 C# 版中逐行复制。C# 版应借此机会把边界定清：公共 API 易用，内部遵守 DEPA 的 `output = fn(runtime, input, config)`。

## 方案概览

1. **DEPA capsule 目录**
   - 目标目录固定为 `cozo-lib-dotnet/src/Om/`
   - 必须按 `guidance/depa-capsule-layout.md` 的结构创建
   - `CozoOm.cs` 是公开 facade；`Logic/` 才是行为承载层

2. **Effect contract**
   - `Contracts/ICozoOmStore.cs` 定义 OM 所需 CozoScript 执行能力
   - `Support/CozoDbOmStore.cs` 包装现有 `CozoDb`
   - `Logic/` 不直接引用 `CozoDb` 或 `CozoNative`

3. **Runtime / input / config 归位**
   - `CozoOmRuntime` 只持有 store、clock/options 等长生命周期依赖
   - 单次 payload 使用 `Inputs/*`
   - 单次静态开关使用 config record；没有实际配置时不制造空泛策略层

4. **功能切片**
   - 第一批实现基础 schema/TBox/ABox 能力，跑通端到端
   - 后续按 Node OM 能力域分片迁移：继承/别名、temporal、constraints/computed/actions、schema versioning、permission、existential rules
   - 每片都先写 parity tests，再实现

5. **事实源边界**
   - Cozo relations 是 OM authoritative facts
   - C# model objects、entity views、schema diffs、graph visuals 是 projections
   - snapshots 是 checkpoint，不成为正常 live 写入判断的第二真源

## 影响范围与修改点（Impact）

- `cozo-lib-dotnet/src/Om/**`：新增 OM capsule
- `cozo-lib-dotnet/README.md`：补充 OM 使用说明
- `cozo-lib-dotnet/example/**`：按需增加最小 OM 示例
- `cozo-lib-dotnet` 测试配置：新增或扩展 .NET 测试项目

## 决策摘要

- Track id：`add-dotnet-om-depa`
- 目标：在 `cozo-lib-dotnet/src/Om/` 基于 DEPA capsule 实现与 Node OM 同等的 C# 本体论封装
- 目录结构：采用 `guidance/depa-capsule-layout.md`
- 提交模式：manual
- 校验模式：最终 phase GapLoop，`verify-round=false`

## 风险 / 权衡

- **Node OM 功能面大**：按 parity slice 推进，避免一次性大爆炸实现。
- **C# API 易用性 vs DEPA 纯度**：允许 `CozoOm` 做 facade，但业务逻辑必须下沉到 `Logic/`。
- **测试基座缺失**：实现阶段先建立最小 .NET test harness，再迁移功能。
- **JSON/CozoScript 行解析脆弱**：集中在 `Support/` 和 `Internals/`，避免散落在业务逻辑中。
- **存在规则终止语义**：沿用 Node OM 的 Skolem chase + maxIterations 语义，不在本 track 扩大到 restricted chase。

## 待解决问题

- 选择 .NET 测试框架和测试项目布局。
- 确定 C# API 命名是否完全贴近 Node OM 函数名，还是采用 idiomatic async method 命名；默认建议 C# public facade 使用 `Async` 后缀，模型语义保持 parity。
