# Decisions

## Usage
- 执行期决策追加至此

## #1 V-F2 纯委托豁免的呈现口径（T2.1）
- 备选：(a) 豁免方法降 confidence≤0.3 物化 INFO 类 violation；(b) 静默跳过不物化，verdict/诊断带豁免计数；(c) 静默跳过，仅注释与 rule-map 记录语义。
- 取 (c)：detector 的 RuleOutcome.Verdict 是纯枚举（GAP/PASS/BLOCKED），findings 无 per-rule PASS message 通道；为带计数而扩 verdict 结构或物化低置信实体都超出"豁免"语义（proposal 明言取实现简单诚实者）。豁免语义与弱化近似完整记录于 rubrics/rule-map.md F4 行 + DetectF2 代码注释。
- 近似口径（B-3"单表达式委托"）：出边 CALLS==1 且无 write ACCESSES → 豁免；read ACCESSES 不参与判定（ck_* 粒度下读转发与读后转发不可分辨），多 CALLS 或含写访问仍报。
