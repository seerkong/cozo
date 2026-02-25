# Decisions

## Usage
- 记录需确认的决策、选项、结论与理由；执行期新决策继续追加。

### 1. 【P0】分形管线的对外名称
- 背景：用户指示"wiki 子命令名称不叫 fractal，而是叫 codument-fractal"。既有 `build_wiki --pipeline fractal` 已被测试/eval/文档/skill 引用。
- 选项：A) 全面改名，fractal 直接失效 B) codument-fractal 为规范名，fractal 保留静默兼容别名 C) 只在 wiki 子命令用新名，build_wiki 维持 fractal
- 用户答复：命名=codument-fractal（明示）；别名策略未明示。
- 最终决策：B——wiki 子命令与 build_wiki schema/文档一律以 `codument-fractal` 为规范名；`fractal` 作为兼容别名继续被接受（不出现在 schema 描述与手册正文，仅代码注释说明），结果 JSON 的 pipeline 字段统一回显 `codument-fractal`。
- 决策理由：满足命名要求且不破坏既有脚本/测试；回显统一避免双名混淆。
- 状态：assumed（light；用户可评审时改 A/C）

### 2. 【P1】W-2 重要性排序的具体准则
- 选项：A) public-API 成员数降序 → 总成员数降序 → 名称升序 B) 包层级深度优先 C) misc 容器归并
- 最终决策：A。public-API 密度直接对应"对外重要性"（JS demo 微社群 public=0 自然沉底，Om.* 核心 namespace 上浮）；包层级与 misc 容器留给 W-3。
- 状态：assumed
