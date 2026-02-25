# Decisions

## Usage
- 执行期决策追加至此

### 1. 【P1】examples 占位
- 最终决策：examples/index.md 占位说明（真实样例依赖后续需求），不硬造内容
- 状态：decided（design §1）

### 2. 【P2】examples 类目不建（推翻 #1 的占位页）
- 背景：T2.1 实现 checker 时发现 fixtures E-C1 明文「含 index.md 但无任何叶子的类目目录判失败」——占位 examples/index.md（无叶子）与 [P] 100% 通过直接冲突。
- 最终决策：生成器不再产出 examples/ 类目（缺哪类就不建，规范 docs-engineering-fractal §3 原话）；plane index 导航表相应去掉 examples 行；待真实 worked example 需求出现时再建类目（届时按 E-G 演化规则）。
- 影响：BuildInventory 移除 ExamplesIndexPage；FractalWikiEngineeringFractalTests 断言 examples 目录不存在。
- 状态：decided（T2.1，supersedes #1）
