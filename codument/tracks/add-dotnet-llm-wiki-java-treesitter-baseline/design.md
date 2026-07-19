# Design：Tree-sitter Java baseline

## 上下文

Java 必须通过现有 backend/extractor contract 进入统一 CodeKnowledge 图，不能形成旁路 schema 或独立索引器。

## 方案概览

1. Native grammar
   - pin tree-sitter-java v0.23.5 commit;
   - build `libtree-sitter-java` with existing runtime and package under RID native assets;
   - add P/Invoke entry point and ABI validation.
2. Java extractor
   - one query captures declarations, modifiers, package/import, extends/implements, invocations, object creation and member access;
   - normalize into existing `ParsedSymbol`, `ParsedEdge`, `ParsedCallSite`.
3. Indexer integration
   - add `.java` to defaults and `java` language detection;
   - retain native -> CLI -> regex selection and file-local fallback.

## 影响范围与修改点（Impact）

- `Cozo.DotNet.LlmWiki.SemanticParsing`
- `Cozo.DotNet.LlmWiki.Indexing`
- native parser build script/assets
- `Cozo.DotNet.LlmWiki.Tests`

## 风险 / 权衡

- Java grammar node names differ from C#/TS: fixed fixtures and query compilation tests protect the contract.
- Initial package remains osx-arm64: other RIDs stay explicit follow-up interfaces.
- Java regex fallback is intentionally shallow; native baseline is the supported quality path.

## 兼容性设计

All additions are language-gated. Existing grammar initialization, symbol identity and C#/TS/JS fixtures must remain unchanged.
