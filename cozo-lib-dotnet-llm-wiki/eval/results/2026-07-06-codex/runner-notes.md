# codex-cli 双模式 eval 管线（cozo-wiki baseline vs native）

harness 位置：/tmp/codex-eval/{run.py,score.py}
任务集/模板（仓内只读）：`cozo-lib-dotnet-llm-wiki/eval/`

## 前置：预索引（一次性，已完成 2026-07-06）

用 Release apphost（免 dotnet run 编译开销）：

```bash
REPO_ROOT="$(git rev-parse --show-toplevel)"
BIN="$REPO_ROOT/cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.McpServer/bin/Release/net10.0/cozo-wiki"
"$BIN" index --repo "$REPO_ROOT/cozo-lib-dotnet-llm-wiki"
"$BIN" index --repo "$REPO_ROOT/cozo-lib-dotnet/src"
```

索引落各 workRoot 下 .cozo-wiki/cozo-wiki.db（已 gitignore）。若仓库源码有更新，重跑上面两条再跑 eval。

## 全量运行（24 题 × 2 模式 = 48 次 codex 调用，注意配额）

```bash
cd /tmp/codex-eval
python3 run.py --mode both --ids all        # 或 --mode baseline / native；--ids eval-001,eval-002 抽样
python3 score.py                            # 汇总 per-task 分、avg、must 要素命中率、native−baseline
```

答案：/tmp/codex-eval/answers/<mode>/<id>.txt；codex 全事件日志：/tmp/codex-eval/logs/<mode>-<id>.log
（日志中 `mcp: cozo_wiki/<tool> started/(completed)` 即工具调用痕迹，可用于审计 native 是否真用了 MCP。）

## 管线关键结论（探明记录）

1. **答案提取**：`codex exec --output-last-message <file>` 把 agent 最后一条消息干净落盘，stdout 日志不混入。
2. **MCP 临时配置形态**（不动 ~/.codex/config.toml）：
   ```
   -c 'mcp_servers.cozo_wiki.command="<cozo-wiki 绝对路径>"'
   -c 'mcp_servers.cozo_wiki.args=["mcp","--stdio","--work-dir","<workRoot 绝对路径>"]'
   -c 'mcp_servers.cozo_wiki.default_tools_approval_mode="approve"'
   ```
   - server 名用下划线 `cozo_wiki`，避免 `-c` 点路径里连字符的 TOML 引号问题。
   - **必须** `default_tools_approval_mode="approve"`：codex 0.142.5 对 MCP 工具调用发审批 elicitation，
     exec 非交互下自动 decision=Cancel → 所有调用报 "user cancelled MCP tool call"。
     `approval_policy="never"` 管不到 MCP 审批，只有这个 per-server key 有效。
3. **sandbox 选择**：两模式统一 `--sandbox read-only --skip-git-repo-check -C /tmp/codex-eval/empty`（空 cwd）。
   - baseline 约束主要靠 prompt（模板要求"仅凭先验、不访问文件"）；codex read-only sandbox 仍允许读全盘，
     无法硬禁读文件——这是与模板意图最接近的可用参数组合，空 cwd 降低顺手读仓的倾向。已知偏置，记录在案。
   - native 同样空 cwd：工具贡献主要经 MCP；但 prompt 中含 workRoot 绝对路径，模型在 MCP 失败时可能
     退回 shell grep（审计日志可分辨）。
4. 索引已预建，MCP server 冷启动 <1s，无需调 startup_timeout_sec。

## 评分口径（score.py = EvalScorer 复刻）

score = 0.8×must命中率 + 0.2×should命中率；无 should 时 0.2 份额跟随 must；
命中 = 大小写不敏感子串 Contains；汇总门禁指标 = Σmust命中/Σmust ≥ 90%。
