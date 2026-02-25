# Decisions

## Usage
- 执行期决策追加至此

### 1. 【P0】规则强化三方案用户裁定
- 全部 16 条静态可补 + rubrics 复制进项目真源（src/Om.Depa/rubrics/，与 ~/.claude 双源各自演化、项目真源优先）+ 改写全局 skill 吸收 4 条口径。状态：decided（用户 2026-07-06）

### 2. 【T1.1】V-F1/V-F2 dimension 归属裁定：processor → layering
- 依据（项目内真源 src/Om.Depa/rubrics/violation-catalog.md F 组「分层类（runtime/input/config 归位 + contract/logic 分离）」原文）：
  - V-F1（config 类型字段出现函数对象）对应 F 组「函数对象塞 config」行，原文所属＝**分层**（"config dataclass 字段类型出现 `Callable`/闭包 → 函数是 effect 契约，移 runtime.effect"）。
  - V-F2（runtime carrier 承载业务方法）对应 F 组「业务逻辑写在 runtime dataclass 方法里」行，原文所属＝**分层 / Effect**，主归属分层——该红灯的判定证据是"归位错误"（runtime 应为纯数据），而非副作用泄漏本身（B 组语义），故不归 effect。
- 结论：两条规则 dimension 字段与 RulesByDimension 均改 layering；catalog C 组（Processor＝处理/分发维）与 V-F1/F2 语义无关，原 processor 归属为实现期误标。状态：decided（T1.1，2026-07-06）

### 3. 【T1.1】coverage 全目录口径与占位纪律
- 报告分母＝rule-map.md 中 implemented（8）+ placeholder-BLOCKED（8）共 16 条，按 8 维分组（新增 overdesign/vendor）；planned-batch1（14 条）/batch2（3 条）落地后入分母；human-only 4 条（D3/D4/G4/G5）标注在 rule-map 但不进工具分母（delta 语句口径）。
- 占位 BLOCKED reason 逐条命名缺失观测类别（需语句级 AST / 需运行时语义 / 需人工语义比对，按 gap-matrix 分类），禁止笼统措辞。状态：decided（T1.1，2026-07-06）
