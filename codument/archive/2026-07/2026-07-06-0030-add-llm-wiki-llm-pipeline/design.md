# LLM 后端契约与增量 wiki 管线设计（G1-T3）

> 参照系：GitNexus `gitnexus/src/core/wiki/{generator,llm-client,prompts,graph-queries}.ts`（TS，四阶段 + .meta.json 增量 + 多 provider）。
> 落地系：cozo-lib-dotnet-llm-wiki（C#/.NET，capsule 包布局），替换现纯模板 `Cozo.DotNet.LlmWiki.Wiki/WikiCompiler.cs`。
> 目标 schema：分形文档（engineering / modeling），非 GitNexus 的扁平 `wiki/*.md`。
> 硬约束（mission design 1/5/6）：**图先于 LLM**——结构层确定性生成可重复可 diff；LLM 只做叙述与归组辅助；离线/无 key 必须可降级只出结构层。

---

## 1. LLM 后端契约：新 capsule 包 `Cozo.DotNet.LlmWiki.LlmClient`

### 1.1 包定位

独立 capsule，单一入口面（`ILlmClient` + `LlmClientFactory` + 配置类型），internals（各 provider HTTP 细节、SSE 解析、重试）不对外导出。`Wiki` 包只依赖契约，不依赖任何 provider 实现细节——这是"契约 + 实现分离"约束的直接体现，也让 DEPA 视角下该包是干净的 Effect capsule（唯一副作用面 = 出站 HTTP）。

```
packages/Cozo.DotNet.LlmWiki.LlmClient/
  ILlmClient.cs               # 契约（入口）
  LlmClientOptions.cs         # 请求级 options
  LlmClientConfig.cs          # provider 级配置 + 解析（env/CLI）
  LlmClientFactory.cs         # config → ILlmClient（含 UnavailableLlmClient）
  Providers/                  # internals，不导出
    OpenAiCompatibleClient.cs
    AnthropicClient.cs
  Internal/
    SseReader.cs              # 流式解析（两家 SSE 格式差异封装在各自 provider 内）
    RetryPolicy.cs            # 429/5xx 指数退避 + Retry-After
```

### 1.2 契约

```csharp
public interface ILlmClient
{
    /// 是否可用（有 key、配置合法）。false 时管线降级为纯结构层。
    bool IsAvailable { get; }
    /// 不可用原因（无 key / 显式 offline / 配置非法），供 build_wiki 结果回显。
    string? UnavailableReason { get; }

    Task<LlmCompletion> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        LlmRequestOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed record LlmRequestOptions(
    int? MaxTokens = null,            // 默认 16384（对齐 GitNexus）
    double? Temperature = 0,          // 文档生成默认 0
    TimeSpan? RequestTimeout = null,  // 默认不设硬超时（本地大模型可能 >1min，对齐 GitNexus 的 opt-in 超时）
    int MaxAttempts = 3,
    Action<int>? OnStreamProgress = null); // 已接收字符数回调 → 进度条/防 idle

public sealed record LlmCompletion(
    string Content,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    string? Model = null);
```

设计点（对照 GitNexus llm-client.ts 取舍）：

- **保留**：streaming 进度回调（`OnStreamProgress`，等价 `onChunk`，长调用期间同时用于 touch Cozo 连接防超时）；429/5xx 有界重试；base-url 校验（仅 https，http 仅允许 localhost——GitNexus 的 `validateLLMBaseUrl` 教训，防 SSRF/file scheme）；空响应报错。
- **新增**：`IsAvailable/UnavailableReason` 一等公民——GitNexus 里"无 key"是 resolve 后留空字符串、调用时才炸；我们把不可用状态提前到构造期显式化，因为**降级是我们的正常路径而非错误路径**（约束 5）。
- **不搬**：cursor/claude/codex/opencode 本地 CLI provider（GitNexus 的 IDE 生态包袱）；Azure 特判（api-key header、api-version、content-filter 解析）第一版不做，OpenAI 兼容通道天然覆盖大多数场景，Azure 走 custom base-url 时若不兼容再补。

### 1.3 Provider 实现（两个，够用）

| Provider | 覆盖 | 关键差异 |
|---|---|---|
| `OpenAiCompatibleClient` | OpenAI / OpenRouter / DeepSeek / 本地 llama.cpp / Ollama / LiteLLM——一切 `POST {base}/chat/completions` | `Authorization: Bearer`；`max_completion_tokens`；SSE `data: {choices[0].delta.content}` |
| `AnthropicClient` | Claude 系 | `x-api-key` + `anthropic-version` header；system 是顶层字段非 message；`POST {base}/v1/messages`；SSE `content_block_delta` |

reasoning-model 特判（GitNexus 的 o1/o3 检测）不做启发式，改为配置项 `COZO_WIKI_LLM_REASONING=true` 时去掉 temperature——启发式正则跟不上模型命名演化。

### 1.4 配置来源与优先级

优先级：**CLI/工具参数 > 环境变量 > 不可用（降级）**。不引入 GitNexus 的 `~/.gitnexus/config.json` 持久配置文件——cozo-wiki 的入口是 MCP 工具/CLI，配置应随调用方走；如后续需要，放 `<work-dir>/.cozo-wiki/config.json`（G2 非必需）。

```
COZO_WIKI_LLM_PROVIDER   openai-compatible | anthropic   （默认 openai-compatible）
COZO_WIKI_LLM_BASE_URL   默认 https://api.openai.com/v1（anthropic 默认 https://api.anthropic.com）
COZO_WIKI_LLM_API_KEY    未设时回退 OPENAI_API_KEY / ANTHROPIC_API_KEY（按 provider）
COZO_WIKI_LLM_MODEL      必填之一；无默认模型（避免 GitNexus 硬编码默认模型过期问题）
COZO_WIKI_LLM_MAX_TOKENS / _TIMEOUT_SECONDS / _REASONING   可选
COZO_WIKI_OFFLINE=true   显式离线开关：即使有 key 也强制降级（CI/可重复构建场景）
```

`build_wiki` 工具参数镜像同名字段（`llmProvider/llmBaseUrl/llmModel/llmApiKey`），覆盖 env。

### 1.5 不可用语义（降级契约）

`LlmClientFactory.Create(config)` 永不抛错：key 缺失/模型未配/显式 offline → 返回 `UnavailableLlmClient`（`IsAvailable=false`，`CompleteAsync` 抛 `LlmUnavailableException` 兜底防误用）。管线在每个 LLM 触点前检查 `IsAvailable`：

- 可用 → 结构层 + 叙述层；
- 不可用 → 只出结构层，页面叙述位插入占位块（`<!-- llm:pending -->` + frontmatter `narrative: pending`），build 结果带 `degraded: true, reason: ...`。占位标记同时是增量补写点：下次有 key 时只补 `narrative: pending` 的页，不动结构层。

---

## 2. 管线阶段（对照 GitNexus 四阶段改造）

GitNexus：Phase0 图收集 → Phase1 LLM 分组建模块树 → Phase2 逐模块页（LLM 全文生成）→ Phase3 LLM 总览。我们的核心改造：**Phase1 归组主导权从 LLM 移到社群检测，Phase2 页面主体从 LLM 全文改为确定性骨架 + LLM 叙述段**。

新增编排器 `Cozo.DotNet.LlmWiki.Wiki/WikiPipeline.cs`（替换 WikiCompiler 的地位；WikiCompiler 可保留为 Phase2 结构层的内部实现件）。

### Phase 0：图结构收集（纯确定性）

从 CodeKnowledge 图（对应 GitNexus graph-queries.ts 的角色）拉取：

- 文件 + 导出符号面（≈ `getFilesWithExports`）；
- CALLS 边：组内边 / 跨组出入边（≈ `getIntra/InterModuleCallEdges`）；
- 执行流（P0 mission 的 processes，≈ `getProcessesForFiles/getAllProcesses`）；
- 社群检测结果（P0 产出，GitNexus 无此资产——它才需要 LLM 分组）；
- code-map 素材：符号声明位置、类型、per-kind 边计数（`symbol_context` 已有的数据面）。

产出内存中的 `WikiGraphSnapshot`（可序列化，供测试夹具与可重复 diff）。

### Phase 1：归组（社群检测为主，LLM 辅助命名与归并评审）

**与 GitNexus"LLM 分组"的差异及理由**——GitNexus 把整份文件+导出清单喂 LLM 要 JSON 分组，为此付出了大量代价：10 万 token 预算、超限分批再合并、JSON 解析失败回退目录分组、slug 去重、防止语言指令污染 JSON key……我们已有确定性社群检测（P0 地基，GitNexus 没有的资产），所以反转主从：

1. **确定性骨架**：社群检测 → 候选分组；小社群（<3 文件）按目录邻近并入；超大组按子目录拆分（保留 GitNexus 的 token 预算拆分逻辑，但作用于确定性输入）。**离线时这一步单独成立**——分组永远可得，这是降级路径的前提。
2. **LLM 辅助（可选增强，两个小调用）**：
   - **命名**：每组给出文件+导出符号样本 → 人类可读组名（社群检测只有编号/种子符号名）。输出 JSON `{groupId: name}`，解析失败回退"目录名派生"。
   - **归并评审**：把分组摘要（组名+文件数+跨组边密度 Top-N）喂 LLM，只允许输出有界操作：`merge(a,b)` / `rename(id, name)` / `flag(id, reason)`——**不允许移动单个文件**（防 LLM 破坏确定性；文件级调整只留给人工 review）。
3. **review 稿**：分组结果写 `grouping.json`（≈ GitNexus `module_tree.json` 的 `--review` 流程），人工可改后固化；存在人工版则跳过 LLM 评审（对齐 mission 风险表"归组结果先出 review 稿"）。

理由汇总：(a) 可重复性——同一 commit 两次 build 分组一致，LLM 分组做不到；(b) 降级完整性——无 LLM 时分组不缺失；(c) token 成本从 O(全仓文件清单) 降到 O(组数)；(d) GitNexus 分批合并/校验/回退那 ~400 行复杂度整体消失。代价：社群检测边界可能不如 LLM"语义分组"贴业务直觉——用归并评审 + review 稿补偿，且这正是 mission frontier"context 划分谁说了算"的答案：**图说骨架，LLM 提建议，人拍板**。

### Phase 2：逐页生成（确定性骨架 + LLM 叙述段）

与 GitNexus 最大分歧：GitNexus 页面全文是 LLM 输出（`# name` + response.content），我们每页是**模板装配**：

```
frontmatter（受控字段：doc_role/status/last_verified/derived_from…——确定性）
# 标题（确定性，slug 稳定源）
## <结构节>   ← 图直出：sources / symbols / 调用关系表 / mermaid 边图 / 执行流步骤表
## <叙述节>   ← LLM：目的、工作机制、组件协作叙述（或 pending 占位）
```

- **页面模板按分形类目**注册：G2 先落 engineering 分形最小集——`overview/`（组级：结构=组间边+成员清单，叙述=组目的与协作）、`reference/code-map`（**纯确定性，无 LLM 触点**，从图直接导出符号表/调用边）。modeling 分形类目（contexts/objects/workflows）是 G3/G4 的新模板注册，管线不变。模板接口：`IWikiPageTemplate { string Category; PageSkeleton BuildSkeleton(snapshot, group); LlmSection[] NarrativeSlots; }`。
- **LLM 叙述调用**：每个叙述位一次 `CompleteAsync`。prompt 结构继承 GitNexus MODULE_PROMPT 的有效要素——喂源码节选 + 组内/出/入调用边 + 执行流（"reference for accuracy"模式），系统提示继承其纪律条款（禁 meta-commentary、禁编造 API、mermaid 节制）；但明确"只写叙述段，不要重复结构节已有的表格/清单"。token 预算沿用每组 30k 上限 + 超限截断。
- **叙述节落盘带标记**：`<!-- llm:begin section=purpose model=... inputsHash=... -->`，使结构层可确定性重生成而不冲掉叙述、叙述可按 inputsHash 判断是否过期（服务约束 1"可重复、可 diff"与风险表"叙述层带 last_verified 与再生成命令"）。
- **并发**：叶子页并行（信箱式有界并发 + 429 降并发，逻辑对齐 GitNexus `runParallel`，.NET 用 `SemaphoreSlim` 即可）；父级/汇总页在子页后串行。
- **单页失败不倒全局**：叙述调用失败 → 该页保留结构层 + pending 标记，记入 `failedPages`（对齐 GitNexus failedModules 语义）。

### Phase 3：索引/总览

- **索引（确定性）**：每个目录的 `index.md`（只导航，分形递归规则）+ folder-manifest 职责块（holds/excludes/tier/⬆from/⬇to——从模板类目元数据直出，G3 完整化，G2 先出 index 导航）。
- **总览叙述（LLM，可降级）**：输入=各组叙述节摘要 + 组间边聚合 + Top 执行流 + 项目信息（csproj/README 节选，对齐 GitNexus `readProjectInfo`），输出总览页叙述段；结构段（架构 mermaid 的边数据、组清单）确定性直出。GitNexus 让 LLM 画总览 mermaid，我们把边数据表确定性给出、LLM 只做文字叙述 + 可选小图，降低漂移面。

---

## 3. 增量（.meta 缓存 + 页级哈希失效判定）

> 修订（ED-1，执行期）：原稿的 git diff 增量通道（fromCommit..HEAD → 受影响文件 → groupFiles 反查）在实现中改为**纯哈希失效判定**——每页从 pre-LLM-review 图快照计算 structureHash/narrativeInputsHash，与 .meta.json 逐页比对，命中即跳过。图是真源（§3.3），哈希直接对真源取证，且免去 divergent-branch 回退与非 git 特判；fromCommit 仅保留为"图与 wiki 一致 commit"的记录字段。详见 decisions.md ED-1。以下 §3.2 原步骤 2-3 的 git diff 措辞按此修订理解。

### 3.1 `.meta.json`（对齐 GitNexus WikiMeta，扩展到页粒度）

存于输出根（见 §4）`.cozo-wiki/wiki-meta.json`：

```jsonc
{
  "fromCommit": "<sha>",
  "generatedAt": "...", "model": "...", "schemaVersion": 1,
  "grouping": { /* 固化的分组树（含人工 review 修改） */ },
  "groupFiles": { "<groupId>": ["src/.../a.cs", ...] },   // 文件→组反查
  "pages": {
    "docs/impl/core/overview/index.md": {
      "group": "core",
      "sourceFiles": [...],
      "structureHash": "<结构层输入哈希>",   // 图快照相关切片的哈希
      "narrativeInputsHash": "<叙述 prompt 输入哈希>",
      "narrative": "done | pending"
    }
  }
}
```

相对 GitNexus 的 `moduleFiles`（组→文件）多了**页→源哈希映射**：因为分形结构下一个组产出多页（overview/reference/…），且降级/补写需要页级状态。

### 3.2 增量判定流程

1. 逐页双哈希命中且文件在盘 → 跳过（但扫 `narrativePending`：LLM 现在可用则只补叙述页，不动结构）。
2. ~~git diff `fromCommit..HEAD --name-only`~~ **（ED-1 修订：不走 git diff）**——受影响页由页级哈希直接判定：Phase0 重算图快照 → 每页重算 structureHash/narrativeInputsHash → 与 .meta.json 比对，任一不匹配即进重生成集。divergent branch/非 git 目录不再是特殊分支（哈希判定与 git 状态无关）。
3. ~~changed files → `groupFiles` 反查受影响组~~（同上，由哈希直接命中受影响页）；归组每次 build 从社群检测确定性重算，人工 review 修改经 approved `.grouping-review.json` 固化（存在 approved 文件则以其为准，不被重算覆盖）。
4. 重生成页：结构层无条件重出（确定性，便宜）；叙述层仅当 `narrativeInputsHash` 变化才调 LLM（**比 GitNexus 省**：它整页重生成，我们纯格式/注释级改动若不影响 prompt 输入则零 LLM 调用）。
5. 受影响页完成后重出索引/总览（总览叙述仅当组摘要变化时重调）。

### 3.3 与索引管线的关系

wiki 增量以**图为真源**：跑 wiki 前须先 `index_repo` 到 HEAD（或 build_wiki 内部先触发 delta 索引）。`.meta.json` 的 fromCommit 记录的是"图与 wiki 一致的 commit"，避免 GitNexus 那种 wiki-meta 与图各自为政的隐性错位。

---

## 4. 落盘位置：推荐 `<work-dir>/.cozo-wiki/docs-preview/` 生成 + 显式同步

**推荐方案 B（预览区 + 同步），理由：**

1. **生成物与人工文档的写权限边界**：分形 docs/ 的愿景里目标目录终将混居人工文档（knowledge-tiers 晋升产物、owner 文档）。直写 docs/ 意味着增量重生成的"删除受影响页再重写"（GitNexus 模式）可能误伤人工内容；预览区内生成器拥有完全所有权，可以放心 rm+rewrite。
2. **review 闸门与 mission 非目标一致**：非目标明言"不做全自动文档晋升"；同一纪律适用于生成文档进 docs/——同步是一个人在环节点。落地形式：`sync_wiki` 工具/命令做 preview→docs 的三方 diff（preview 新版 / docs 现版 / meta 记录的上次同步版），无人工改动则快进覆盖，有则报冲突清单。
3. **降级与失败的中间态不污染真源**：`narrative: pending` 的半成品页、failedPages 留在 preview 区无害；直写 docs/ 会让真源长期带占位噪声。
4. **`.cozo-wiki/` 已是工作目录约定**（meta、config 同居），preview 放同处自然，且整目录可 gitignore——由用户决定生成文档是否入库、何时入库。

代价与缓解：多一步同步、docs/ 内相对链接需按最终位置生成——生成时即按 docs/ 布局组织 preview 内部路径（`docs-preview/impl/<plane>/...` 镜像 `docs/impl/<plane>/...`），链接全部相对，同步即整树拷贝，无需改写。

留一个逃生门：`build_wiki --output <dir>` 保持现有参数语义，显式给目标时直写该目录（CI 出静态站等场景），默认走 preview。

---

## 5. G2 track 切片建议（LLM 管线基建）

### 该含（G2 完成即"LLM 驱动管线替换纯模板 WikiCompiler"可演示）

1. **`Cozo.DotNet.LlmWiki.LlmClient` 包**：ILlmClient + OpenAiCompatible + Anthropic 两 provider + 配置解析 + UnavailableLlmClient 降级语义 + SSE/重试。测试：mock HTTP 的契约测试、降级路径测试。
2. **WikiPipeline 四阶段编排**：Phase0 快照、Phase1 社群归组 + LLM 命名/归并评审 + grouping.json review 流、Phase2 模板装配（骨架+叙述位机制、llm 标记、并发、单页失败隔离）、Phase3 index 导航 + 总览。
3. **两个先导页面模板**：组级 overview（结构+叙述混合，验证叙述位机制）与 reference/code-map（纯确定性，验证零 LLM 路径）——恰好是 engineering 分形的先行件，但 G2 只承诺这两类，**不承诺 engineering 分形目录全形**。
4. **增量机制**：wiki-meta.json（页级哈希）、git diff→受影响页、narrative pending 补写、divergent 回退全量。成功判据对齐 mission："改动单文件后只重生成受影响页"。
5. **build_wiki 工具升级**：LLM 参数、degraded/failedPages 回显、默认落盘 docs-preview、`--output` 逃生门。

### 不该含（划走）

- **分形 schema 本体**：engineering 分形五类目全形 + folder-manifest 职责块 + frontmatter 受控字段校验（规范文件作测试夹具）→ **G3**；modeling 分形（contexts/objects/workflows、derived_from 单父）→ **G4**。G2 的模板接口必须为它们留好注册位，但类目内容不做。
- **sync_wiki 同步工具**（preview→docs 三方 diff）：依赖"docs/ 有人工内容"的真实场景，与 G3 分形落地同批更合理；G2 只出 preview 区。
- **多输出语言**（GitNexus `--lang`）：机制简单（系统提示附加句）但校验/meta 交互繁琐，非差异化，G2 后按需。
- **HTML viewer**：非目标明确"Markdown 真源优先"，不做。
- **DEPA 线一切**（本体、effect 标注、健康度工具）：并行线，与本管线只在"未来 DEPA 判定结果作为页面结构层数据源"处交汇，接口上由 Phase0 快照可扩展性承接即可。
- **`~/.cozo-wiki` 全局持久配置、Azure 特判、本地 CLI provider**：见 §1 取舍。

依赖提醒：Phase1 依赖 P0 社群检测、Phase2 结构层依赖 P0 调用边/执行流已在图中——G2 开工前置门是 P0 对应关系可查询；git diff 通道依赖 P1 Git capsule（现 detect_changes 已可用，风险低）。
