# CozoDB Monorepo

## 项目概述

CozoDB 是一个通用、事务性、关系型嵌入式数据库，使用 **Datalog** 查询语言，专注于**图数据与算法**，支持时间旅行（time travel）。当前版本 `0.7.6`（见 `VERSION`）。

## 仓库结构

- **`cozo-core/`** — 核心引擎 crate（发布为 `cozo`），包含 Datalog 解析/执行、存储抽象、图算法、HNSW 向量索引、FTS 全文搜索等。通过 Cargo features 选择后端：`storage-sqlite`（默认）、`storage-rocksdb`、`storage-sled`、`storage-tikv`。
- **`cozorocks/`** — RocksDB 的 Rust FFI 封装（含 RocksDB 源码子模块）。
- **`cozo-bin/`** — 独立可执行文件，提供 REPL 交互式终端和 HTTP/REST API 服务（基于 axum）。
- **`cozo-lib-bun/`** — Bun 运行时绑定（FFI），含本体建模 DSL（`cozo-om.js`、`cozo-dsl.js`），并支持 Action/Mutation/Interceptor、复合约束、派生属性等行为层能力，以及存在规则（声明式定义、违例检测、Skolem chase 物化，纳入 schema 版本化）。
- **`cozo-lib-bun-viz/`** — 本体可视化工具：`frontend/`（Vue 3 + Vite + vis-network）和 `server/`（Elysia + cozo-lib-bun），包含审批流（Action/Constraint/Computed + time travel 时间轴）demo 与 governance 页完整性检查（存在规则违例检测 + 一键物化）tab，附 Playwright E2E 测试。
- **`cozo-lib-nodejs/`** — Node.js N-API 绑定（发布为 `cozo-node`），使用 node-pre-gyp 分发预编译二进制。
- **`cozo-lib-python/`** — Python 绑定（发布为 `pycozo`）。
- **`cozo-lib-java/`** — Java JNI 绑定。
- **`cozo-lib-swift/`** — Swift/iOS CocoaPods 绑定。
- **`cozo-lib-c/`** — C FFI 共享库（`cozo_c`），也是 .NET / Go 等绑定的基础。
- **`cozo-lib-dotnet/`** — .NET 10 绑定，通过 P/Invoke 调用 `cozo_c`（不在 Cargo workspace 内）。
- **`cozo-lib-wasm/`** — 浏览器 WASM 绑定。
- **`cozo-core-examples/`** — 核心引擎使用示例。

## 常用构建与测试命令

| 场景 | 命令 | 说明 |
|------|------|------|
| 全量构建 | `cargo build` | 编译 workspace 所有 crate（默认 SQLite 后端） |
| 带 RocksDB | `cargo build --features storage-rocksdb` | 启用 RocksDB 后端 |
| 核心测试 | `cargo test -p cozo` | 运行 cozo-core 单元/集成测试 |
| 全量测试 | `cargo test` | 运行 workspace 所有测试 |
| Bun 绑定测试 | `bun test`（在 `cozo-lib-bun/`） | 运行 Bun FFI 绑定测试 |
| Bun 原生构建 | `bun run build-native`（在 `cozo-lib-bun/`） | 构建 `cozo-node` 原生模块并复制为 Bun 可加载的 `native/cozo_bun.node` |
| Viz 前端开发 | `vite`（在 `cozo-lib-bun-viz/frontend/`） | 启动 Vue 开发服务器 |
| Viz 后端开发 | `bun --watch src/index.js`（在 `cozo-lib-bun-viz/server/`） | 启动 Elysia API 服务 |
| Viz E2E 测试 | `playwright test`（在 `cozo-lib-bun-viz/`） | 运行端到端测试 |
| 发布构建 | `scripts/build-release-mac.sh` 等 | 平台特定的发布构建脚本 |

## CI

GitHub Actions 工作流：`.github/workflows/build.yml`。

---

*最后更新: 2026-06-10*
