# Decisions

## Usage
- 执行期决策追加至此

### 1. 【P0】删除语义 = 物理删
- DeleteEntityAsync 物理级联删（实体+属性+出入边）；时态 retract 留需求出现时。理由：首个消费者（DEPA violation 清理）是可重建投影，无历史价值。状态：decided

### 2. 【P1】REC-5 = known-exception（零代码）
- Om.Depa 消费 Om.CodeKnowledge internal EffectApiBuiltins：同 assembly 合法；public 化违反最小公开面纪律；裁定为跨 capsule 边界的显式例外——观测层词表唯一合法消费者是解释层，注释已双向自证（G5 track decisions #4）。状态：decided

### 3. 【P1】V-F2 五条 = 误报，整改走标注侧
- OmMutationContext 是作用域 facade（五方法均单表达式委托到 Logic 静态函数），真载体 CozoOmRuntime 零方法合规；仓根 depa-map.json 声明 runtimeCarrierTypes 压制 heuristic。工具侧"纯委托豁免"留 backlog（B-3）。状态：decided
