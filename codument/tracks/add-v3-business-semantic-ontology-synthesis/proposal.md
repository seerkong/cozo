# Proposal: v3 业务语义本体归纳

## 背景

v0、v1、v2 的主要产物仍以确定性的代码符号投影为骨架。v2 虽然成功调用了 Codex 并写出 pending candidates，却没有把实现证据收敛为业务概念；其 canonical 输出保留了大量类型，同时没有 canonical 关系、规则或生命周期。这个结果只能证明候选隔离与模型调用链可工作，不能证明系统理解了业务。

本 track 最初尝试在仓库内建设 v3 语义归纳。但真实数据库实验表明，仓库内的受限 completion 只接收到证据标识和实现定位信息，既不能阅读业务证据，也不应在这里维护提示词与分析策略。

修订后的方向是：本仓库提供业务语义 investigation 数据面和 CLI；外部工具维护提示词、模型、agent loop、语义归纳策略和人工审核。外部分析器通过稳定的层级 CLI 路由读取有界事实，而不是由本仓库替它作业务判断。

## 目标

- 以层级 `investigate <resource> <action>` CLI 暴露业务域、跨层用例、状态规则、实现锚点、语义模式和证据的只读有界查询。
- 保留 flag 形式的 query 参数；新增 `--json`，支持 `--json -` 从标准输入接收多行 JSON body，并让显式 flag 覆盖 body 中同名字段。
- 让外部分析器可以将查询结果、原始证据和其自身的提示词/模型策略组合为业务语义结论。
- 保证 CLI 不提供任意 SQL、任意文件路径或写入 accepted ontology 的快捷入口。

## 非目标

- 不在本仓库维护业务分析提示词、模型 provider、Codex CLI 路由、agent loop、critic 或语义 candidate 发布策略。
- 不把每个类、DTO、路由、表或前端组件视为一个业务类型。
- 不提供任意 SQL、任意文件读取或写入 accepted `onto_*` 的 investigation 参数。
- 不绑定任何特定用户目录、目标仓库目录或制品目录；这些由运行时配置提供。

## 成功标准

- 外部工具可用稳定、可扩展的资源/动作路径读取业务调查结果，例如 `investigate domains discover` 和 `investigate evidence get`。
- CLI 同时支持 flags 与对象型 JSON body；`--json -` 可安全接收 heredoc 等多行输入，并保留明确的输入大小和对象结构边界。
- 所有 investigation 路由都是只读、有界、确定排序且不接受任意 SQL、路径或 prompt。
- fixture 覆盖路由解析、JSON body、flag 覆盖、非法 body 拒绝和既有 `call` 兼容性。
- 为显式领域术语提供可视化消费的分层拓扑：节点权重由可解释的连接度与证据数量导出，图只表达调查事实，不替代外部语义判断。
- 对真实 IT Asset 后端和前端数据库仅在副本上重建缺失的 semantic claims，并通过 CLI 记录事实、跨端差异和证据缺口。
