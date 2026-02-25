# rule-map — 判据目录 ↔ 工具规则三方映射（真源）

> 本表是 `violation-catalog.md`（A1..G5 共 35 条红灯）与各核查表专属项到 Om.Depa 工具规则（V-*）的三方映射，
> 与 `DepaReportQueries.RulesByDimension` / `PlaceholderRuleIds` 双向对齐（由 tests/Program.cs 的
> rule-map-anchored 测试机检）。规划真源：codument/tracks/expand-depa-detection-rules/analysis/gap-matrix.md。

## 状态词表

| 状态 | 含义 |
|------|------|
| implemented | 检测器已实现，语义与目录条目对齐 |
| implemented-weakened | 已有检测器弱化覆盖（现象归并/子集覆盖），备注注明缺口 |
| placeholder-BLOCKED | 无检测器、入报告分母、恒 BLOCKED，reason 命名缺失观测 |
| planned-batch1 | 本 track 批次一（零新标注，P2）实现后改 implemented |
| planned-batch2 | 本 track 批次二（depa-map add-only 键，P3）实现后改 implemented |
| human-only | 人工核查表专属，不进工具分母（delta 语句口径） |

## violation-catalog 目录映射（A1..G5）

| 条目 | 红灯（缩写） | 维度 | 工具规则 | 状态 | 备注 |
|------|--------------|------|----------|------|------|
| A1 | 状态只活在内存、无可重建源 | data | V-D2 | placeholder-BLOCKED | 需运行时语义：状态可重建性（事件源→重放）无法从静态符号图证明 |
| A2 | 多个半事实源对同一真相同时生效 | data | V-D1 | implemented | 分级事实源多写者 |
| A3 | 状态靠"先写文件再回读"传递 | data | V-E1 | implemented-weakened | 归并为 core 直接 IO 现象，未判"写后回读"数据流 |
| A4 | 投影/读模型与事实纠缠 | data | V-D3 | implemented | 同 symbol 同时标 projection 与 grade≤3 fact_source；另判 projection 被闭包外符号写 |
| B1 | 核心逻辑里直接 IO | effect | V-E1 | implemented | |
| B2 | 副作用契约与编排糊在一起 | effect | V-E3 | implemented-weakened | contract 符号成员有 CALLS 出边；无调用的组装表达式（new/字段初始化）符号级不可见 |
| B3 | 隐式全局依赖 | effect | V-E2 | implemented | core 访问静态可变字段 |
| B4 | 应用级 registry 只经全局变量传递 | effect | V-E2 | implemented-weakened | 归并为静态可变字段访问现象，未识别 registry 语义 |
| C1 | 多策略用 if/elif 字符串分发 | processor | V-P1 | placeholder-BLOCKED | 需语句级 AST：方法体分支结构不在符号级观测内 |
| C2 | 未知 id 静默回退/返回 None | processor | V-P3 | placeholder-BLOCKED | 需语句级 AST：静默回退分支（return null/None）在方法体语句层 |
| C3 | command 与 message 边界混用 | processor | V-P4 | placeholder-BLOCKED | 需运行时语义：等应答/单向的调用语义静态不可判 |
| C4 | 处理逻辑对调用域有感知 | processor | V-P2 | implemented-weakened | depa-map layers[]（低→高声明）：低层符号 CALLS/ACCESSES 高层即命中；IMPORTS 目标退化为 import:* 哈希节点不可解析故实际不参与（与 capsule_depends_on 同口径）；violation 置信度如实携带观测边 confidence（name-only ambiguous 解析 0.5 不冒充 1.0，evidence 标 heuristic，T3.1 复扫裁定）；命中主体挂靠 impl/capsule，capsule 外符号的命中被跳过；未声明 layers/capsules 则 BLOCKED |
| D1 | 并发任务直接改同一可变对象 | actor | V-A* | placeholder-BLOCKED | 需运行时语义：并发/mailbox 行为静态观测不足 |
| D2 | 用裸锁糊共享状态替代消息 | actor | V-A1 | implemented-weakened | 现象级：threading 外呼密度（符号聚合 count≥3），heuristic conf≤0.6、evidence 标 heuristic；"锁糊共享状态"语义未证 |
| D3 | 调度与身份纠缠 | actor | — | human-only | 人工核查表专属 |
| D4 | 缺 selective receive | actor | — | human-only | 人工核查表专属 |
| D5 | 用隐式 DI 把循环依赖藏起来 | layering | V-L2 | implemented | 跨 capsule CALLS/ACCESSES 依赖图强连通分量；与 CP2§5 共用 |
| E1 | checkpoint 读取后影响 live 控制流 | fact_source | V-S2a | implemented-weakened | 现象级：grade-5 快照被非 recoveryPaths 内符号 ACCESSES-read 即命中（conf≤0.7、evidence 标 heuristic）；"读后影响控制流"语义未证；projection 读者与自身闭包豁免（正常派生/自访问）；未声明 recoveryPaths 或无 grade-5 节点则 BLOCKED |
| E2 | journal 字段被用来判断下一步 | fact_source | V-S2b | implemented-weakened | 现象级：grade-4 流水账被非 recoveryPaths 内符号 ACCESSES-read 即命中（conf≤0.7、evidence 标 heuristic）；"用于判下一步"语义未证；projection 读者与自身闭包豁免；未声明 recoveryPaths 或无 grade-4 节点则 BLOCKED |
| E3 | UI 状态反向驱动主循环 | fact_source | V-S1 | implemented | V-S1 扩展：源扩至 grade-6/7 节点、目标扩至 grade≤3；grade-6/7 命中只留证据不发 backwrites 信号边（关系类型限投影） |
| E4 | 文件 mtime/存在性被用来判运行状态 | fact_source | V-S4 | implemented-weakened | ck_external_call 命中 Exists/LastWriteTime/CreationTime 词表且 caller=core；现象级 conf≤0.6、evidence 标 heuristic，"用于判状态"语义未证 |
| E5 | 投影算完反写源事实 | fact_source | V-S1 | implemented | |
| E6 | 快照夹带单次 payload | fact_source | V-S3 | implemented-weakened | grade-5 字段类型 ∈ input-role declared_type 集；字段类型文本匹配近似"夹带 payload"，序列化路径未证 |
| F1 | 长生命周期对象藏在 input/config | layering | V-F1 | implemented-weakened | 仅覆盖函数对象子集；客户端/会话等类型语义判定由 runtime-explicitness.md 人工兜底 |
| F2 | 函数对象塞 config | layering | V-F1 | implemented | dimension 修正：F 组所属=分层，见 decisions.md |
| F3 | config 重复 runtime 字段 | layering | V-F3 | implemented | config-role declared type 与 runtime 侧（carrier/runtime-role declared type）字段名+类型文本交集 |
| F4 | 业务逻辑写在 runtime dataclass 方法里 | layering | V-F2 | implemented-weakened | 所属=分层/Effect，主归属分层；dimension 修正见 decisions.md。纯委托豁免（track refine-depa-detection-precision T2.1，B-3）：方法出边仅 1 条 CALLS 且无 write ACCESSES → 视为单表达式委托 facade（OmMutationContext 形态），不产 GAP；弱化说明——ck_* 粒度下"单表达式"以 CALLS==1 + 无写访问近似，read ACCESSES 不参与判定（读转发/读后转发不可分辨），带读逻辑的单 CALLS 方法会被一并豁免；多 CALLS 或含写访问仍报 |
| F5 | 外部 import 触及 internals.* | layering | V-L1 | implemented | |
| F6 | 跨 capsule 触对方 internals / types 借 internal | layering | V-L1 | implemented-weakened | internals 交叉半条已覆盖 |
| F6 | 契约签名引他 capsule internal 类型 | layering | V-L5 | implemented-weakened | contract 成员签名文本按词边界匹配他 capsule internals 类型名；文本匹配非语义 type 解析 |
| F7 | contract 与 logic 反向依赖 | layering | V-L3 | implemented | 反向依赖半条 |
| F7 | 同名契约双处定义 | layering | V-L6 | implemented-weakened | 同名 type 同现 contract 包与他 capsule；同名即命中，未比对结构同一性 |
| G1 | 同构却强造空壳 adapter | overdesign | V-G2 | placeholder-BLOCKED | 需人工语义比对：outer/inner 字段同构与"只透传"判定 |
| G2 | 只有一个实现的策略注册表/多态 | overdesign | V-G1 | implemented-weakened | IMPLEMENTS 计数==1 信号 conf≤0.7；effect contract 豁免（单实现契约是 DEPA 自身范式）；无 capsule 归属的抽象跳过；枚举注册表侧不可见 |
| G3 | "以防万一"的配置开关无人用 | overdesign | V-G3 | placeholder-BLOCKED | 需语句级 AST：调用点实参是否全程默认值 |
| G4 | 复杂度由"将来可能"驱动 | overdesign | — | human-only | 人工核查表专属 |
| G5 | 自造已有 vendor 原语 | vendor | — | human-only | 人工核查表专属；工具侧 vendor 维观测缺口由 V-V* 占位承接 |

## 核查表专属项

| 条目 | 红灯（缩写） | 维度 | 工具规则 | 状态 | 备注 |
|------|--------------|------|----------|------|------|
| CP1§5 | DOP fn(r,i,c) 覆盖缺失 | processor | V-C1 | implemented-weakened | core 闭包内 role 标注参数覆盖 runtime/input/config；仅裁决有角色标注参数的 core，参数未观测/未标注的 core 跳过（全跳过则 BLOCKED） |
| CP2§5 | capsule 依赖环 | layering | V-L2 | implemented | 与 D5 共用一条规则 |
| CP2§6 | capsule 多入口 | layering | V-L4 | implemented | capsule_exposes 出边计数 >1，逐入口 path:line 证据 |
| RE | runtime=Any/大袋子 | layering | V-R1 | implemented-weakened | runtime-role declared_type / carrier 字段签名判大袋子词表（Dictionary<string,object>/object/dynamic/ExpandoObject/Hashtable）；词表现象级 conf≤0.7、evidence 标 heuristic |
| DC⑦ | vendor 优先（自造原语核查） | vendor | V-V* | placeholder-BLOCKED | 需人工语义比对："自造 vendor 原语"需与 vendor 能力清单人工比对 |

## 口径

- **工具分母（报告 coverage）** = 状态 ∈ {implemented, implemented-weakened, placeholder-BLOCKED} 的规则集合
  （当前 24 实现[8 原有 + 批次一 13 新 id + V-S1 扩展同 id + 批次二 3 新 id] + 8 占位 = 32 条），按 8 维分组：
  data / effect / processor / layering / fact_source / actor / overdesign / vendor。human-only 4 条（D3/D4/G4/G5）不进分母。
- **planned-batch1（14 行）已于 T2.1 落地**、**planned-batch2（3 行：V-S2a/V-S2b/V-P2）已于 T3.1 落地**
  （状态改 implemented / implemented-weakened 并入分母，弱化逐条注明缺口）。批次二依 depa-map add-only 键
  recoveryPaths[]（恢复路径 glob）与 layers[]{name, pathGlobs[]}（层标注，低→高声明）判定，未声明恒 BLOCKED。
- 占位规则的 BLOCKED reason 逐条命名缺失观测类别（需语句级 AST / 需运行时语义 / 需人工语义比对），
  与 `DepaReportQueries` 的 PlaceholderRules 保持一致。
