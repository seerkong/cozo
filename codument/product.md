# CozoDB - 产品定义

## 产品愿景

提供一个可嵌入、高性能、事务性（ACID）的关系数据库内核，以 Datalog/CozoScript 作为主要查询语言，面向图数据与图算法场景，同时可在多语言生态中以绑定形式被集成。

## 目标用户

- 需要在应用进程内嵌入数据库的开发者（移动端/桌面端/边缘侧/单机服务）
- 需要用 Datalog 表达递归与图分析工作流的开发者与数据工程师
- 需要在多语言环境（Rust/Node.js/Python/Java/.NET/Swift/WASM/Bun）中调用同一数据库内核的团队

## 核心功能

- Datalog/CozoScript 查询：即席联接、递归规则、聚合与图算法（见 `README.md`）
- 多存储后端：SQLite（默认）、RocksDB 等（通过 Cargo features 选择，见 `cozo-core/`）
- 运行形态：嵌入式库调用 + CLI/HTTP 服务（见 `cozo-bin/`）
- 多语言绑定：Node.js/Python/Java/C/.NET/Swift/WASM/Bun（见各 `cozo-lib-*`）
- 本体建模层（cozo-om）：在 Bun 绑定中提供轻量本体/属性图建模能力，并支持 Action/Mutation/Interceptor、复合约束与派生属性；配套 viz demo（见 `cozo-lib-bun/cozo-om.js`、`cozo-lib-bun-viz/`）

## 成功指标

- 核心引擎与主要绑定在 CI 上构建/测试稳定（见 `.github/workflows/build.yml`）
- 文档与示例可让新用户在 30 分钟内完成安装并跑通第一个查询与 demo
- 关键模块（如 `cozo-lib-bun`）保持单元测试覆盖率目标 >80%（见 `codument/std/workflow.md`）

---

*最后更新: 2026-02-28*
