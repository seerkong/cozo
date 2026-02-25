# 变更：LLM wiki 管线基建（多后端 + 增量）

## 背景和动机 (Context And Why)

mission `add-llm-wiki-depa-fractal-wiki`（P2）G2。现 WikiCompiler 是纯模板 Markdown，无 LLM 叙述、无增量。GitNexus 的四阶段 LLM 管线（分组→逐页→总览 + git-diff 增量）已验证有效，但其"LLM 分组"是因为没有社群检测——cozo-wiki 有（P0），故反转主从：社群检测主导归组，LLM 只做命名/有界归并评审与叙述段落。本 track 落地 LLM 客户端 capsule 与管线基建（overview/code-map 两个先导页面模板），分形类目全形是 G3/G4。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 新 capsule 包 `Cozo.DotNet.LlmWiki.LlmClient`：ILlmClient.CompleteAsync(system,user,options) + IsAvailable/UnavailableReason 一等公民；两 provider：OpenAI 兼容 HTTP（base-url 可配，覆盖 OpenAI/OpenRouter/llama.cpp）与 Anthropic；配置经环境变量/CLI；单测用 mock HTTP。
- 四阶段管线（design.md §2）：Phase0 图结构收集（确定性）；Phase1 社群主导归组 + LLM 命名/有界归并评审（merge/rename/flag only，禁移动单文件；review 稿闸门）；Phase2 页面生成=确定性骨架 + `<!-- llm:begin/end -->` 叙述段（LLM 不可用 → 只出结构层）；Phase3 索引页。先导页面：overview（社群+模块间边）与 code-map（零 LLM 触点）。
- 页级增量：.meta（fromCommit + structureHash/narrativeInputsHash per page）；git diff 复用 P1 Git capsule；纯结构未变页跳过。
- 落盘：`<work-dir>/.cozo-wiki/docs-preview/`（镜像 docs/ 布局）+ `--output` 直写逃生门。
- build_wiki 工具升级（add-only 参数：useLlm/output/force），三入口自动获得。

**非目标:**
- 分形类目全形（engineering/modeling 生成器）——G3/G4。
- sync_wiki 同步工具、多语言输出、HTML viewer、DEPA 线。
- 不引入除 HTTP 外的 provider（本地 CLI/Azure 特判不做）。

## 变更内容（What Changes）

- 新包 LlmClient；Wiki 包管线重构（WikiCompiler 保留旧路径或迁移，取兼容简单者）；Tools 的 build_wiki 升级；Core add-only 配置模型。

## 影响范围（Impact）

- 受影响能力：llm-wiki-pipeline（新 capability）。
- 受影响代码：packages/{LlmClient(新),Wiki,Tools,Core}、tests。
