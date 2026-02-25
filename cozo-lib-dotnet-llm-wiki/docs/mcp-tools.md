# MCP 工具参考

`depa-wiki tools` 列出的全部 18 个工具（2026-07 实测，本页与其一一对应，无遗漏）。每个工具都可以两种方式调用：

- **MCP**：agent 通过 `depa-wiki mcp --stdio` 以 camelCase 参数调用（如 `repoPath`）；
- **CLI**：`depa-wiki call <tool-name> --kebab-case value ...`（`--repo-path` 自动转 `repoPath`），或 `--arguments-json '{...}'`。

以下示例用 CLI 形式，输出均为真实运行结果（截断，长 id 中的 repo 哈希缩写为 `743da8…`）。示例中 `REPO=/path/to/repo`，且该仓库已完成索引。

## MCP 接入配置

### Claude Code

项目级 `.mcp.json`（或 `claude mcp add`）：

```json
{
  "mcpServers": {
    "depa_wiki": {
      "command": "depa-wiki",
      "args": ["mcp", "--stdio", "--work-dir", "/absolute/path/to/repo"]
    }
  }
}
```

等价命令行：

```bash
claude mcp add depa_wiki -- depa-wiki mcp --stdio --work-dir /absolute/path/to/repo
```

### codex

非交互 `codex exec` 用 `-c` 临时配置（不动全局 config）：

```bash
codex exec --sandbox read-only \
  -c 'mcp_servers.depa_wiki.command="/absolute/path/to/depa-wiki"' \
  -c 'mcp_servers.depa_wiki.args=["mcp","--stdio","--work-dir","/absolute/path/to/repo"]' \
  -c 'mcp_servers.depa_wiki.default_tools_approval_mode="approve"' \
  "你的问题"
```

**必须**配 `default_tools_approval_mode="approve"`：这是实测过的坑——非交互模式下不配它，MCP 工具审批会被自动 Cancel，所有工具调用静默失败。

MCP stdio 模式下 stdout 只输出 JSON-RPC 响应，诊断信息全部走 stderr。

---

## 索引与构建

### index_repo

把本地仓库索引进 CodeKnowledge 图谱（`index` 子命令的工具形态；增量：已索引过的仓库按 ck_file 内容哈希对账只重扫变更文件，不依赖 git）。

- 必填：`repoPath`
- 可选：`docSymbolLinkMode`（`off|local|global`，默认 `local`）、`maxDocLinksPerDoc`（默认 20）、`maxInferredRelations`（默认 200000）

```bash
depa-wiki call index_repo --work-dir "$REPO" --repo-path "$REPO"
```

### index_embeddings

把 CodeKnowledge 文本写入 Cozo 向量 relation，为 `semantic_search` 的 vector 通道供数。默认使用随包分发的 all-MiniLM-L6-v2 ONNX 模型（`<F32; 384>`）；模型资产缺失时回退 deterministic provider。

- 可选：`includeSymbols`、`includeDocs`（bool）、`limit`（最大 source 数）

```bash
depa-wiki call index_embeddings --work-dir "$REPO" --include-symbols true --include-docs true --limit 1000
```

### build_wiki

从已索引图谱生成 Markdown wiki。

- 可选：`outputDirectory`；`pipeline`（`legacy` 模板编译器 | `codument-fractal` 四阶段社区主导管线，页级增量，写 `.depa-wiki/docs-preview`，默认 `legacy`）；`writeFiles`（仅 legacy）；`useLlm`（仅 codument-fractal，默认 true，无 LLM backend 时降级纯结构层）；`force`（仅 codument-fractal，忽略页级增量缓存全量重建）；`workDirectory`（仅 codument-fractal，默认预览根目录与 migration-ledger commit 的推导基准，默认当前目录）

```bash
depa-wiki call build_wiki --work-dir "$REPO" --pipeline codument-fractal --use-llm false
```

详见 [Skills 与 Wiki 生成](skills-and-wiki.md)。

## 检索与图查询

### semantic_search

混合检索：BM25 全文 + 向量相似度做 reciprocal rank fusion。查询文本按字面匹配（不解释 FTS 运算符）；某一通道不可用时安全降级（如未建 embedding 索引时只走 text 通道）。

- 必填：`query`
- 可选：`limit`、`sourceKinds`（`code,docs,symbol,doc`）、`mode`（`hybrid|vector|text`，默认 `hybrid`）、`rrfK`（默认 60）

```bash
depa-wiki call semantic_search --work-dir "$REPO" --query "hook augment" --limit 3
```

```json
{"query":"hook augment","hits":[{"itemId":"","sourceKind":"doc",
  "sourceId":"doc:file:repo:743da8…:codument/archive/2026-07/…/proposal.md:7:要做-和-不做-goals-non-goals",
  "text":"## \"要做\"和\"不做\" (Goals / Non-Goals)…",
  "rrfScore":0.0163,"channels":["text"]}, …]}
```

### overview_graph

加载有界的仓库概览图（repo/file/symbol/doc 节点 + 边）。

- 可选：`categories`（`code` 和/或 `docs`）、`maxNodes`（默认 300）、`maxEdges`（默认 600）

```bash
depa-wiki call overview_graph --work-dir "$REPO" --categories code --max-nodes 5 --max-edges 5
```

```json
{"nodes":[{"id":"repo:743da8…","label":"cozo","group":"repo",…},
          {"id":"file:repo:743da8…:AGENTS.md","label":"AGENTS.md","group":"file",…}, …]}
```

### symbol_context

按 symbol id 取符号上下文：声明、直接关系、关联文档，并富化其参与的执行流（processes）、community 归属与分 kind 边计数。

- 必填：`symbolId`（`symbol:` 前缀 id，可从 `semantic_search` 的 `sourceId` 拿到）

```bash
depa-wiki call symbol_context --work-dir "$REPO" \
  --symbol-id 'symbol:file:repo:743da8…:cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Wiki/SkillsGenerator.cs:44:SkillsGenerator'
```

```json
{"symbol":{"name":"SkillsGenerator","kind":"class",
  "path":"cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.Wiki/SkillsGenerator.cs",
  "startLine":44,"endLine":455,"signature":"internal sealed class SkillsGenerator"},
 "incoming":[{"fromId":"symbol:…:SkillsGeneratorTests.cs:149:RunAsync","kind":"CALLS",
   "evidence":"…SkillsGeneratorTests.cs: new","confidence":0.9}, …]}
```

### impact_of_change

从一个 symbol 做传递性影响分析：沿 CALLS 边分层 BFS（每层带 symbols/total/truncated）、LOW/MEDIUM/HIGH/CRITICAL 风险评级、受影响执行流，兼容旧的扁平 edges/impactedIds 字段。

- 必填：`symbolId`
- 可选：`direction`（`up`=谁传递依赖我/爆炸半径，`down`=我传递依赖谁，默认 `up`）、`maxDepth`（1..16，默认 3）、`minConfidence`（默认 0.0）

```bash
depa-wiki call impact_of_change --work-dir "$REPO" --symbol-id 'symbol:…:SkillsGenerator.cs:44:SkillsGenerator' \
  --direction up --max-depth 2
```

```json
{"rootId":"symbol:…:SkillsGenerator.cs:44:SkillsGenerator",
 "edges":[{"fromId":"symbol:…:CallResolver.cs:620:Append","kind":"CALLS",
   "evidence":"…CallResolver.cs: ambiguous:2","confidence":0.5}, …]}
```

### trace

追踪两个符号间的最短调用路径（CALLS 边）。输入可以是 symbol id（`symbol:` 前缀）或精确符号名；名字歧义时返回 `found=false` 和有界候选列表（不瞎猜）。hop 携带可点击 `path:line` 与逐边 confidence。

- 必填：`from`、`to`
- 可选：`maxDepth`（默认 16，最短路更长则 `found=false`）、`minConfidence`（默认 0.7）

```bash
depa-wiki call trace --work-dir "$REPO" --from HookCommands --to Run
```

```json
{"found":false,"reason":"symbol name 'Run' (to) is ambiguous (3 matches); retry with one of the candidate ids",
 "argument":"to",
 "candidates":[{"id":"symbol:…:cozo-lib-dotnet/src/CozoDb.cs:202:Run","name":"Run","kind":"method","line":202}, …]}
```

拿候选 id 重试即可得到 hop 路径。

### check

在 IMPORTS 和/或 CALLS 图上检测依赖环（强连通分量）。环成员携带 id/name/file，确定性排序。

- 可选：`cycles`（`import|calls|both`，默认 `import`）

```bash
depa-wiki call check --work-dir "$REPO" --cycles import
```

```json
{"kind":"import","count":0,"cycles":[]}
```

### detect_changes

把 git 变更集映射到已索引符号：diff 行区间对上 ck_symbol 区间，聚合有界传递影响（callers）、受影响执行流与整体风险评级。仓库外/异常降级为诊断，从不报错。

- 可选：`workDirectory`（默认当前目录）、`scope`（`unstaged|staged|compare`，默认 `unstaged`）、`baseRef`（scope=compare 用，缺省沿 origin/HEAD→main→master 解析）、`maxImpactDepth`（默认 2）、`minConfidence`（默认 0.0）

```bash
depa-wiki call detect_changes --work-dir "$REPO" --scope unstaged
```

```json
{"scope":"unstaged","changedFiles":[],"changedSymbols":[],"impactedSymbols":[],"risk":"LOW",
 "indexedCommit":"bba2fc55…","headCommit":"76dd80c0…","stale":true,
 "staleHint":"the index was built at a different commit than the current HEAD; re-run index_repo to refresh",
 "diagnostics":[]}
```

（`stale:true` 即索引落后 HEAD，见[故障排查](troubleshooting.md#索引落后-head)。）

## 文档与关系解释

### docs_for_code

查找链接到某代码目标的文档块。

- 必填：`targetId`

```bash
depa-wiki call docs_for_code --work-dir "$REPO" --target-id 'symbol:…:SkillsGenerator.cs:44:SkillsGenerator'
```

```json
{"targetId":"symbol:…:SkillsGenerator.cs:44:SkillsGenerator","docs":[],"missing":true}
```

### explain_relation

解释两个节点间直接关系的证据（关系 kind、evidence 文本、confidence）。

- 必填：`fromId`、`toId`

```bash
depa-wiki call explain_relation --work-dir "$REPO" \
  --from-id 'symbol:…:SkillsGeneratorTests.cs:149:RunAsync' \
  --to-id   'symbol:…:SkillsGenerator.cs:44:SkillsGenerator'
```

```json
{"relations":[{"kind":"CALLS","evidence":"cozo-lib-dotnet-llm-wiki/tests/…/SkillsGeneratorTests.cs: new","confidence":0.9}]}
```

### query_named

执行注册过的 CodeKnowledge NamedQuery（高阶查询入口，替代 raw CozoScript）。

- 必填：`name`
- 可选：`parametersJson`（JSON object 字符串）

```bash
depa-wiki call query_named --work-dir "$REPO" --name code_impact --parameters-json '{"symbolId":"symbol:…"}'
```

查询名取决于当前 DB 注册表；名字不存在时返回结构化诊断而非崩溃（真实输出）：

```json
{"queryName":"code_overview","success":false,"diagnostics":[{"severity":2,"code":"OMQ404",
  "message":"NamedQuery 'code_overview' not found."}],"hasErrors":true}
```

## 解析器

### parser_status

报告 Tree-sitter 解析器可用性。CLI 或 grammar 缺失时返回结构化 diagnostic，不回退 Roslyn。

```bash
depa-wiki call parser_status --work-dir "$REPO"
```

```json
{"available":false,"parser":"tree-sitter","version":"",
 "diagnostics":[{"code":"TSCLI001","message":"tree-sitter CLI is unavailable. Install tree-sitter and grammars…","severity":"warning"}]}
```

### parse_file

用 Tree-sitter CLI 解析单个源文件，返回节点摘要。

- 必填：`filePath`
- 可选：`language`、`maxNodes`

```bash
depa-wiki call parse_file --work-dir "$REPO" --file-path "$REPO/src/Foo.cs" --max-nodes 32
```

tree-sitter 不可用时同样返回 `success:false` + `TSCLI001` 诊断（见 parser_status 示例）。

## DEPA 架构符合度

以下三个工具的详细解读见 [DEPA 报告解读](depa-report.md)。

### depa_conformance

跑 DEPA 符合度报告：红灯规则按 8 维度分组（data/effect/processor/layering/fact_source/actor/overdesign/vendor），每条规则 PASS/GAP/BLOCKED，violation 按 confidence 排序并携带 `path:line` 证据；BLOCKED 会点名缺失输入而不是假装 PASS。占位规则恒 BLOCKED 并点名缺失观测类别。

- 可选：`scanFirst`（默认 true，先跑标注同步+检测再聚合；false 只聚合已持久化 violations）、`dimension`（只报某一维）、`workDirectory`（其 depa-map.json / depa-effects.json 用作默认输入）、`mapPath`、`effectsPath`

```bash
depa-wiki call depa_conformance --work-dir "$REPO"
```

```json
{"dimensions":[…,{"dimension":"layering","rules":[…,
  {"ruleId":"V-L4","verdict":"GAP","violations":[{
    "violationId":"depa:violation:V-L4@depa:capsule:Om.Core@401c89a602bc",
    "dimension":"layering","subjectEntityId":"depa:capsule:Om.Core",
    "message":"capsule 'Om.Core' exposes 157 entries (a capsule has one stable entry): …"}]},…]},…],
 "structuralFindings":[],"scanned":true}
```

### health_score

分维度 DEPA 健康度：每维 gap/blocked 计数、规则覆盖与 display-only score。各维独立裁决，从不合并成单一总分。

- 可选：同 depa_conformance（`scanFirst`/`workDirectory`/`mapPath`/`effectsPath`）

```bash
depa-wiki call health_score --work-dir "$REPO" --scan-first false
```

```json
{"dimensions":[{"dimension":"layering","gapCount":1,"blockedCount":9,"rulesCovered":1,"rulesTotal":10,"score":0}, …],
 "note":"Display layer only: each DEPA dimension is judged independently…"}
```

### fact_grade_map

导出 DEPA 事实源分级图：所有分级 depa_fact_source 节点（grade 1-7、grade_id、expected owner、anchor path:line）与 fact_written_by / projection_derived_from 邻接。未做 depa-map 标注的仓库返回空集：

```bash
depa-wiki call fact_grade_map --work-dir "$REPO"
```

```json
{"factSources":[],"edges":[]}
```
