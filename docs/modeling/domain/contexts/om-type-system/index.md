---
knowledge_plane: domain
doc_role: guide
status: active
context: om-type-system
last_verified: 2026-07-01
---

# OM Type System Context

## 目录职责

- **holds**：OM 类型系统的领域语义边界，包括 type hierarchy、mixin、attribute、relation、alias、validity 与 polymorphic query。
- **excludes**：具体 C#/Bun 实现维护步骤放在 `docs/impl/global/overview/om-type-system-maintenance.md`；一次性 parity audit 证据放在 `codument/tracks/audit-dotnet-om-type-system-parity/`。
- **tier**：`stable`
- **promotes_from**：类型系统相关 track 中已经由源码和测试验证的稳定语义
- **promotes_to**：Bun/.NET OM 实现、测试用例和行为登记

## Boundary

本 context 拥有 OM 类型系统作为用户可见 schema 语言的语义：用户如何定义类型、复用属性、演化名称、判断实体类型，以及按类型集合查询数据。

## Not Owned Here

- Higher ontology, reasoning, action, permission, and existential rule behavior are adjacent OM layers, not this context's canonical subject.
- Cozo storage internals are referenced only when they affect the public type-system semantics.

| Category | Responsibility |
|----------|----------------|
| [objects](objects/index.md) | 类型系统核心对象和语义 |
| [code-map.md](code-map.md) | 本 context 到源码/测试的入口 |

