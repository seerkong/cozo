# Decisions

## Usage
- mission 契约已定；执行期决策追加至此

### 1. 【P1】policies 类目不建
- 代码仓无 policy 证据源；空类目违反 E-C1/M 系空类目规则。状态：decided

### 2. 【P1】context 截断策略
- 超 MaxContexts 丢弃+计数，不造 misc 容器。状态：decided

### 3. 【P2】context index doc_role=canonical（与 std 模板 guide 的显式偏离）
- std docs-modeling-fractal 小模板的 context index 写 doc_role: guide；本实现取 canonical——context index 承载 Boundary/Not Owned Here 等真源性内容，且 behavior delta 语句即写 canonical。fixtures 无对应 [P] 规则，checker 两侧容许。状态：decided
