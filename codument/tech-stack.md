# 技术栈

本文档定义了项目的技术选型和架构决策。

## 编程语言

| 语言 | 版本 | 用途 |
|------|------|------|
| Rust | stable | 核心引擎与主要 crate（`cozo-core/` 等） |
| JavaScript | ES2020+ | Node.js/Bun 绑定与示例（`cozo-lib-nodejs/`, `cozo-lib-bun/`） |
| TypeScript | 5.x | Viz 前端类型检查与构建（`cozo-lib-bun-viz/frontend/`） |
| Python | 3.x | Python 绑定（`cozo-lib-python/`） |
| Java | 8+ | Java JNI 绑定（`cozo-lib-java/`） |
| C | C11 | C FFI 共享库（`cozo-lib-c/`） |
| Swift | 5.x | Swift/iOS 绑定（`cozo-lib-swift/`） |
| C# | .NET 8+ | .NET 绑定（`cozo-lib-dotnet/`） |

## 运行时

| 运行时 | 版本 | 用途 |
|--------|------|------|
| Rust | stable | `cozo` crate、CLI 与服务端（`cozo-bin/`） |
| Node.js | 16+ | `cozo-node` 绑定运行时（`cozo-lib-nodejs/`） |
| Bun | 1.x | `cozo-lib-bun` 绑定与 viz server（`cozo-lib-bun/`, `cozo-lib-bun-viz/server/`） |
| Browser (WASM) | - | `cozo-lib-wasm/` |
| JVM | 8+ | `cozo-lib-java/` |
| Python | 3.x | `cozo-lib-python/` |
| .NET | 8+ | `cozo-lib-dotnet/` |

## 框架与库

### 后端

| 名称 | 版本 | 用途 |
|------|------|------|
| axum | 0.7.x | `cozo-bin/` HTTP/REST 服务（Rust） |
| Elysia | ^1.2.25 | 本体可视化后端（`cozo-lib-bun-viz/server/`） |
| @elysiajs/cors | ^1.2.0 | viz server CORS |

### 前端

| 名称 | 版本 | 用途 |
|------|------|------|
| Vue | ^3.5.13 | 本体可视化前端（`cozo-lib-bun-viz/frontend/`） |
| Vite | ^6.0.0 | 前端构建与开发服务器 |
| vis-network | ^9.1.9 | 图可视化 |
| @tanstack/vue-table | ^8.21.3 | 结果表格 |
| UniverJS | ^0.15.1 | 电子表格编辑（ontology workbook） |

### 测试

| 名称 | 版本 | 用途 |
|------|------|------|
| Bun Test | 内置 | 单元测试 |
| Cargo test | 内置 | Rust crate 测试（`cozo-core/` 等） |
| Playwright | ^1.55.0 | viz E2E 测试（`cozo-lib-bun-viz/`） |

## 数据库

| 类型 | 名称 | 用途 |
|------|------|------|
| Datalog RDBMS | CozoDB | 核心数据库引擎（`cozo-core/`） |
| Storage engine | SQLite | 默认存储后端（feature） |
| Storage engine | RocksDB | 可选存储后端（feature + `cozorocks/`） |

## 开发工具

| 工具 | 用途 |
|------|------|
| Cargo | Rust 构建与测试 |
| Bun | JS/TS 依赖管理与运行（viz + bun binding） |
| node-pre-gyp | Node.js 绑定预编译包分发（`cozo-lib-nodejs/`） |

## CI/CD

| 平台 | 用途 |
|------|------|
| GitHub Actions | 构建与测试（`.github/workflows/build.yml`） |

## 部署环境

| 环境 | 平台 | 用途 |
|------|------|------|
| 开发 | 本地 | 开发和测试 |
| 生产 | - | 生产部署 |

## 架构决策

1. **查询语言**：以 Datalog/CozoScript 为主要查询接口，优先支持递归与图分析表达能力（见 `README.md`）。
2. **存储抽象**：通过 Cargo feature 切换存储后端（SQLite/RocksDB/…），尽量将引擎逻辑与存储实现解耦（见 `cozo-core/`）。
3. **多语言生态**：围绕同一 Rust 内核提供多语言绑定（`cozo-lib-*`），并保留可嵌入/可服务化两种形态（见 `cozo-bin/`）。

---

## 技术约束

1. **版本兼容性**：所有依赖必须支持指定的运行时版本
2. **安全更新**：依赖必须定期更新以修复安全漏洞
3. **许可证**：只使用与项目许可证兼容的依赖

## 技术债务

| ID | 描述 | 优先级 | 状态 |
|----|------|--------|------|
| - | - | - | - |



---

*最后更新: 2026-02-28*
