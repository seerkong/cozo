---
knowledge_plane: global
doc_role: howto
status: active
last_verified: 2026-07-06
---

# 索引运维：全量、增量与一致性门禁

## When To Use

接入新仓库、代码变更后保鲜索引、schema 升级后重建、怀疑增量结果异常时验证一致性。流程语义真源见 [modeling: code-knowledge workflows](../../../modeling/domain/contexts/code-knowledge/workflows/indexing.md)，本文只写操作。

## Preconditions

`depa-wiki` 可执行（符号链接安装见用户手册[快速开始](../../../../cozo-lib-dotnet-llm-wiki/docs/getting-started.md)）。

## Steps

**首次/日常索引**（同一条命令，有基线自动走增量——只重扫内容哈希变化的文件）：

```bash
depa-wiki index --repo /path/to/repo
# 等价工具面调用：
depa-wiki call index_repo --work-dir /path/to/repo --repo-path /path/to/repo
```

索引落 `<repo>/.depa-wiki/depa-wiki.db`（应 gitignore）。

**强制全量重建**（schema v1→v2 迁移、或需要干净基线时）：删除 `<repo>/.depa-wiki/depa-wiki.db` 后重跑 index；遇 schema 版本报错按提示带 `--reindex`（drop+recreate，数据可重算不搬迁，语义见 [schema-evolution policy](../../../modeling/domain/contexts/code-knowledge/policies/schema-evolution.md)）。

**语义搜索需另建 embeddings**（可选）：`depa-wiki call index_embeddings --work-dir /path/to/repo`；未建时 `semantic_search` 自动降级 BM25。

## Verification

```bash
depa-wiki call detect_changes --work-dir /path/to/repo   # stale:false 即索引与 HEAD 对齐
```

## 一致性门禁

增量与全量重建结果**等价**由 llm-wiki 测试套件的一致性门禁测试保障：

```bash
cd cozo-lib-dotnet-llm-wiki/tests/Cozo.DotNet.LlmWiki.Tests
dotnet run        # 全套件，含 consistency-gate / derived-layers-fresh
```

怀疑增量后图数据异常（符号残留、关系缺失）的处置见 [troubleshooting](../troubleshooting/index.md)：先删库全量重建对比；确证不一致属产品缺陷，保留两份 DB 提 issue，不自行改库。

## Related Rules

置信度与降级语义：[edge-confidence](../../../modeling/domain/contexts/code-knowledge/policies/edge-confidence.md)、[degradation-and-budget](../../../modeling/domain/contexts/llm-wiki/policies/degradation-and-budget.md)。
