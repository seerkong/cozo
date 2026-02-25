# 真实 agent 双模式 eval 报告 · codex（2026-07-06）

首次真实 agent 对比运行（eval/README.md 使用期步骤），agent = 本机已登录 codex CLI（codex-cli 0.142.5，`codex exec` 非交互）。

## 结果总览

| 模式 | avg score | must 要素命中率 | 说明 |
|---|---|---|---|
| baseline（无工具，仅先验） | **0.096** | 2/26 = **7.7%** | codex 对本仓特定问题基本诚实拒答（模板要求不编造） |
| native（18 个 cozo-wiki MCP 工具） | **0.950** | 25/26 = **96.2%** | 答案带 path:line 且经抽查与仓库实际一致 |
| **native − baseline** | **+0.854** | +88.5pp | 图工具贡献 |

- 24 题全集 × 2 模式 = 48 次 codex exec；native 唯一失分 eval-002 属题面歧义（"解析组件"被合理理解为 parser backend——agent 给出的 TreeSitter/ISemanticParserBackend 链路技术上正确，只是未提 CallResolver），非工具失败。
- baseline 侧 eval-021 一次 600s 超时（记 0 分空答案），已在 harness 加超时容错。
- 评分口径：EvalScorer（0.8×must+0.2×should，防回显——评分只认图证据）；python 复刻版见 runner。

## 运行参数（复现要点，详见 runner-notes.md）

- 答案提取：`codex exec --output-last-message <file>`。
- native MCP 临时配置（不动全局 config）：`-c mcp_servers.cozo_wiki.command/args` + **必须** `-c 'mcp_servers.cozo_wiki.default_tools_approval_mode="approve"'`（否则 exec 非交互下 MCP 审批被自动 Cancel）。
- 两模式统一 `--sandbox read-only -C <空目录>`；两 workRoot 预建索引；native prompt 附加"索引已预建，勿跑 index_repo"。

## 结论与建议

1. **图工具对 agent 回答仓库结构问题的贡献得到量化确证**（+0.854 / must 命中率 7.7%→96.2%）——mission harden-llm-wiki-engineering 留下的"eval 基准可重复运行并出对比报告"判据的真实运行侧闭环。
2. **hooks 默认开启决策**：本数据测的是主动 MCP 工具调用价值（强支持默认安装 MCP 工具）；`hook augment` 的被动注入是其子集场景，建议：MCP 工具默认接入可直接推进；augment hook 默认开启可随（低风险，静默降级已验证），或再做一次有/无 hook 的会话级 A/B 精确测量。
3. 后续可迭代：eval-002 题面消歧；baseline 超时保护已加入 harness。
