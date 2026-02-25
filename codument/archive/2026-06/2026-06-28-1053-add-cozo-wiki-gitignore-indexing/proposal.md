# 变更：cozo-wiki repo indexing 支持 .gitignore

## 背景和动机

构建 repo DB 时，如果遍历到 `node_modules`、构建产物、日志等目录，索引会变慢、污染知识图谱，并可能误导 agent 查询。虽然当前索引器已有硬编码目录排除，但用户希望支持读取项目自己的 `.gitignore`，让 repo DB 构建遵循项目本身的忽略配置。

## 目标

- `RepositoryIndexer` 默认读取 repository root 下的 `.gitignore`。
- 遍历文件时跳过 `.gitignore` 匹配的文件和目录。
- 保留现有硬编码安全排除目录。
- 补测试覆盖 `node_modules/` 和文件 glob。

## 非目标

- 不实现完整 Git pathspec 的所有边界语义。
- 不读取每个子目录的嵌套 `.gitignore`。
- 不调用外部 `git check-ignore`，保持跨环境可用。

## 影响范围

- `Cozo.DotNet.LlmWiki.Core`
- `Cozo.DotNet.LlmWiki.Indexing`
- `Cozo.DotNet.LlmWiki.Tests`
- `cozo-lib-dotnet-llm-wiki/README.md`
