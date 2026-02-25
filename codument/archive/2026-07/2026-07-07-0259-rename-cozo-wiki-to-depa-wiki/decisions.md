# Decisions

### 1. 【P0】改名范围
- 用户答复（AskUserQuestion 2026-07-06）：二进制名 + 数据/配置目录 + hooks 标识与安装命令 + skills 前缀 + MCP server 名与手册示例；.NET 包/命名空间不改。
- 状态：confirmed

### 2. 【P0】存量兼容
- 用户答复：硬切换——只认 .depa-wiki，旧 .cozo-wiki 不回退不自动迁移（手动重建索引）；本机符号链接/hooks 由父层重装。
- 状态：confirmed

### 3. 【P0】旧命令别名
- 用户答复：不保留，彻底改名。
- 状态：confirmed

### 4. 【P1】内部标识符处理（规划者裁量）
- C# 类型名如 CozoWikiStorageOptions、变量名、注释属内部实现，不在"对外标识"范围——本轮仅在顺手处更新注释文案，不做类型重命名（避免无谓 churn；与"不改包名"口径一致）。docs-preview 等 .depa-wiki 下派生路径随目录名走。
- 状态：assumed
