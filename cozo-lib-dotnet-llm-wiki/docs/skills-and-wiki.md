# Skills 与 Wiki 生成

## skills：从代码图谱生成 Claude Code skills

`depa-wiki skills` 把已索引图谱中的 context/工作流知识生成为 Claude Code skill 文件，让 agent 在会话中按需加载仓库结构知识：

```bash
depa-wiki skills generate --work-dir /path/to/repo    # 生成
depa-wiki skills status   --work-dir /path/to/repo    # 查看
depa-wiki skills clean    --work-dir /path/to/repo    # 清除
```

关键选项（见 `--help`）：

| 选项 | 含义 |
|---|---|
| `--work-dir <path>` | 目标仓库（默认写 `<work-dir>/.claude/skills`） |
| `--target-dir <path>` | 覆盖 skills 输出目录 |
| `--max-skills <n>` | 每次生成的 **context skill** 数上限（默认 24）；三个通用 workflow skill（`depa-wiki-exploring`/`-impact`/`-depa`）固定生成、不占此额度 |
| `--budget <chars>` | 每个 skill 字符预算 |

安全边界：**只写、只删 `depa-wiki-*` 前缀目录**，用户手写的其他 skills 不会被触碰。生成是确定性、有界的（同一图谱重复生成结果一致）。

`status` 真实输出示例（未生成时）：

```text
Skills directory: /path/to/repo/.claude/skills
  No depa-wiki-* skills generated.
```

前置条件：目标仓库已完成 `depa-wiki index`。

## wiki：双分形文档生成

`depa-wiki wiki` 从图谱生成 Markdown 文档树：

```bash
depa-wiki wiki --repo /path/to/repo --out /path/to/output --pipeline codument-fractal
```

- `--repo`：已索引的仓库（`--work-dir` 缺省取它）；
- `--out`：输出目录。**建议指向 staging 目录**，人工审阅后再合并进正式文档树——生成器不了解你手工维护的真源文档，直接指向已有文档目录有覆盖风险。
- `--pipeline`：`legacy`（默认）走模板编译器；`codument-fractal` 走四阶段社区主导管线，生成双分形结构：modeling 平面（plane → context → 类目 → 叶子，index 只导航）与 engineering/impl 平面（页级增量，`--use-llm false` 可降级为纯结构层，`--force` 忽略增量缓存全量重建）。

同一能力也可通过 MCP 工具 `build_wiki` 调用（`pipeline=codument-fractal` 同义）。参数详情见 [MCP 工具参考 · build_wiki](mcp-tools.md#build_wiki)。

典型 dogfood 流程：

```bash
depa-wiki index --repo "$REPO"
depa-wiki wiki --repo "$REPO" --out /tmp/wiki-staging --pipeline codument-fractal
# 人工审阅 /tmp/wiki-staging，再合并需要的部分
```
