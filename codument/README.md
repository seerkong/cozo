# Codument Workspace

本目录是项目内自包含的 Codument 工作区。它保存项目吸引子、行为登记表、track、内置规程与操作提示词，使 AI 助手可以在项目本地完成 planning、实现、验证、归档和迁移。

Codument 的核心状态文件按生命周期放在 `tracks/pending/<id>/`、`tracks/active/<id>/` 和 `tracks/archived/YYYY-MM/<timestamp>-<id>/`。其中的 `track.xml` 把一次变更拆成三条正交轴：

| 轴 | 位置 | 作用 |
|---|---|---|
| 结构 | `<TaskSpace>` | 记录任务树、阶段、任务状态与验收信息 |
| 调度 | `<Schedule>` | 记录顺序 / DAG 依赖与并行约束 |
| 行为 | `<Hooks>` | 记录方向审查、gap-loop、人工确认、制品同步等生命周期动作 |

旧 `plan.xml`、`specs/`、`spec_deltas/` 等路径已迁移到当前标准：`track.xml`、`behaviors/`、`behavior_deltas/`。

## 目录职责

```text
codument/
├── std/                         内置标准、规程、操作提示词；upgrade-workspace 会刷新
│   ├── AGENTS.md                AI 入口与路由
│   ├── kernel-pointer.md        Codument 与通用 workflow/task 内核的关系
│   ├── spec/                    文件格式规范
│   ├── attractors/              可升级标准 attractor（knowledge/docs/memory/DEPA）
│   ├── actions/                 codument-* skill 的权威 action body
│   ├── commands/                复杂 CLI 子命令的使用说明
│   ├── protocols/               questioning、validation 等共享协议
│   ├── methods/                 TDD、DAG、workflow 等共享方法
│   └── compat/                  历史路径、名称和格式迁移映射
├── attractors/                  项目、产品等项目自有吸引子
├── config/                      profile 与 action hook 配置
├── workflows/                   结构化 workflow definitions / instances
├── sop/                         项目自定义执行流程
├── backlog/                     候选工作与 AI 自主度规则
├── missions/                    跨多个 track 的 mission / roadmap
├── memory/                      长期记忆：lessons / incidents / patterns / summaries
├── behaviors/                   行为登记表，归档时由 behavior_deltas 提升
└── tracks/                      运行期变更追踪：pending / active / archived
```

## 当前标准

1. `track.xml` 是 track 状态真源；任务状态在 XML 内维护，不再依赖 `state.json`。
2. 第一层 `TaskGroup` 表示 phase；其下可继续嵌套 `TaskGroup` 或 `Task`。
3. 调度按层声明：需要并行的层标 `cdt:child-mode="dag"`，依赖写入 `<Schedule><Dag>`.
4. `summary` 不手维护；统计由工具遍历 TaskSpace 派生。
5. 变更输入输出使用目录物料：`behavior_deltas/`、`docs/`、`codument/behaviors/` 等。
6. 能力开关使用 `config/attractor-profiles.xml` 的 `<Profile enabled>`；不再使用 `config/feature.json`。
7. 内置规程在 `std/methods/`，项目自定义规程在顶层 `sop/`，结构化 workflow 放在 `workflows/`。
8. 行为登记表使用 `codument/behaviors/`；行为增量使用 `behavior_deltas/`；selector 使用 `behavior://`。
9. 跨多个 track 的长期路线放在 `missions/<id>/roadmap.md`；单次变更以其生命周期目录中的 `track.xml` 为状态真源。
10. `std/actions/` 是各 `codument-*` skill 的权威提示词 body；agent skill 目录只安装薄壳入口。

## 常用入口

- `std/AGENTS.md`：给 AI 助手的总入口。
- `std/spec/track-xml-spec.md`：`track.xml` 格式。
- `std/spec/behavior-delta.md`：行为增量格式。
- `std/spec/behavior-registry.md`：行为登记表格式。
- `std/methods/workflow.md`：Codument 工作流总纲。
- `missions/README.md`：跨 track 路线图写法。
- `std/actions/README.md`：所有操作提示词索引。
