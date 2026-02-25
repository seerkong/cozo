# eval — 轻量 eval 基准（track add-llm-wiki-eval-baseline）

对标 GitNexus 的 baseline/native 对比方法论，从小做起：≥20 题本仓真实任务集 + 加权关键词评分器 + oracle 直答门禁，验证「任务可被图回答、评分器可判分」，为真实 LLM agent 对比运行（使用期动作）提供可插拔骨架。

## 目录结构

```text
eval/
├── README.md            # 本文件：结构、评分口径、oracle 复现、真实 agent 对比运行步骤
├── tasks.json           # 任务集（24 题，双 workRoot）
└── prompts/
    ├── baseline.md      # baseline 模式 prompt 模板（无工具）
    └── native.md        # native 模式 prompt 模板（18 个 depa-wiki MCP 工具）
```

代码侧资产（tests 内 internal，零新增 public 面）：

- `tests/Cozo.DotNet.LlmWiki.Tests/EvalBaselineTests.cs` — `EvalTaskSet`（加载 tasks.json）、`EvalScorer`（评分器）、形态/判别性测试。
- `tests/Cozo.DotNet.LlmWiki.Tests/EvalOracleTests.cs` — `EvalOracle`（oracle 执行器）、`EvalOracleGateTests`（≥90% 门禁）。

## 任务集（tasks.json）

每题字段：

| 字段 | 含义 |
| --- | --- |
| `id` / `question` / `difficulty` | 题号、真实问题（中文，题面标注 workRoot）、easy/medium/hard |
| `workRoot` | 相对「同时包含两仓的目录」的仓库路径：`cozo-lib-dotnet-llm-wiki` 或 `cozo-lib-dotnet/src` |
| `tools` | 建议工具序列（oracle 模式据此直答）；工具名 ⊆ `LlmWikiToolRunner.ToolsJson()` 的 18 工具矩阵 |
| `must` / `should` | 期望答案要素关键词（长真实标识符，大小写不敏感 Contains 命中） |

## 评分口径（EvalScorer）

```text
score = 0.8 × (must 命中数 / must 总数) + 0.2 × (should 命中数 / should 总数)
```

- 无 should 关键词时，0.2 份额跟随 must 命中率（全命中即 1.0）。
- 命中判定：答案文本对关键词的大小写不敏感子串包含；关键词全部选用长真实标识符（类/方法/文件名），避免误命中。
- 固有限度：Contains 无法判别语境——一段**含 must 关键词但语境/结论错误**的答案仍会得分。评估真实 agent 时这是已知偏置（对 baseline/native 两侧同向），关键词选长标识符只缓解「碰巧撞词」，不缓解「答非所问但引用了对象」。
- 汇总门禁指标：**全集 must 要素命中率 = Σmust 命中 / Σmust 总数 ≥ 90%**。

## oracle 模式（门禁）

oracle = 不经 LLM 的上界验证：per workRoot 建一个真实 in-memory Cozo 库，经共享 18 工具运行器 `index_repo` 索引真实仓库，随后对每题依序执行建议工具序列，把各工具输出 JSON 序列化拼接为「答案文本」交给评分器。证明两件事：任务集可被图回答、评分器可判分。

复现命令（tests 目录）：

```bash
cd cozo-lib-dotnet-llm-wiki/tests/Cozo.DotNet.LlmWiki.Tests
EVAL_ORACLE_ONLY=1 dotnet run        # 只跑 oracle 门禁（打印每题 must/should 明细与总命中率）
dotnet run                           # 全套件（oracle 门禁包含在内）
```

调试：`EVAL_ORACLE_DEBUG=1` 会把未全中 must 的题的完整答案文本落到系统临时目录 `eval-oracle-<id>.txt`。

oracle 执行约定：

- 未显式给 `workDirectory` 的工具（`depa_conformance`/`health_score`/`detect_changes`）默认注入该题 workRoot 路径（等价于 MCP server 以 workRoot 为 cwd 运行）。
- oracle 不跑 `index_embeddings`（成本控制），`semantic_search` hybrid 安全降级为 BM25 文本通道——整词匹配声明文本（符号名 + 签名），因此「哪个 API/谁调用 X」类题的建议查询会带候选标识符；被验证的图证据是返回的符号命中（含 path:line source id）与 trace/check/depa 的关系级输出，而非查询回显。
- **防回显**：oracle 评分前会剥离每个工具输出顶层的输入回显字段（`query`/`rootId`/`targetId`/`reason`），must 命中只能来自真实返回的图证据——门禁 100% 不含任何参数回显贡献。

## 真实 agent 对比运行（使用期动作，本 track 不执行）

两模式（对标 GitNexus baseline vs native）：

| 模式 | prompt 模板 | 工具 |
| --- | --- | --- |
| baseline | `prompts/baseline.md` | 无——仅凭模型先验回答 |
| native | `prompts/native.md` | depa-wiki 的 18 个 MCP 工具 |

agent 命令可插拔：任何能「读 prompt → （可选）经 MCP 调工具 → 输出最终答案文本」的 agent CLI 都可以充当运行器，逐题替换模板占位符后调用，答案文本交给 EvalScorer 评分（复用 tests 内 internal 类型，或按上文口径另写一个薄评分脚本）。

步骤：

1. 准备 native 模式的 MCP server（baseline 跳过）——按题 workRoot 启动：

   ```bash
   dotnet run --project packages/Cozo.DotNet.LlmWiki.McpServer/Cozo.DotNet.LlmWiki.McpServer.csproj \
     -- mcp --stdio --work-dir <workRoot 绝对路径>
   ```

   Claude Code 示例（`claude mcp add`）：

   ```bash
   claude mcp add depa-wiki -- dotnet run --project <repo>/packages/Cozo.DotNet.LlmWiki.McpServer/Cozo.DotNet.LlmWiki.McpServer.csproj -- mcp --stdio --work-dir <workRoot>
   ```

2. 逐题渲染模板：把 `{{QUESTION}}` 替换为该题 `question`（native 模板另有 `{{WORK_ROOT}}`）。
3. 调用 agent 命令（可插拔位），采集最终答案文本，例如：

   ```bash
   claude -p "$(cat rendered-prompt.md)" > answers/<mode>/<task-id>.txt   # 或任意等价 agent CLI
   ```

4. 对每题答案跑评分器，汇总两模式的 per-task 分与总分；native − baseline 的差值即工具贡献。
5. 注意成本：24 题 × 2 模式 × N 次重复，须自带 API key 与预算；建议先抽样 easy 题验证管线再跑全集。

## 非目标

- 本 track 不跑真实 LLM agent（无 key/成本边界），不接 SWE-bench。
