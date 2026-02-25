---
knowledge_plane: domain
doc_role: guide
status: active
last_verified: 2026-07-01
---

# Domain Modeling

## 目录职责

- **holds**：项目对外领域本体的 canonical 文档，解释稳定概念、边界、规则与工作流。
- **excludes**：实现步骤和代码维护细节放在 `docs/impl/`；一次性调研材料放在 `codument/tracks/`。
- **tier**：`stable`
- **promotes_from**：已稳定的 track design、行为 delta 与源码/测试核对结果
- **promotes_to**：实现、测试、行为登记表与使用者文档

| File | Purpose |
|------|---------|
| [glossary.md](glossary.md) | domain 术语表 |
| [contexts](contexts/index.md) | 按领域 context 组织的建模真源 |

