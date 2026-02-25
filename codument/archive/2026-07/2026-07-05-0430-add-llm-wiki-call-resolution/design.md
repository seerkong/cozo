# 方案设计：统一调用消解（tree-sitter 基线）

## 上下文

G3 产物：extractor 输出 ParsedSymbol 树 + 结构边；RepositoryIndexer 主路径 tree-sitter。本 track 加"调用点提取 → 注册表消解 → 语义边"后处理链。GitNexus 参照：scope-resolution 的 registry-primary 模式（不复刻 fixpoint，只做分层匹配）。

## 方案概览

1. **契约扩展**（SemanticParsing）
   - ParsedFileResult 增加 `CallSites: IReadOnlyList<ParsedCallSite>`（add-only）。
   - `ParsedCallSite`：callerQualified（所在符号限定名）、targetName、receiverText（可空：`repo.Save()` 的 `repo`、`this`、类型名）、arity、kind（call|new|access）、accessMode（read|write|null）、line。
   - C# query 捕获 invocation_expression（member_access/identifier 两形态）、object_creation_expression、assignment 左侧 member_access（write access）；TS 捕获 call_expression/new_expression/member_expression。
2. **符号注册表**（Indexing，索引期内存构建）
   - 三层索引：byQualified（sym_key 去 arity）、byName+arity、byName；记录每符号的 fileId 与容器类型。
   - 局部绑定表（per caller 作用域，尽力）：`var x = new T()` / `const x = new T()` / 字段与属性声明类型 → receiver→类型映射（文本级，单文件内）。
3. **消解流水（per call site）**
   - kind=new：targetName 剥泛型 → 类型符号 → constructor 子符号（arity 匹配优先）或类型本身；conf 0.9（唯一）/0.7。
   - receiver=this/无 receiver：caller 容器类型成员 → EXTENDS 链向上；conf 0.9。
   - receiver 在局部绑定表 → 该类型成员匹配 name+arity；conf 0.9（唯一）。
   - receiver 是仓内类型名（静态调用）→ 该类型成员；conf 0.9/0.7。
   - 兜底：import 约束文件集合内 name+arity 唯一 0.7；仓内唯一 0.7；多候选选同名第一个 0.5 + evidence `ambiguous:<n>`；无候选 → 计数不落边。
   - ACCESSES：receiver 可消解才落边，evidence `read`/`write`，per-file 预算（默认 200）+ 全局开关（IndexRequest add-only：`EmitAccessEdges=true`、`MaxAccessEdgesPerFile=200`）。
4. **METHOD_OVERRIDES / METHOD_IMPLEMENTS**（索引后处理）
   - 遍历 EXTENDS/IMPLEMENTS 边：子类型成员与父类型成员 name+arity 匹配 → OVERRIDES（EXTENDS 目标）/IMPLEMENTS（IMPLEMENTS 目标）；conf 沿用继承边 confidence。
5. **落图**：全部经既有 CodeEdgeFact 通道（resolver=treesitter）；索引 summary 增加 callSites/resolved/unresolved 计数（add-only）。

## 影响范围与修改点（Impact）

- SemanticParsing：ISemanticParserBackend.cs（ParsedCallSite）、两个 extractor、query 常量。
- Indexing：CallResolver（新文件）+ RepositoryIndexer 接线。
- Core：RepositoryIndexRequest/Summary add-only 字段。
- tests：消解用例（固定小仓样例）+ dogfood 计数。

## 决策摘要

- 沿用 mission D1/A1（confidence 口径）；ambiguous 记 0.5 不丢弃（宁标注不静默丢）。
- 局部绑定表只做文本级单文件推导，不做跨过程数据流（Roslyn track 会覆盖 C# 的精确场景）。

## 风险 / 权衡

- 同名方法误连（0.5 档噪声）→ evidence 标注 + 下游 minConfidence 过滤已就位（G2）。
- ACCESSES 膨胀 → 预算 + 开关；默认开但受限。
- TS 动态调用（回调/解构）漏检 → 记 unresolved 计数，不追求全覆盖。

## 最小公开面（上两个 track 的 ED-1 教训）

CallResolver 及其内部注册表全部 internal；对外只经 RepositoryIndexer 现有 API 与 Core 的 add-only 请求字段。测试走 InternalsVisibleTo（两 csproj 均已有）。
