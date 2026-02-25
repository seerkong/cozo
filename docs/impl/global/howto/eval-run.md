---
knowledge_plane: global
doc_role: howto
status: active
last_verified: 2026-07-06
---

# eval 跑法：oracle 门禁与 codex 双模式

## When To Use

改动工具面/索引后验证"任务集仍可被图回答"（oracle 门禁）；或要量化图谱工具对真实 agent 的贡献（baseline vs native 双模式）。任务集与评分口径真源：`cozo-lib-dotnet-llm-wiki/eval/README.md`（24 题、score = 0.8×must + 0.2×should、门禁 = Σmust 命中 ≥ 90%）。

## oracle 门禁（不经 LLM，CI 可跑）

```bash
cd cozo-lib-dotnet-llm-wiki/tests/Cozo.DotNet.LlmWiki.Tests
EVAL_ORACLE_ONLY=1 dotnet run     # 只跑 oracle 门禁，打印逐题 must/should 明细
EVAL_ORACLE_DEBUG=1               # 追加：未全中的题把完整答案落系统临时目录
```

oracle 按每题建议工具序列直答并评分；不跑 `index_embeddings`（semantic_search 降级 BM25）；评分前剥离输入回显字段，命中只能来自真实图证据。

## codex 双模式（真实 agent，注意配额：24 题 × 2 模式 = 48 次调用）

1. **预索引**（用 Release apphost，免编译开销）：

   ```bash
   BIN=<仓根>/cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/bin/Release/net10.0/depa-wiki
   "$BIN" index --repo <仓根>/cozo-lib-dotnet-llm-wiki
   "$BIN" index --repo <仓根>/cozo-lib-dotnet/src
   ```

2. **codex MCP 临时配置**（不动 `~/.codex/config.toml`）：

   ```text
   -c 'mcp_servers.depa_wiki.command="<depa-wiki 绝对路径>"'
   -c 'mcp_servers.depa_wiki.args=["mcp","--stdio","--work-dir","<workRoot 绝对路径>"]'
   -c 'mcp_servers.depa_wiki.default_tools_approval_mode="approve"'
   ```

   **approval-mode 坑（必踩项）**：codex 非交互 `exec` 对 MCP 工具审批自动 Cancel，所有调用报 "user cancelled MCP tool call"；`approval_policy="never"` 管不到 MCP 审批，**只有 per-server 的 `default_tools_approval_mode="approve"` 有效**。server 名用下划线 `depa_wiki`，避免 `-c` 点路径里连字符的 TOML 引号问题。

3. **运行与提取**：`codex exec --output-last-message <file>` 干净落盘最终答案；两模式统一 `--sandbox read-only --skip-git-repo-check -C <空目录>`。已知偏置：read-only sandbox 无法硬禁 baseline 读文件，空 cwd 只降低倾向；native 在 MCP 失败时可能退回 shell grep——用事件日志中 `mcp: depa_wiki/<tool> started` 审计。
4. **评分**：逐题渲染 `eval/prompts/{baseline,native}.md` 模板、采集答案、按 EvalScorer 口径评分，native − baseline 即工具贡献。

## Verification

oracle 门禁 must 命中率 ≥ 90%；双模式参照 2026-07-06 codex 基准（baseline 0.096 / native 0.950）判断回归。历史结果与 harness 细节见 `cozo-lib-dotnet-llm-wiki/eval/results/2026-07-06-codex/runner-notes.md`。
