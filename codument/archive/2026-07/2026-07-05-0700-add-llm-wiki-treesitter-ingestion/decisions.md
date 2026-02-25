# Decisions

## Usage
- mission 层 D1/D2 已定；本 track 执行期决策追加至此

### 1. 【P1】EXTENDS/IMPLEMENTS 启发式区分（C#）
- 最终决策：I 前缀启发式判 IMPLEMENTS，confidence 0.7 + evidence 记 heuristic；G4 Roslyn 增强纠正
- 状态：decided（design.md §3）

### ED-1 SemanticParsing 实现细节 internal 化（2026-07-04，AttractorCheck GAP-1）
- 决定：`CSharpExtractor`/`TypeScriptExtractor`/`TreeSitterNative`/`TreeSitterQuery`（含 `TreeSitterQueryCapture`/`TreeSitterQueryMatchResult`）改为 internal；`TreeSitterNativeBackend` 类与 `TryCreate` 保持 public（selector 降级链与测试缺库注入依赖），但 `TryCompileQuery`/`ValidateAbiVersion`/`LoadedGrammars` 成员收敛为 internal。测试通过 InternalsVisibleTo(Cozo.DotNet.LlmWiki.Tests) 继续覆盖。
- 理由：消费方（Indexing/tests 之外的外部使用者）只需 ISemanticParserBackend + ParserBackendSelector + 契约 records + ParserStatus；query 基础设施与 per-language extractor 是 P2 实现细节，internal 化后 G4（Roslyn 增强/目标解析）可自由改签名不构成破坏性变更。grep 证实 Indexing 包对被收敛类型零引用，故未给 Indexing 加 InternalsVisibleTo。
- 状态：decided（本修复已落地，全量测试绿）
