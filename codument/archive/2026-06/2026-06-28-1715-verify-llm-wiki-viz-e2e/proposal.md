# 变更：补齐 cozo-wiki viz 文档与最终验证

## 背景和动机 (Context And Why)

server 和 frontend 已实现，需要把用户可执行的使用方式写入 README，并用一轮端到端 smoke 收口 mission。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**

- README 说明 `cozo-wiki --serve`、Vite dev、production static serving。
- 运行 dotnet build/test、frontend build、server static smoke。
- 写 mission completion report。

**非目标:**

- 不新增更复杂的 Playwright E2E。
- 不继续扩展 UI 功能。

## 变更内容（What Changes）

- 更新 `cozo-lib-dotnet-llm-wiki/README.md`。
- 记录最终验证证据。

## 影响范围（Impact）

- 受影响的能力（behaviors）：`llm-wiki-viz-docs`
- 受影响的代码/文档：`cozo-lib-dotnet-llm-wiki/README.md`、mission reports。
