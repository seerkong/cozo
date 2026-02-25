# 方案设计：trace 与 check 工具

## 上下文

P0 产物：call_graph/import_graph 投影常量（CodeGraphProjections，$min_confidence 参数化）；DetectImportCyclesAsync internal 实现（SCC）；ShortestPathDijkstra 固定规则在 cozo-core 已存在（G1 查证）。工具面模式：LlmWikiToolRunner 单实现 → MCP/CLI/HTTP。

## 方案概览

1. **TraceCallPathAsync**（Om.CodeKnowledge public）
   - 语法：`?[from, to, path, cost] <~ ShortestPathDijkstra(call_graph[], starting[], goals[])`（以 cozo 实际固定规则签名为准，starting/goals 单元素）；边权默认 1（跳数最短）。
   - MaxDepth：path 长度 > MaxDepth+1 时按无路径处理（或用带深度限制的 Datalog 递归替代——实现取简单正确者，记 findings）。
   - hop 装配：path 符号 id 顺序 → 逐对查 ck_edge 取 (file,line,kind,confidence)（同对多调用点取 confidence 最高的一条）→ 查 ck_symbol 补 name/file。
   - TraceOptions(MaxDepth=16, MinConfidence=0.7)；TraceResult(Hops, Found, Reason)。
   - Direction：down=from→to 沿 CALLS 方向；up 就交换 from/to（不做反向投影）。
2. **cycles 正式化**：DetectImportCyclesAsync → public `DetectCyclesAsync(CycleKind kind = Import|Calls|Both)`；Calls 环用 call_graph 投影喂 SCC；输出环成员按最小 id 起点规范化旋转（确定性）。旧 internal 方法删除。
3. **工具面**（Tools）
   - `trace`：参数 from/to（id 或名称）、maxDepth、minConfidence；名称消歧：精确 name 匹配唯一 → 用之；多候选 → 返回 candidates 列表（id/name/kind/file/line）+ found=false；0 候选 → 提示。输出 hops（file:line 可点击格式 path:line）。
   - `check`：参数 cycles=import|calls|both；输出 cycles 列表（成员 id/name/file）。
   - 工具注册（ToolsJson schema）+ CLI call 支持自动获得。
4. **smoke**：小样例仓构造已知链/环 + 空环负例；真实仓 trace 一条已知链（tests fixture 内的）。

## 影响范围与修改点（Impact）

- Om.CodeKnowledge：ProcessExtraction/CodeGraphProjections 同级新增 TraceQueries（或并入 CodeGraphProjections）；models add-only。
- Tools：LlmWikiToolRunner 注册 2 工具；tests。

## 决策摘要

- 深度限制实现方式（固定规则 vs Datalog 递归）由实现实测定，记 findings（ED 候补）。
- up 方向 = 交换端点（不加反向投影，保持投影面最小）。

## 风险 / 权衡

- Dijkstra 无深度参数 → 结果后过滤（路径通常短，可接受）。
- 名称消歧歧义 → 返回候选而非猜测（agent 友好）。

## 最小公开面

public：TraceCallPathAsync + DetectCyclesAsync + options/result records（design 清单为准）；其余 internal。
