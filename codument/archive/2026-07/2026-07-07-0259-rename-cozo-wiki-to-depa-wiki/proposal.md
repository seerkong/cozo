# 变更：产品对外身份改名 cozo-wiki → depa-wiki（硬切换）

## 背景和动机 (Context And Why)

用户指示：生成的二进制产物、配置文件等由 cozo-wiki 改为 depa-wiki。三项范围/兼容决策已确认（decisions.md）：全部对外标识改名、存量硬切换、不留旧命令别名。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 二进制名：AssemblyName cozo-wiki → depa-wiki（usage/帮助/错误文案同步）。
- 数据/配置目录：默认 data-folder-name `.cozo-wiki` → `.depa-wiki`（含 db 路径、docs-preview 等派生路径文案）。
- hooks：安装命令行与 own-entry 标记 "cozo-wiki hook" → "depa-wiki hook"；uninstall/status 只认新标记（硬切换）。
- skills：生成目录前缀 `cozo-wiki-*` → `depa-wiki-*`，own-prefix 安全边界同步。
- MCP server 名与示例：手册/eval 配置 `mcp_servers.cozo_wiki` → `mcp_servers.depa_wiki`。
- 文档全同步：用户手册 8 文件、docs/ 分形树、.gitignore（.cozo-wiki/ → .depa-wiki/）。
- 本机存量切换：~/.local/bin 符号链接、仓根 hooks 重装、`.depa-wiki` 重建索引（父层执行）。

**非目标:**
- 不改 .NET 包名/命名空间/目录名（Cozo.DotNet.LlmWiki.* 不动）。
- 不留 cozo-wiki 命令别名、不做 .cozo-wiki 自动回退/迁移（旧库废弃重建）。
- 不改 codument/ 归档与 memory 中的历史记述。

## 变更内容（What Changes）

- **BREAKING**：命令名、数据目录、hooks 标记、skills 前缀全部换名，旧安装失效需重装。
- packages/{McpServer,Tools,Wiki,Server}/ 中对外字符串；csproj AssemblyName；tests；cozo-lib-dotnet-llm-wiki/docs/ + README + eval/prompts,tasks；docs/ 分形树；.gitignore。

## 影响范围（Impact）

- 受影响的能力（behaviors）：llm-wiki-identity（新，对外命名与安全边界）。
- 受影响的代码：cozo-lib-dotnet-llm-wiki 全包字符串层 + 文档 + 本机安装。
