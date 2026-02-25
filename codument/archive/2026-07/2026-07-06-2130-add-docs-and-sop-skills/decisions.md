# Decisions

## Usage
- 用于记录需要用户确认的决策问题、选项、最终结论与理由
- 问题标题不用字母前缀；字母只用于选项
- 后续执行过程中出现的新决策，也继续追加到本文件，不新建分散的决策记录

### 1. 【P0】分形文档补齐方式
- 背景：docs/ 树仅覆盖 om-type-system，本轮新能力全部缺位；有 docs-bootstrap 全盘点、cozo-wiki 自举、增量补三种路径。
- 选项：A) docs-bootstrap 全盘点 B) cozo-wiki 自举骨架+人工充实 C) 只补新能力增量
- 用户答复：B
- 最终决策：`cozo-wiki wiki --repo --out <staging>` 生成骨架（dogfood 顺带检验产品输出），人工按 docs-{modeling,engineering}-fractal 标准充实，与既有 om-type-system 树合并；不直接覆盖既有真源。
- 决策理由：多一道对齐成本换一次真实产品验证。
- 状态：confirmed

### 2. 【P0】终端用户文档位置与形态
- 选项：A) cozo-lib-dotnet-llm-wiki/docs/ 独立手册 B) 扩写 README
- 用户答复：A
- 最终决策：独立手册（快速开始/CLI/18 工具参考/hooks/skills/DEPA 报告解读/排障），README 瘦身为入口。
- 状态：confirmed

### 3. 【P0】SOP skill 沉淀位置
- 选项：A) codument/std 双落 B) 全局 ~/.claude/skills C) 两者都要
- 用户答复：Other → `<workspace>/.agents/skills`
- 最终决策：新 skill 落 /Users/kongweixian/infra-dev/cozodb/cozo/.agents/skills/（`<name>/SKILL.md` 格式，与既有 codument-* skill 同级）。注意该目录 gitignored——本机生效不随仓提交；skill 正文可引用 codument/std/sop/ 既有方法论（那部分随仓版本化），避免复制。
- 状态：confirmed

### 4. 【P1】P3 沉淀哪些 skill（规划者建议，light 模式不再追问）
- 需要决定：5 个候选是否全做、粒度如何。
- 最终决策：做 5 个，其中 ①orchestrator 派发范式 与 ⑤子代理翻车接手 合为一个 skill（同属"多代理派发生命周期"：派发→抽检→翻车接手），共 4 个 skill：
  - `subagent-orchestration`（派发范式 + 最小公开面硬要求 + 独立抽检 + 403 翻车接手）
  - `gap-loop-probe-design`（怀疑者提示词 + 隐藏 gap 探针 + 诚实性核查清单）
  - `codex-dual-eval`（codex exec 双模式 eval SOP：MCP 配置、approval-mode 坑、oracle 评分、回显得分漏洞防御）
  - `depa-rectification-loop`（工具扫描 + depa-expert 人工裁定 + 误报走标注侧/真 GAP 走代码侧 + 复扫收口）
- 决策理由：①⑤天然是同一生命周期的两段；四个 skill 各自语义单一。
- 状态：assumed（light 模式默认，用户可在评审 track 时改）
