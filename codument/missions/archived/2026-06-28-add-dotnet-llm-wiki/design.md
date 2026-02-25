# Mission Design

## 控制论模型

- desired state：`mission.xml` 的 DAG、track 候选、验收节点、成功判据。
- actual state：当前 `cozo-lib-dotnet` 能力、GitNexus 参考实现、`cozo-lib-dotnet-llm-wiki/packages` 文件状态、track 状态、测试结果、用户新约束。
- actuation：创建/修订/实现 track，写 gap report，落地 packages，运行 build/test/smoke，必要时受控重规划。
- feedback / drift：mission reports、track findings、build/test 结果、MCP smoke 结果、用户对边界的调整。

## Mission Actors

| Actor | 控制论角色 | DEPA 归属 | 职责 |
|---|---|---|---|
| `MissionPlanner` | 期望态产出者 | Processor + Actor | 产出或修订 mission DAG、track 切片和首批 MVP 边界 |
| `MissionObserver` | 传感器 | Data + Actor | 读取 GitNexus 参考、当前 C# OM 能力、track 状态和测试结果 |
| `MissionReconciler` | 控制器 | Processor + Actor | 比较 desired vs actual，判定 gap、ready node、drift、blocked、done |
| `MissionApplier` | 执行器 | Effect + Actor | 执行一个 bounded action：写分析、创建 track、实现 track、验证或收口 |

## DEPA 包边界

- Data：`LlmWiki.Core` 保存请求/结果模型、索引事实模型、MCP payload 模型和配置对象。
- Effect：`LlmWiki.Indexing` 负责读取文件系统、git 元信息、hash 与把事实写入 OM；`LlmWiki.McpServer` 负责 stdio。
- Processor：`LlmWiki.Wiki` 编译 wiki；索引 extractor 将文件文本转换为 CodeKnowledge batch。
- Actor：`LlmWiki.McpServer` 是 agent-facing actor；后续可加 watcher/daemon actor。

## 首批 track 切片

第一批只创建一个可闭环 track：`add-dotnet-llm-wiki-mcp`。

该 track 的边界：

- 创建 packages 目录和四个 project：
  - `Cozo.DotNet.LlmWiki.Core`
  - `Cozo.DotNet.LlmWiki.Indexing`
  - `Cozo.DotNet.LlmWiki.Wiki`
  - `Cozo.DotNet.LlmWiki.McpServer`
- 实现 MVP scanner / extractor / wiki compiler / MCP stdio。
- 引用现有 `cozo-lib-dotnet/Cozo.DotNet.csproj`，通过 `Om.CodeKnowledge` 存储和查询。
- 对 coding agent 暴露稳定工具，不要求 agent 学 CozoScript。

## 受控重规划

active mission 可以在以下情况下重规划：

- `cozo-lib-dotnet` API 发生重构，导致引用路径或 namespace 改变。
- MCP 协议实现需要替换为正式 SDK 或拆出 shared transport。
- smoke test 发现 native Cozo 加载无法在当前环境运行，只能降级为 build-only。
- 用户决定提前加入 HTTP/Web UI、多仓库或 embedding。

每次重规划必须写 `reports/replan-XXX.md`，更新 `mission.xml` 的 `Revision` 与 `UpdatedAt`。

## 风险

- 当前 MVP 的符号抽取使用轻量启发式，不具备 GitNexus 完整语义解析精度。
- Cozo native library 可能受运行环境影响；测试应先覆盖 build，再覆盖 mem engine smoke。
- MCP stdio 需要严格避免普通日志污染 stdout；日志应走 stderr。
- 多 project 引用相对路径较深，构建脚本和 README 需要明确命令。
