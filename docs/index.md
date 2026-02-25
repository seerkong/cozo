---
knowledge_plane: domain
doc_role: guide
status: active
last_verified: 2026-07-01
---

# Project Documentation

## 目录职责

- **holds**：项目长期 owner 文档入口，导航到建模真源与实现维护知识。
- **excludes**：迭代过程材料放在 `codument/tracks/`；行为登记放在 `codument/behaviors/`；承重决策放在 `codument/decisions/`。
- **tier**：`stable`
- **promotes_from**：稳定后的 track proposal、design、reports 与源码核对结果
- **promotes_to**：代码、测试、行为登记表与后续 track 的上下文输入

| Area | Purpose |
|------|---------|
| [modeling/domain](modeling/domain/index.md) | 对外的领域本体与语义说明 |
| [impl/global](impl/global/index.md) | 对内的实现、维护、排障与代码地图 |

