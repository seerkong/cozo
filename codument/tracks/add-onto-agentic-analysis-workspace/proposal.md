# Proposal: Agentic 本体分析工作区与 provenance

建立独立的 `onto_analysis_*` 工作区，记录受控 agent run、observation、hypothesis、conflict、gap 与 candidate draft，以及每项引用的 G1 query digest/evidence id。该工作区 append-only，物理上不复用或改写 accepted `onto_*` generation；只有后续 G4 的本地验证导出可把已验证 draft 转换成候选 XML。
