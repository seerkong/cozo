# Decisions

## Usage
- mission G1 定稿已含关键决策（落盘 docs-preview、双 provider、反转主从）；执行期决策追加至此

## 执行期决策

### ED-1 增量失效判定：git diff 通道改为纯哈希判定
design §3.2 原稿的增量判定走 git diff（fromCommit..HEAD --name-only → groupFiles 反查受影响组）。实现（T2.2）改为**纯哈希失效判定**：每页两枚 SHA-256（structureHash = 页骨架确定性输入的 JSON 序列化；narrativeInputsHash = 叙述 prompt 输入），从 pre-LLM-review 图快照直接计算，逐页与 .meta.json 比对。理由：(a) 图快照本身就是真源（§3.3 "wiki 增量以图为真源"），哈希直接对真源取证，比 git diff→文件→组→页的多级反查更精确——纯格式/注释级改动 git diff 会误报、哈希不会；(b) 免去 divergent-branch/merge-base 不可达的回退分支与非 git 目录特判（fromCommit 仅作记录字段保留，null 安全）；(c) 判定纯函数化，测试可用内存图钉死，无需 git fixture。代价：每次 build 都要重算全量快照与哈希——当前页面清单是常数级，成本可忽略；若未来页数随组数增长到显著规模，可再引入 git diff 作预筛（哈希仍为最终裁决）。

### ED-2 COZO_WIKI_OFFLINE / SSE 流式 / 重试推迟
design §1.4 的 `COZO_WIKI_OFFLINE`、§1.2 的 OnStreamProgress/SSE、§1 的 429/5xx 重试在本 track 未实现，显式推迟。理由：当前 LLM 触点只有两个批量小调用（归组评审 + overview 叙述），无长流式输出，进度回调与重试的收益面尚未出现；离线强制降级已由"不配 key/model 即 NullLlmClient"覆盖（工具层 useLlm=false 亦可）。接入点已留好：ILlmClient 契约不变，SSE/重试是 provider internals，OFFLINE 是 Factory 一行判定——叙述并发化（多页叙述）时一并补。

### ED-3 Core→Wiki 生产 IVT 权衡
fromCommit 需要 HEAD 读取，复用 P1 Git capsule（Core internal `GitCliDiffProvider`）。取舍：给 Core.csproj 加一行 `InternalsVisibleTo("Cozo.DotNet.LlmWiki.Wiki")`（生产程序集互见）而非把 TryGetHeadCommit 提升为 Core 公共 API 或在 Wiki 里复制 git 调用。理由：最小公开面约束优先——公共 API 是永久契约，IVT 是同仓两 capsule 间的内部走廊，可随时收回；复制实现则违反单一 git 通道纪律。风险（internal 依赖脆性）接受：两包同仓同 slnx 同步演进。

### ED-4 review 闸门与 narrative pending 补写的实现口径（AttractorCheck GAP 修复）
- **review 稿**：落盘 `<output>/.grouping-review.json`（非 design §3.1 的 grouping 内嵌 .meta.json——review 稿是人工编辑面，与机器缓存分离）。内容 = 最终分组（id/name/members/sourceCommunities）+ LLM 原始建议动作 + `approved: false`。人工编辑置 `approved: true` 后该文件成为归组权威：替换社群分组、跳过 LLM 评审、build 永不覆写。**approved 文件在页哈希计算之前生效**（它是确定性盘上输入，编辑即失效组页），而 LLM 评审保持在哈希之后（ED-1/T2.2 既有不变式：评审不进缓存键，否则全跳过 build 无法零 LLM）。草稿仅在 Phase1 实际重算（组页重建）或文件缺失时重写，避免全跳过 build 抹掉建议动作。
- **RequireApprovedGrouping**（FractalWikiOptions add-only，默认 false）：true 且无 approved 文件 → 全程 llm=null（只出结构层）+ 诊断 + 仍写 review 草稿供人工批准。默认 false 保证 CLI/CI 不被人工环节阻塞。
- **narrative pending 补写**：.meta.json 页条目加 `narrativePending`（bool，缺省 false，旧 meta 兼容，schemaVersion 仍 1）。重建页带叙述位且叙述仍为 pending 占位 → true；跳过页继承旧值。增量判定追加一条：双哈希命中但 `narrativePending=true` 且本次 LLM 可用 → 仍重建该页（仅为补叙述）；LLM 不可用则不空转。补写成功（叙述非 pending）置 false。降级语义沿用 §1.5 的"占位标记即补写点"，落点从 frontmatter 改为 meta 字段（页面无 frontmatter 层，标记段内占位文本 `_pending: llm unavailable_` 不变）。
