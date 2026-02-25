# Decisions

## Usage
- 用于记录需要用户确认的决策问题、选项、最终结论与理由
- 问题标题不用字母前缀；字母只用于选项
- 后续执行过程中出现的新决策，也继续追加到本文件，不新建分散的决策记录

### 1. 【P0】v1 head 形态
- 背景：存在规则的 head 理论上可以是「∃ 关系边 + 目标实体」「∃ 属性值」等多种形态
- 需要决定：v1 支持哪些 head 形态
- 选项：
  - A) 仅 `exists: { rel, toType }`（∃ 关系边 + 目标实体）
  - B) 同时支持属性存在性
- 当前建议：A
- 用户答复：同意 A
- 最终决策：A —— v1 仅支持 `exists: { rel, direction?, toType }`
- 决策理由：关系边存在性覆盖主数据场景绝大多数存在约束；属性存在性已被 `required` 属性 + `validateConstraints` 覆盖，不重复建设
- 状态：confirmed

### 2. 【P0】Track ID
- 需要决定：track 命名
- 选项：
  - A) add-existential-rules
  - B) add-existential-constraints
- 用户答复：确认 A
- 最终决策：add-existential-rules
- 决策理由：与已归档 track 命名风格一致（add-type-hierarchy / add-temporal-dimension 等）
- 状态：confirmed

### 3. 【P1】viz 演示范围
- 背景：是否在本 track 内提供可视化演示
- 选项：
  - A) governance 页新增「完整性检查」子 tab
  - B) 独立 /integrity 顶部导航页
  - C) 不含 viz，纯 om 层
- 用户答复：A
- 最终决策：A —— 在 `/governance` 页新增「完整性检查」子 tab：展示违例列表 + 一键 chase 物化
- 决策理由：复用现有页面骨架与子 tab 结构，工作量小；治理语义上与权限同属 governance 主题
- 状态：confirmed

### 4. 【P1】Skolem 实体来源标记
- 背景：chase 物化出的实体需可审计、可清理
- 选项：
  - A) 内置属性 `_skolem_rule`（setProperty 写入规则名）
  - B) 专门关系表 om_skolem_origin
  - C) 仅靠 id/label 前缀约定
- 用户答复：A
- 最终决策：A —— Skolem 实体上写 `setProperty(id, '_skolem_rule', ruleName)`
- 决策理由：零新表，走现有 bi-temporal 属性存储，历史可追溯；避免 snapshot/rollback 多处理一张表
- 状态：confirmed

### 5. 【P2】chase 终止防护程度
- 背景：循环规则依赖（A 造的实体触发 B，B 又触发 A）可能不终止
- 选项：
  - A) 仅 maxIterations 兜底（到顶未收敛返回 reachedFixpoint:false + 诊断）
  - B) 另加 defineExistentialRule 时的静态无环预检（检测到环警告）
- 用户答复：A
- 最终决策：A —— v1 仅 maxIterations 兜底
- 决策理由：实现简单、语义清楚；静态预检留作后续增强
- 状态：confirmed
