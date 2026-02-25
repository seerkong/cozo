---
knowledge_plane: domain
doc_role: canonical
status: active
context: code-knowledge
last_verified: 2026-07-06
---

# Schema 演化：版本纪律与 rebuild 式迁移

## Rule

`ck_*` schema 以 `ck_meta.schema_version` 为版本锚（当前 **v2**）。破坏性变更走 **rebuild 式迁移**：不搬数据，drop 全部 `ck_*` 关系后重建 schema 并重索引；同版本内只允许 **add-only** 扩展。

## Rationale

索引数据是可重算投影（事实源是仓库本身），搬迁旧行没有价值且引入一致性风险；重建成本 = 一次重索引，可接受。

## 语义细节

- **旧版检测**：发现 v1 痕迹（有 `ck_relation` 无 `ck_meta.schema_version=2`，或版本号偏低）且未带 reindex 时，初始化**报错**并指引调用方显式带 `reindex: true`（或索引工具 `--reindex`）——不静默升级，让"数据将被重建"成为显式决定。
- **add-only 扩展**：同版本新增关系（如 `ck_external_call`）必须带默认值、不改既有关系语义、不动 schema_version；partial writer（子集 `:put`）保持合法。
- **建表幂等**：重复初始化时建表冲突被吞掉，schema init 可安全重入。

## Compatibility

- `symbol_id` 不跨重建稳定；跨重建锚定用 `sym_key`（DEPA 标注再锚定即依赖此约定）。
- v1 遗留 `Relations` 输入自动转换为 `ck_edge` 行（confidence=0.3、resolver=regex、kind 归一）——旧调用方不破坏，但产出的是显式的最低档证据。

## Enforcement Points

`CodeKnowledgeSchema.InitAsync`（检测/报错/drop+recreate/版本写回），见 [code-map.md](../code-map.md)；机检为 llm-wiki 测试套件中的 schema/增量一致性测试。
