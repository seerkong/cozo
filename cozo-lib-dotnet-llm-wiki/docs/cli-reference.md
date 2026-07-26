# CLI 参考

本页与 `depa-wiki --help` 实际输出逐项对齐（2026-07 实测）。

## 子命令总览

```text
depa-wiki --serve [options]
depa-wiki serve [options]
depa-wiki mcp --stdio [options]
depa-wiki index --repo <path> [options]
depa-wiki scan --repo <path> [options]
depa-wiki export --format runtime-snapshot|ontology-xml|wiki|business-ontology-xml --out <empty-dir> [--ontology-id <Fqn>] [--repo <path>] [--scan-first] [options]
depa-wiki ontology derive --ontology-id <Fqn> --generation-id <id> --source-fingerprint <fingerprint> [--repo <path>] [options]
depa-wiki ontology derive-semantics --ontology-id <Fqn> --generation-id <id> --source-fingerprint <fingerprint> --repo <path> --mode deterministic|assisted [--corroboration-db <db> --corroboration-repo <path>] [options]
depa-wiki ontology purge --ontology-id <Fqn> --generation-id <id> --reason <audit-reason> [--repo <path>] [options]
depa-wiki ontology review apply --ontology-id <Fqn> --decisions <file> [--repo <path>] [options]
depa-wiki wiki --repo <path> --out <dir> [--pipeline legacy|codument-fractal] [options]
depa-wiki tools
depa-wiki call <tool-name> [--arguments-json json] [--tool-arg value] [options]
depa-wiki investigate <resource> <action> [query flags] [--json <object>|-] [storage options]
depa-wiki hook augment [--budget <chars>] [options]
depa-wiki hook staleness [options]
depa-wiki hooks install|uninstall|status [--work-dir <path>]
depa-wiki skills generate|clean|status [--work-dir <path>] [--target-dir <path>] [--max-skills <n>] [--budget <chars>]
```

### `investigate <resource> <action>`

外部业务语义分析器应通过这个只读入口获取证据，而在自己的环境中维护 prompt、模型、检索策略、agent loop 和候选审核。路径是可扩展的 API 风格命名空间，目前支持：

```text
overview get
terms find
patterns find
evidence list
evidence get
domains discover
use-cases list
state-rules find
implementations find
topology domain
ontology subjects inspect
ontology use-cases list
ontology use-cases get
```

普通 flag 是 query 参数；`--json` 是对象型 body。两者可同时使用，flag 覆盖同名 body 字段。`--json -` 从 stdin 读取一个最多 1 MiB 的多行 JSON object，适合 heredoc 和外部 agent 传递结构化输入。该入口只映射白名单 investigation 操作，不接受 SQL、prompt、任意源码路径或 mutation。

```bash
depa-wiki investigate domains discover \
  --term asset --limit 10 --db "$DB"

# JSON graph for progressive domain -> entry point -> subject -> claim -> evidence navigation.
# It is deterministically capped at 20 entry points, 160 subjects, 240 claims,
# 160 CALLS edges, and 680 total edges; truncated results set truncated=true.
depa-wiki investigate topology domain --term asset --db "$DB"

# Enumerate every indexed semantic claim by following nextCursor until it is null.
depa-wiki investigate evidence list --limit 500 --db "$DB"

depa-wiki investigate evidence get --db "$DB" --json - <<'JSON'
{
  "evidenceIds": ["claim:asset-state", "claim:asset-validation"]
}
JSON
```

现有 `call <tool-name>` 仍保留兼容性，也支持 `--json -`；新集成应优先使用 `investigate`，以免获得模型编排或写操作。

## 各子命令

### `index --repo <path>`

扫描仓库写入 CodeKnowledge 图谱。索引选项：

| 选项 | 含义 | 默认 |
|---|---|---|
| `--doc-symbol-link-mode off\|local\|global` | 文档→符号链接推断模式 | `local` |
| `--max-doc-links-per-doc <n>` | 每个 doc block 最多推断链接数 | `20` |
| `--max-inferred-relations <n>` | 每次索引最多推断关系数 | `200000` |

`local`（默认）只链接同目录/README 覆盖目录/docs 路径 token 相关的文档与符号；`off` 不生成推断 `documents` 关系；`global` 启用全局文本匹配但仍受上限约束。全局弱文本召回请改用 `semantic_search`。

### `scan --repo <path>`

显式运行 DEPA conformance scan，并把判断持久化到 `depa_*`。这是会改变数据库的操作；`export` 默认只读，不会隐式 scan。可选 `--map-path`、`--effects-path` 指向项目的 DEPA 注解输入。

```bash
depa-wiki scan --repo "$REPO" --db /tmp/project.db
```

当项目未提供可验证的 DEPA 注解时，scan 会输出并保留 `BLOCKED`，不会猜测为 PASS 或 GAP。

### `export --format <kind> --out <empty-dir>`

从既有 `ck_*` / `depa_*` 数据库生成一份派生物。输出目录必须不存在或为空，避免静默覆盖；指定 `--scan-first` 才会先刷新 `depa_*`。

| `--format` | 产物 | 用途 |
|---|---|---|
| `runtime-snapshot` | `depa-runtime-snapshot.xml` | 忠实记录当前 DEPA runtime 行和诊断 |
| `ontology-xml` | `ontology.xml` 加 `types/`、`relations/`、`judgments/` | 可验证的 XML 本体投影；不是可回写的真源 |
| `business-ontology-xml` | `ontology.xml` 加 `types/`、可选 `relations/`、`rules/`、`lifecycles/`、`mappings/`、`evidence/`、`generation/candidates.json` 和 `generation/quality-report.json` | 从独立 `onto_*` 业务本体 generation 读取的中文 XML bundle；必须给出 `--ontology-id`，不读取 DEPA judgment；可用 `--supplemental-db` 合并前端等补充库的证据和候选；质量报告单独统计 carrier 归并、概念污染、field constraints、business rules、relations、lifecycles/transitions 和跨文件/use-case evidence |
| `wiki` | `INDEX.md` 与分区页面 | 人和 AI 可浏览的 Markdown 投影 |

```bash
depa-wiki export --repo "$REPO" --db /tmp/project.db \
  --format ontology-xml --out ./cozo-ontology/v0/project/ontology-xml
```

需要同时刷新判断时：

```bash
depa-wiki export --repo "$REPO" --db /tmp/project.db --scan-first \
  --format wiki --out ./cozo-ontology/v0/project/wiki
```

XML 结构和 DEPA judgment sidecar 的约束见项目 `depa-ontology-xml` skill；Wiki 目录约束见 `code-knowledge-wiki-export` skill。

### `ontology derive`

这是写操作：从已经存在的 `ck_*` 代码观察生成或替换一个指定 ontology ID 的 `onto_*` generation。它要求调用者提供稳定的 generation ID 与 source fingerprint，且不会运行 DEPA scan 或读取 `depa_*`。完成后再使用 `export --format business-ontology-xml` 生成只读 XML bundle。

```bash
depa-wiki ontology derive --repo "$REPO" --db "$REPO/.depa-wiki/depa-wiki.db" \
  --ontology-id ItAssetManagement.Ontology --generation-id v0-20260717 --source-fingerprint ck-index-20260717
depa-wiki export --repo "$REPO" --db "$REPO/.depa-wiki/depa-wiki.db" \
  --format business-ontology-xml --ontology-id ItAssetManagement.Ontology --supplemental-db "$FRONTEND_REPO/.depa-wiki/depa-wiki.db" --out ./cozo-ontology/v0/ontology/backend
```

### `ontology derive-semantics`

这是语义候选写操作，但不是索引操作或人工确认操作。命令只读取已经存在的 CodeKnowledge v3 数据库；缺少 `ck_semantic_claim` 或 schema 版本过旧时会明确报告 `reindex required`，不会隐式重建索引、回退到字段名猜测，也不会读取 `depa_*`。

```bash
depa-wiki ontology derive-semantics \
  --ontology-id ItAssetManagement.Ontology \
  --generation-id v1-semantic-20260718 \
  --source-fingerprint ck-v3-20260718 \
  --repo "$BACKEND_REPO" \
  --db "$BACKEND_REPO/.depa-wiki/depa-wiki.db" \
  --mode deterministic \
  --corroboration-db "$FRONTEND_REPO/.depa-wiki/depa-wiki.db" \
  --corroboration-repo "$FRONTEND_REPO"
```

`--mode deterministic` 不创建或调用 LLM client。`--mode assisted` 从现有 `DEPA_WIKI_LLM_*` 配置通过 `LlmClientFactory` 显式构建 client；client 不可用时在任何 `onto_*` 写入前失败。模型只能在有界证据包中提出使用既有概念和既有证据 ID 的候选，不能提供 candidate ID、提高 evidence grade 或产生 accepted 决策。

`--experiment v2|v3` 是 assisted 的显式、版本化实验策略，必须同时给出新的空 `--experiment-out` 目录。`v2` 对稳定排序的业务 use-case slices 逐个提出候选；`v3` 使用相同 proposal 输入后再执行 critic，critic 只能对已经本地验证的 candidate ID 做 `keep`/`drop`，不能创造候选或证据。两者默认最多选择 8 个切片；可用 `--max-slices` 下调，但不能提高。输出目录会包含正常 XML、`generation/candidates.json`、质量报告和不含模型原文的 `generation/experiment.json`。

已登录本机 Codex CLI 时，可将它作为 assisted provider。`DEPA_WIKI_LLM_MODEL` 可选，未指定时固定使用 `gpt-5.6-terra`；显式设置可覆盖默认值。`DEPA_WIKI_LLM_TIMEOUT_SECONDS` 默认是 120 秒。`DEPA_WIKI_CODEX_CLI_PATH` 也可省略，此时从 `PATH` 解析 `codex`。

适配器始终忽略完整的 `~/.codex/config.toml`，避免加载个人 MCP、plugin、hook 或项目规则；因此正常 CLI 依赖自定义 `model_provider` 时，需要显式传入无密钥的 provider 路由。三项必须同时给出：`DEPA_WIKI_CODEX_CLI_MODEL_PROVIDER`、`DEPA_WIKI_CODEX_CLI_BASE_URL`、`DEPA_WIKI_CODEX_CLI_WIRE_API=responses`。认证仍由本机 Codex 登录状态提供。建议先复制数据库到一次性位置，避免把候选 generation 写入正在使用的数据库：

```bash
export DEPA_WIKI_LLM_PROVIDER=codex-cli
export DEPA_WIKI_CODEX_CLI_PATH="$(command -v codex)" # 可选
export DEPA_WIKI_LLM_MODEL="gpt-5.6-terra"            # 默认；可省略
export DEPA_WIKI_LLM_TIMEOUT_SECONDS=120              # 可选
# 仅在正常 Codex CLI 使用自定义 provider 时设置；不写入密钥。
export DEPA_WIKI_CODEX_CLI_MODEL_PROVIDER="custom"
export DEPA_WIKI_CODEX_CLI_BASE_URL="https://provider.example.test/v1"
export DEPA_WIKI_CODEX_CLI_WIRE_API="responses"

depa-wiki ontology derive-semantics \
  --ontology-id ItAssetManagement.Ontology \
  --generation-id v1-codex-assisted-20260718 \
  --source-fingerprint ck-v3-20260718 \
  --repo "$BACKEND_REPO" \
  --db "$DISPOSABLE_DB" \
  --mode assisted
```

```bash
# 在数据库副本上运行；v2 与 v3 必须使用不同 generation 和不同空输出目录。
V2_EXPERIMENT_OUT="$PWD/cozo-ontology/v2/ontology/it-asset-management"
V3_EXPERIMENT_OUT="$PWD/cozo-ontology/v3/ontology/it-asset-management"

depa-wiki ontology derive-semantics \
  --ontology-id ItAssetManagement.Ontology \
  --generation-id v2-codex-slices-20260718 \
  --source-fingerprint ck-v3-20260718 \
  --repo "$BACKEND_REPO" \
  --db "$DISPOSABLE_DB" \
  --mode assisted \
  --experiment v2 \
  --max-slices 8 \
  --experiment-out "$V2_EXPERIMENT_OUT"

depa-wiki ontology derive-semantics \
  --ontology-id ItAssetManagement.Ontology \
  --generation-id v3-codex-propose-critic-20260718 \
  --source-fingerprint ck-v3-20260718 \
  --repo "$BACKEND_REPO" \
  --db "$DISPOSABLE_DB" \
  --mode assisted \
  --experiment v3 \
  --max-slices 8 \
  --experiment-out "$V3_EXPERIMENT_OUT"
```

该 provider 以无 shell 的 `codex exec` 启动，每次调用使用私有空工作目录、`read-only` sandbox、受限环境变量和 CLI JSON 输出约束；不会把目标仓库、`.depa-wiki` 数据库或导出目录作为 CLI 工作目录或 `--add-dir` 传入。Codex 只会收到既有的有界 evidence pack。这个隔离不宣称阻止受信任本机 CLI 读取整个主机；它的目标是禁止本命令把项目目录作为输入通道。CLI 不可解析、未登录、账户、模型或网络失败都会失败，不会因该失败写入 `onto_*`。

无论 provider 返回什么，local validator 仍会校验证据和 JSON 合同，结果只会成为 `pending` candidate；必须再通过 `ontology review apply` 的显式人工 decision 才能物化或导出为已接受的业务本体。

前端或文档数据库只用于补强已经由直接源码事实形成的候选；`--corroboration-db` 与 `--corroboration-repo` 必须同时提供，补强证据不能单独创建关系、规则或生命周期。

### `ontology purge`

这是审计型删除命令，只删除指定 ontology ID 当前 active generation 的 generation-scoped `onto_*` 行。调用必须同时提供精确 `--ontology-id`、当前 active `--generation-id` 和非空 `--reason`；generation ID 不匹配时命令失败，不做模糊清理。

```bash
depa-wiki ontology purge \
  --ontology-id ItAssetManagement.Ontology \
  --generation-id v1-semantic-20260718 \
  --reason "remove invalid semantic dogfood generation" \
  --repo "$BACKEND_REPO" \
  --db "$BACKEND_REPO/.depa-wiki/depa-wiki.db"
```

purge 会保留 `onto_review` / `onto_review_expectation` 审核历史，也不会读写 CodeKnowledge `ck_*` 或 DEPA `depa_*` 关系。成功后会写入 `onto_generation_tombstone`，同一个 ontology-id + generation-id 不能再次用于 `derive` 或 `derive-semantics`。

### `ontology review apply`

该命令是唯一的 CLI promotion 入口。它严格解析 decision file，追加 `onto_review` 历史，然后立即按当前 effective decision 物化仍有效的 accepted candidate。输出是机器可读 JSON summary，包含新增/重放 review 数，以及本次物化的 relation、rule、lifecycle、state、transition 和 stale review 数。

```bash
depa-wiki ontology review apply \
  --ontology-id ItAssetManagement.Ontology \
  --decisions ./ontology-review.json \
  --repo "$BACKEND_REPO" \
  --db "$BACKEND_REPO/.depa-wiki/depa-wiki.db"
```

decision file 使用严格 JSON，不允许未知或重复字段：

```json
{
  "ontologyId": "ItAssetManagement.Ontology",
  "decisions": [
    {
      "candidateId": "candidate:semantic:<hash>",
      "decision": "accepted",
      "reviewer": "user:<stable-id>",
      "rationale": "确认该关系表达真实业务约束。",
      "expectedEvidenceIds": ["evidence:<hash>"]
    }
  ]
}
```

`decision` 只能是 `accepted`、`rejected` 或 `superseded`。`expectedEvidenceIds` 必须与当前 candidate 完全一致；candidate、canonical payload、generation 或直接证据变化会使旧 review 失效，并生成 `stale_review` 诊断，不能物化。

信任边界如下：

| 层 | 能做什么 | 不能做什么 |
|---|---|---|
| `ck_semantic_claim` | 保存可复现的源码/框架直接观察 | 不能声明业务事实 |
| deterministic/assisted projector | 生成 `pending` relation/rule/lifecycle candidate | 不能确认或导出业务声明 |
| 人工 decision file | 显式接受、拒绝或取代稳定 candidate | 不能绕过 evidence snapshot 校验 |
| materializer/exporter | 仅把仍有效的 `accepted` candidate 写入本体模块 | `pending`、`rejected`、`superseded`、`stale` 只保留在审计数据 |

因此 `generation/candidates.json` 中出现 candidate 不表示本体已经接受它；只有 materialized `accepted` 记录才会进入 `relations/generated.xml`、`rules/generated.xml` 或 `lifecycles/generated.xml`。

### `wiki --repo <path> --out <dir> [--pipeline legacy|codument-fractal]`

从已索引图谱生成 Markdown 文档树到 `--out`。Wiki 选项：

| 选项 | 含义 | 默认 |
|---|---|---|
| `--pipeline legacy\|codument-fractal` | `legacy` 模板编译器；`codument-fractal` 四阶段社区主导双分形管线 | `legacy` |
| `--use-llm true\|false` | 仅 codument-fractal：使用已配置的 LLM backend，不可用时降级纯结构层 | `true` |
| `--force` | 仅 codument-fractal：忽略页级增量缓存全量重建 | 关 |

未知 `--pipeline` 取值直接报错并列出合法取值，不静默降级。分形管线细节见 [Skills 与 Wiki 生成](skills-and-wiki.md)。

### `mcp --stdio`

以 MCP stdio server 运行，给 coding agent 用。stdout 只输出 JSON-RPC 响应，诊断写 stderr。接入配置见 [MCP 工具参考](mcp-tools.md#mcp-接入配置)。

```bash
depa-wiki mcp --stdio --work-dir /path/to/my/project
```

### `tools` / `call <tool-name>`

`tools` 列出全部 MCP 工具及 JSON schema；`call` 在命令行直接调用同一套工具实现。参数两种传法：

```bash
# kebab-case 选项自动转 camelCase（--repo-path → repoPath）
depa-wiki call semantic_search --work-dir "$REPO" --query "SampleService" --limit 5

# 或直接传 JSON
depa-wiki call semantic_search --work-dir "$REPO" --arguments-json '{"query":"SampleService","limit":5}'
```

### `call publish_business_semantic_synthesis`

v3 discovers bounded business domains, synthesizes evidence-bound pending candidates, routes them through an independent critic, and emits a review-only artifact bundle. The source database is read-only for this operation; run it against a copied database rather than a live project store. The tool accepts only optional literal `domainTerms` (one to six); it accepts no output path, ontology ID, SQL, prompt, provider configuration, evidence IDs, or review decision.

`DEPA_WIKI_SEMANTIC_ARTIFACT_ROOT` is mandatory and server-owned. `DEPA_WIKI_SEMANTIC_BASELINE_ONTOLOGY_ID` optionally selects an existing deterministic code-symbol projection in the same copied database. If that baseline is absent or unreadable, publication still stays pending but `quality-report.json` is `semantic_quality_failed`; it is never reported as a semantic success.

```bash
export DEPA_WIKI_SEMANTIC_ARTIFACT_ROOT="$ARTIFACT_ROOT"
export DEPA_WIKI_SEMANTIC_BASELINE_ONTOLOGY_ID="ItAssetManagement.Ontology"
depa-wiki call publish_business_semantic_synthesis \
  --db "$COPIED_DB" --work-dir "$PROJECT" \
  --arguments-json '{"domainTerms":["asset","acceptance"]}'
```

Publication atomically creates a digest-derived run directory with `domain-charters.json`, `semantic-candidate.xml`, `review-packet.json`, `quality-report.json`, and `provenance.json`. `keep` remains a pending review candidate; no `onto_*` mutation or promotion occurs.

全部工具见 [MCP 工具参考](mcp-tools.md)。

### `hook augment` / `hook staleness`

Claude Code hook 的进程入口（读 stdin JSON、写 stdout）。通常不手工调用，由 `hooks install` 写入 `.claude/settings.json` 后被 Claude Code 触发。`augment` 支持 `--budget <chars>`（注入上下文字符预算，默认 2000）。详见 [Hooks 接入](hooks.md)。

### `hooks install|uninstall|status [--work-dir <path>]`

管理 `<work-dir>/.claude/settings.json` 中的 hook 条目，merge-safe（只增删自己的条目，不动其他配置）。详见 [Hooks 接入](hooks.md)。

### `skills generate|clean|status`

从代码图谱生成 Claude Code skills 到 `<work-dir>/.claude/skills`，只写/删 `depa-wiki-*` 前缀目录。选项：`--target-dir`、`--max-skills <n>`、`--budget <chars>`。详见 [Skills 与 Wiki 生成](skills-and-wiki.md)。

### `--serve` / `serve`

本地 HTTP server 模式，服务前端可视化工作台与调试 API：

| 选项 | 含义 | 默认 |
|---|---|---|
| `--host <host>` | 监听地址 | `127.0.0.1` |
| `--port <port>` | 监听端口 | `4176` |
| `--static-dir <path>` | 服务 cozo-lib-dotnet-llm-wiki-viz 构建产物 | — |
| `--api-only true` | 即使有静态资源也只开 API | — |

主要 API：`GET /health`、`GET /api/status`、`GET /api/tools`、`POST /api/tools/call`、`POST /api/index`、`POST /api/embeddings/index`、`POST /api/search/semantic`、`POST /api/graph/overview`、`POST /api/symbol/context`、`POST /api/symbol/impact`、`POST /api/wiki/build`、`POST /api/query/named`。不暴露 raw CozoScript endpoint，高阶查询用 `query_named`。

## 全局存储选项

所有子命令通用：

| 选项 | 含义 | 默认 |
|---|---|---|
| `--engine sqlite\|mem` | CozoDB engine | `sqlite` |
| `--global-dir <path>` | 全局数据/配置基目录 | `~/` |
| `--work-dir <path>` | 项目/工作区目录 | `index`/`wiki` 取 `--repo`；`mcp` 取当前目录 |
| `--data-folder-name <name>` | 数据目录名 | `.depa-wiki` |
| `--db <path>` | 显式 sqlite DB 路径（覆盖默认） | `<work-dir>/.depa-wiki/depa-wiki.db` |

在真实仓库上做 CLI 调用时，建议始终显式传 `--work-dir`，保证所有调用复用同一个 repo DB。
