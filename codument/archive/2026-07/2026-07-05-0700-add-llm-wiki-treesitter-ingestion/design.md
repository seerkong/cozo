# 方案设计：tree-sitter 原生解析 ingestion

## 上下文

- spike 证据（mission analysis/spike-treesitter-pinvoke.md，产物 /tmp/spike-treesitter-pinvoke）：libtree-sitter v0.27.0 + tree-sitter-c-sharp（ABI 15）+ tree-sitter-typescript（ABI 14）用 cc 一条命令各编出 dylib；~20 个 P/Invoke 声明覆盖解析+query；`ts_language_abi_version` 启动断言防 ABI 漂移；`ts_node_string` 返回指针需 free；TSNode 按值 marshal。
- v2 图模型（G2 产物）：CodeSymbolFact（含 ParentId/Lang/Visibility/Exported/SymKey/DocId/Resolver）、CodeEdgeFact + CodeEdgeKinds。

## 方案概览

1. **解析后端契约**（SemanticParsing 包）
   - `ISemanticParserBackend`：能力查询（SupportsLanguage）+ `ParseFileAsync(path, source) → ParsedFileResult`（符号树 + 结构边 + 诊断）。
   - `ParsedSymbol`：name/kind/start_line/end_line/signature/parent（树形）/visibility/exported；`ParsedEdge`：from/to/kind（结构边）。
   - 现有 TreeSitterCliParser 实现同一契约（降级后端），新 `TreeSitterNativeBackend` 为主后端；后端选择器按可用性排序：native → cli → none。
2. **原生绑定**
   - `TreeSitterNative`（DllImport libtree-sitter + per-grammar entry：tree_sitter_c_sharp/tree_sitter_typescript）；SafeHandle 包 parser/tree/query/cursor；启动断言 grammar ABI ∈ [ts_language_abi_version 支持区间]。
   - dylib 查找：NativeLibrary.SetDllImportResolver，按 runtimes/<rid>/native 与包旁路径探测（对齐 CozoNative 的加载策略）。
   - `scripts/build-native-parsers.sh`：clone 固定 commit（pin 写脚本内）→ cc 编译 → 拷入 runtimes/osx-arm64/native（其余 rid 留接口）。本 track 把已验证的 3 个 arm64 dylib 直接入库。
3. **per-language extractor（query 驱动）**
   - `CSharpExtractor` / `TypeScriptExtractor`：.scm query 常量（class/interface/struct/enum/record/delegate/method/constructor/property/field + base_list 继承 + using/import）；从 capture 构造 ParsedSymbol 树与结构边。
   - kind 映射到 v2 节点 kind 枚举；sym_key 生成：`(lang, 命名空间/嵌套限定名, arity)`（方法带参数个数，类型 arity=0）。
   - EXTENDS vs IMPLEMENTS：C# base_list 无语义区分时，目标名以 I 开头 + 大写第二字母的启发式判 IMPLEMENTS，否则 EXTENDS，confidence 相应降低（0.7），并在 evidence 记 heuristic；TS extends/implements 语法可区分（confidence 0.9）。
4. **Indexer 集成**
   - RepositoryIndexer：per-file 选后端；native 可用且语言支持 → ParsedFileResult 转 CodeSymbolFact/CodeEdgeFact（resolver="treesitter"，结构边 confidence=0.9，启发式边 0.7）；否则走既有 regex 路径（resolver="regex"，0.3）。
   - IMPORTS 边保持现有文件级解析（regex import 解析仍可用）与 tree-sitter import capture 合并去重（同 key 高 confidence 胜）。
   - 符号 id 规则沿用现有 `sym:` 前缀约定，保证工具面 id 兼容。

## 影响范围与修改点（Impact）

- packages/Cozo.DotNet.LlmWiki.SemanticParsing/*（新增 native binding、契约、extractor、runtimes、脚本）
- packages/Cozo.DotNet.LlmWiki.Indexing/RepositoryIndexer.cs（主路径切换）
- tests（extractor 固定样例单测 + dogfood 集成断言）

## 决策摘要

- D1（mission 已定）：tree-sitter 统一基线；CLI adapter 降级后端。
- ED（本 track）：EXTENDS/IMPLEMENTS 启发式区分（C#），G4 Roslyn 增强时纠正；dylib 只交付 osx-arm64。

## 风险 / 权衡

- grammar ABI 漂移 → commit pin + 启动断言 + findings 记录版本。
- query 覆盖不全导致漏符号 → dogfood 计数门禁（≥ regex 版）+ 固定样例单测。
- native crash 风险 → 后端选择器降级链，解析失败 per-file 降级 regex 不炸整个索引。

## 兼容性设计

- 无 grammar 语言、native 加载失败：行为与现状完全一致（regex）。
- parser_status/parse_file 工具语义保留（报告后端可用性时增加 native 信息，向后兼容字段只增不删）。
